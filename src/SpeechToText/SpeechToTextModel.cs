using System.Runtime.CompilerServices;
using System.Text.Json;
using CommunityToolkit.AI.Protocol.SpeechToText;

namespace CommunityToolkit.AI.SpeechToText;

/// <summary>A local speech-to-text model from the catalog.</summary>
public sealed class SpeechToTextModel : Model
{
    private SpeechToTextModel(string id)
        : base(id, SpeechToTextContract.TaskId)
    {
    }

    /// <summary>Returns a handle to a catalog model. Does no I/O.</summary>
    public static SpeechToTextModel FromCatalog(string id) => new(id);

    /// <inheritdoc cref="FromCatalog(string)"/>
    public static SpeechToTextModel FromCatalog(string id, ModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SpeechToTextModel(id) { Options = options };
    }

    /// <summary>Finds or starts the shared model host and returns a session-bound client.</summary>
    public async Task<SpeechToTextClient> GetClientAsync(CancellationToken ct = default) =>
        new(await ConnectToHostAsync(SpeechToTextContract.Version, ct).ConfigureAwait(false));
}

/// <summary>Transcribes audio.</summary>
public sealed class SpeechToTextClient : IAsyncDisposable
{
    private const int MaxAudioBytes = 256 * 1024 * 1024;
    private readonly ModelHostConnection _connection;

    internal SpeechToTextClient(ModelHostConnection connection) => _connection = connection;

    /// <summary>Transcribes a 16 kHz mono 16-bit PCM WAV stream and returns the whole transcript.</summary>
    public async Task<Transcript> TranscribeAsync(Stream wav, SpeechToTextOptions? options = null, CancellationToken ct = default)
    {
        var segments = new List<TranscriptSegment>();
        await foreach (var segment in TranscribeStreamingAsync(wav, options, ct).ConfigureAwait(false))
        {
            segments.Add(segment);
        }

        return new Transcript(string.Concat(segments.Select(s => s.Text)).Trim(), segments);
    }

    /// <summary>Transcribes a 16 kHz mono 16-bit PCM WAV stream, yielding segments as they are decoded.</summary>
    public async IAsyncEnumerable<TranscriptSegment> TranscribeStreamingAsync(Stream wav, SpeechToTextOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wav);
        using var buffer = new MemoryStream();
        await wav.CopyToAsync(buffer, ct).ConfigureAwait(false);
        if (buffer.Length > MaxAudioBytes)
        {
            throw new ArgumentException($"Audio larger than {MaxAudioBytes} bytes is not supported.", nameof(wav));
        }

        var request = new TranscribeRequest { Audio = Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length), Language = options?.Language };
        await using var stream = await _connection.StreamAsync(SpeechToTextContract.Transcribe, JsonSerializer.SerializeToElement(request, SpeechToTextJsonContext.Default.TranscribeRequest), ct).ConfigureAwait(false);
        await foreach (var evt in stream.ReadEventsAsync(ct).ConfigureAwait(false))
        {
            if (evt.Deserialize(SpeechToTextJsonContext.Default.TranscriptSegmentWire) is { } s)
            {
                yield return new TranscriptSegment(s.Text, TimeSpan.FromMilliseconds(s.StartMs), TimeSpan.FromMilliseconds(s.EndMs));
            }
        }

        await stream.GetResultAsync(ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}

public sealed class SpeechToTextOptions
{
    /// <summary>A language hint such as en. Null lets the model detect it.</summary>
    public string? Language { get; set; }
}

public sealed record TranscriptSegment(string Text, TimeSpan Start, TimeSpan End);

public sealed record Transcript(string Text, IReadOnlyList<TranscriptSegment> Segments);
