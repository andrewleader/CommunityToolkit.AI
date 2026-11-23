using CommunityToolkit.AI.Protocol;
using CommunityToolkit.AI.Protocol.Trust;

namespace CommunityToolkit.AI;

/// <summary>Process-wide settings for the Local AI client.</summary>
public static class LocalAIRuntime
{
    /// <summary>The production site root that hosts manifests, the catalog and the runtime.</summary>
    public const string DefaultRootUrl = "https://andrewleader.github.io/CommunityToolkit.AI/";

    /// <summary>The update channel. Enterprise policy overrides it.</summary>
    public static string Channel { get; set; } = "preview";

    /// <summary>
    /// Enables dev mode, which honours LOCALAI_DEV_ROOT_URL, LOCALAI_DEV_TRUST_KEY, LOCALAI_HOME and LOCALAI_CHANNEL.
    /// Off by default in release builds, so environment variables alone cannot redirect an app to another host.
    /// </summary>
    public static bool EnableDevMode { get; set; }
#if DEBUG
        = true;
#endif

    /// <summary>A name the runtime shows in logs for this app. Defaults to the process name.</summary>
    public static string? ClientName { get; set; }

    internal static ClientEnvironment ResolveEnvironment()
    {
        var policy = EnterprisePolicy.Read();
        bool dev = EnableDevMode;
        string? Env(string name) => dev ? Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null : null;

        var config = new RuntimeConfig
        {
            RootUrl = policy.RootUrl ?? Env("LOCALAI_DEV_ROOT_URL") ?? DefaultRootUrl,
            Channel = policy.Channel ?? Env("LOCALAI_CHANNEL") ?? Channel,
        };

        if (Env("LOCALAI_DEV_TRUST_KEY") is { } keys)
        {
            config.DevTrustKeys.AddRange(keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return new ClientEnvironment(new LocalAIPaths(Env("LOCALAI_HOME")), config, dev);
    }
}

internal sealed record ClientEnvironment(LocalAIPaths Paths, RuntimeConfig Config, bool DevMode)
{
    public TrustStore CreateTrustStore()
    {
        var roots = TrustRoots.Production.Select(Convert.FromBase64String).ToList();
        if (DevMode)
        {
            foreach (var key in Config.DevTrustKeys)
            {
                try
                {
                    roots.Add(Convert.FromBase64String(key));
                }
                catch (FormatException)
                {
                }
            }
        }

        return new TrustStore(roots);
    }
}
