using Microsoft.Win32;

namespace CommunityToolkit.AI.Protocol;

/// <summary>
/// Enterprise policy values under HKCU or HKLM \Software\Policies\CommunityToolkit\LocalAI. HKLM wins.
/// </summary>
internal sealed class EnterprisePolicy
{
    public const string KeyPath = @"Software\Policies\CommunityToolkit\LocalAI";

    /// <summary>DWORD 1 disables runtime self-update.</summary>
    public bool DisableUpdates { get; init; }

    /// <summary>DWORD 1 disables telemetry.</summary>
    public bool DisableTelemetry { get; init; }

    /// <summary>String. Replaces the Pages root URL (manifests, catalog, runtime) with a mirror.</summary>
    public string? RootUrl { get; init; }

    /// <summary>String. Pins the update channel, for example stable.</summary>
    public string? Channel { get; init; }

    /// <summary>String. Rewrites https://huggingface.co/ in model URLs to an internal mirror. Hashes keep this safe.</summary>
    public string? HuggingFaceMirror { get; init; }

    public static EnterprisePolicy Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new EnterprisePolicy();
        }

        try
        {
            using var machine = Registry.LocalMachine.OpenSubKey(KeyPath);
            using var user = Registry.CurrentUser.OpenSubKey(KeyPath);
            object? Get(string name) => machine?.GetValue(name) ?? user?.GetValue(name);
            return new EnterprisePolicy
            {
                DisableUpdates = Get("DisableUpdates") is int u && u != 0,
                DisableTelemetry = Get("DisableTelemetry") is int t && t != 0,
                RootUrl = Get("RootUrl") as string,
                Channel = Get("Channel") as string,
                HuggingFaceMirror = Get("HuggingFaceMirror") as string,
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new EnterprisePolicy();
        }
    }
}
