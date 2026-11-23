namespace CommunityToolkit.AI.Protocol.Runtime;

/// <summary>Minimal file logger for the broker and hosts. Never logs prompts, audio or images.</summary>
internal static class Log
{
    private static readonly object s_lock = new();
    private static string? s_path;
    private static string s_component = "";

    public static void Initialize(string logsDir, string component)
    {
        Directory.CreateDirectory(logsDir);
        s_component = component;
        s_path = Path.Combine(logsDir, $"{component}-{DateTime.UtcNow:yyyyMMdd}.log");
        Prune(logsDir, TimeSpan.FromDays(14));
    }

    public static void Info(string message) => Write("INF", message);

    public static void Warn(string message) => Write("WRN", message);

    public static void Error(string message, Exception? ex = null) => Write("ERR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.UtcNow:O} [{level}] {s_component}[{Environment.ProcessId}] {message}{Environment.NewLine}";
        if (Environment.GetEnvironmentVariable("LOCALAI_LOG_CONSOLE") == "1")
        {
            Console.Error.Write(line);
        }

        if (s_path is null)
        {
            return;
        }

        lock (s_lock)
        {
            try
            {
                File.AppendAllText(s_path, line);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void Prune(string dir, TimeSpan age)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.log"))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > age)
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
