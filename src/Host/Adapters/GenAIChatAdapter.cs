using System.Text;
using System.Text.Json;
using CommunityToolkit.AI.Protocol;
using CommunityToolkit.AI.Protocol.Chat;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace CommunityToolkit.AI.Host.Adapters;

/// <summary>
/// Chat on ONNX Runtime GenAI. Each session keeps its own generator (KV cache); a new turn rewinds to the longest
/// common token prefix and only appends the new tokens.
/// </summary>
internal sealed class GenAIChatAdapter : IChatAdapter
{
    public const string Id = "ort-genai-chat";
    private const int DefaultMaxLength = 4096;
    private readonly Model _model;
    private readonly Tokenizer _tokenizer;
    private readonly int _maxLength;
    private readonly int _maxCachedSessions;
    private readonly string? _template;
    private readonly HashSet<int> _eos;
    private readonly LinkedList<GenSession> _lru = new();
    private readonly object _lruLock = new();

    public GenAIChatAdapter(ModelInfo model)
    {
        string dir = model.Parameters.TryGetValue("subfolder", out var sub) && sub.Length > 0 ? Path.Combine(model.Directory, sub) : model.Directory;
        using var config = new Config(dir);
        config.ClearProviders();
        if (model.Ep != "cpu")
        {
            config.AppendProvider(model.Ep);
        }

        _model = new Model(config);
        _tokenizer = new Tokenizer(_model);
        _maxLength = model.Parameters.TryGetValue("maxLength", out var ml) && int.TryParse(ml, out int maxLength) ? maxLength : DefaultMaxLength;
        _maxCachedSessions = model.Parameters.TryGetValue("maxCachedSessions", out var mc) && int.TryParse(mc, out int maxCached) ? maxCached : 4;
        _template = model.Parameters.TryGetValue("chatTemplate", out var template) && template.Length > 0 ? template : null;
        _eos = [.. _tokenizer.GetEosTokenIds().ToArray()];
    }

    public string Task => ChatContract.TaskId;

    public IDisposable? CreateSession() => new GenSession(this);

    public ChatCompletionResult Generate(IDisposable? sessionObject, ChatCompletionRequest request, Action<string> onText, CancellationToken ct)
    {
        var session = (GenSession)(sessionObject ?? throw new InvalidOperationException("No session."));
        string prompt = BuildPrompt(request.Messages);
        int[] promptTokens;
        using (var sequences = _tokenizer.Encode(prompt))
        {
            promptTokens = sequences[0].ToArray();
        }

        var options = request.Options ?? new ChatWireOptions();
        int maxNew = Math.Min(options.MaxOutputTokens ?? int.MaxValue, _maxLength - promptTokens.Length);
        if (maxNew <= 0)
        {
            throw new RpcException(ErrorCodes.InvalidRequest, $"The conversation is {promptTokens.Length} tokens, which leaves no room in the {_maxLength}-token context.");
        }

        lock (session)
        {
            var generator = session.Prepare(options, promptTokens);
            Touch(session);
            using var stream = _tokenizer.CreateStream();
            var text = new StringBuilder();
            int produced = 0;
            string finish = "stop";
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (generator.IsDone())
                {
                    finish = generator.TokenCount() >= (ulong)_maxLength ? "length" : "stop";
                    break;
                }

                if (produced >= maxNew)
                {
                    finish = "length";
                    break;
                }

                generator.GenerateNextToken();
                int token = generator.GetNextTokens()[0];
                produced++;
                if (_eos.Contains(token))
                {
                    break;
                }

                string piece = stream.Decode(token);
                if (piece.Length == 0)
                {
                    continue;
                }

                if (options.StopSequences is { Count: > 0 } stops)
                {
                    text.Append(piece);
                    string all = text.ToString();
                    int hit = stops.Select(s => all.IndexOf(s, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
                    if (hit >= 0)
                    {
                        int emitted = all.Length - piece.Length;
                        if (hit > emitted)
                        {
                            onText(all[emitted..hit]);
                        }

                        break;
                    }
                }

                onText(piece);
            }

            return new ChatCompletionResult { FinishReason = finish, InputTokens = promptTokens.Length, OutputTokens = produced };
        }
    }

    private string BuildPrompt(List<ChatWireMessage> messages)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var message in messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", message.Role);
                writer.WriteString("content", message.Text);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        string json = Encoding.UTF8.GetString(buffer.ToArray());
        try
        {
            return _tokenizer.ApplyChatTemplate(_template!, json, null!, true);
        }
        catch (Exception ex) when (ex is OnnxRuntimeGenAIException or ArgumentException or NullReferenceException)
        {
            // Models without a chat template fall back to ChatML.
            var sb = new StringBuilder();
            foreach (var m in messages)
            {
                sb.Append("<|im_start|>").Append(m.Role).Append('\n').Append(m.Text).Append("<|im_end|>\n");
            }

            return sb.Append("<|im_start|>assistant\n").ToString();
        }
    }

    private void Touch(GenSession session)
    {
        lock (_lruLock)
        {
            if (session.Node is not null)
            {
                _lru.Remove(session.Node);
            }

            session.Node = _lru.AddFirst(session);
            while (_lru.Count > _maxCachedSessions)
            {
                var oldest = _lru.Last!.Value;
                _lru.RemoveLast();
                oldest.Node = null;
                if (Monitor.TryEnter(oldest))
                {
                    try
                    {
                        oldest.DropCache();
                    }
                    finally
                    {
                        Monitor.Exit(oldest);
                    }
                }
            }
        }
    }

    private void Forget(GenSession session)
    {
        lock (_lruLock)
        {
            if (session.Node is not null)
            {
                _lru.Remove(session.Node);
                session.Node = null;
            }
        }
    }

    public void Dispose()
    {
        _tokenizer.Dispose();
        _model.Dispose();
    }

    private sealed class GenSession(GenAIChatAdapter owner) : IDisposable
    {
        private Generator? _generator;
        private GeneratorParams? _params;
        private string? _paramsKey;

        public LinkedListNode<GenSession>? Node { get; set; }

        public Generator Prepare(ChatWireOptions options, int[] promptTokens)
        {
            string key = $"{options.Temperature}|{options.TopP}|{options.TopK}|{options.Seed}";
            if (_generator is null || key != _paramsKey)
            {
                Create(options, key);
            }

            int[] cached = _generator!.GetSequence(0).ToArray();
            int common = 0;
            while (common < cached.Length && common < promptTokens.Length && cached[common] == promptTokens[common])
            {
                common++;
            }

            // At least one token must be appended to produce new logits.
            common = Math.Min(common, promptTokens.Length - 1);
            try
            {
                if (common == 0 && cached.Length > 0)
                {
                    Create(options, key);
                }
                else if (common < cached.Length)
                {
                    _generator.RewindTo((ulong)common);
                }

                _generator!.AppendTokens(promptTokens.AsSpan(common));
            }
            catch (OnnxRuntimeGenAIException)
            {
                Create(options, key);
                _generator!.AppendTokens(promptTokens);
            }

            return _generator!;
        }

        private void Create(ChatWireOptions options, string key)
        {
            DropCache();
            _params = new GeneratorParams(owner._model);
            _params.SetSearchOption("max_length", owner._maxLength);
            bool sample = options.Temperature is > 0 || options.TopP is not null || options.TopK is not null;
            _params.SetSearchOption("do_sample", sample);
            if (options.Temperature is { } temperature)
            {
                _params.SetSearchOption("temperature", Math.Max(temperature, 1e-3));
            }

            if (options.TopP is { } topP)
            {
                _params.SetSearchOption("top_p", topP);
            }

            if (options.TopK is { } topK)
            {
                _params.SetSearchOption("top_k", topK);
            }

            if (options.Seed is { } seed)
            {
                _params.SetSearchOption("random_seed", seed);
            }

            _generator = new Generator(owner._model, _params);
            _paramsKey = key;
        }

        public void DropCache()
        {
            _generator?.Dispose();
            _params?.Dispose();
            _generator = null;
            _params = null;
            _paramsKey = null;
        }

        public void Dispose()
        {
            owner.Forget(this);
            lock (this)
            {
                DropCache();
            }
        }
    }
}
