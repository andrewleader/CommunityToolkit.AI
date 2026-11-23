namespace CommunityToolkit.AI.Protocol;

/// <summary>
/// Contract version negotiation. Versions are "major.minor". Within a major, the lower minor wins.
/// Different majors are served side by side by the runtime.
/// </summary>
internal static class ContractVersions
{
    public static bool TryParse(string? text, out int major, out int minor)
    {
        major = minor = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var parts = text.Split('.');
        return parts.Length == 2 && int.TryParse(parts[0], out major) && int.TryParse(parts[1], out minor) && major >= 0 && minor >= 0;
    }

    /// <summary>
    /// Negotiates a version from the client's max and the versions the server supports (one entry per major, holding that major's max minor).
    /// </summary>
    /// <returns>The negotiated version, or null with an error code when no common major exists.</returns>
    public static string? Negotiate(string clientMax, IEnumerable<string> serverSupported, out string? errorCode)
    {
        errorCode = null;
        if (!TryParse(clientMax, out int cMajor, out int cMinor))
        {
            errorCode = ErrorCodes.InvalidRequest;
            return null;
        }

        int highestServerMajor = -1;
        foreach (var version in serverSupported)
        {
            if (!TryParse(version, out int sMajor, out int sMinor))
            {
                continue;
            }

            highestServerMajor = Math.Max(highestServerMajor, sMajor);
            if (sMajor == cMajor)
            {
                return $"{cMajor}.{Math.Min(cMinor, sMinor)}";
            }
        }

        // The client is newer than the runtime: the runtime must update. Otherwise the client's major was retired.
        errorCode = cMajor > highestServerMajor ? ErrorCodes.ProtocolTooOld : ErrorCodes.NotSupported;
        return null;
    }

    public static int Major(string version) => TryParse(version, out int major, out _) ? major : -1;
}
