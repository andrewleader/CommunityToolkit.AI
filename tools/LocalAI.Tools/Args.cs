namespace CommunityToolkit.AI.Tools;

internal sealed class ToolException(string message) : Exception(message);

/// <summary>Positional arguments plus --name value options (repeatable).</summary>
internal sealed class Args
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);

    public Args(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                string name = args[i][2..];
                string value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
                if (!_options.TryGetValue(name, out var list))
                {
                    _options[name] = list = [];
                }

                list.Add(value);
            }
            else
            {
                Positional.Add(args[i]);
            }
        }
    }

    public List<string> Positional { get; } = [];

    public string Required(string name) => Optional(name) ?? throw new ToolException($"--{name} is required.");

    public string? Optional(string name) => _options.TryGetValue(name, out var list) ? list[^1] : null;

    public bool Flag(string name) => Optional(name) is "true";

    public int Int(string name, int fallback) => Optional(name) is { } v ? int.Parse(v) : fallback;

    public long Long(string name, long fallback) => Optional(name) is { } v ? long.Parse(v) : fallback;

    public List<string> List(string name) =>
        _options.TryGetValue(name, out var list) ? list.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList() : [];

    public List<string> All(string name) => _options.TryGetValue(name, out var list) ? list : [];

    /// <summary>Reads a secret from a file, or from an environment variable when given as env:NAME.</summary>
    public static string ReadSecret(string source)
    {
        if (source.StartsWith("env:", StringComparison.Ordinal))
        {
            return Environment.GetEnvironmentVariable(source[4..])?.Trim() ?? throw new ToolException($"Environment variable {source[4..]} is not set.");
        }

        return File.ReadAllText(source).Trim();
    }
}
