namespace CommunityToolkit.AI.Protocol;

internal static class DiskSpace
{
    /// <summary>Throws InsufficientDisk unless the volume holding <paramref name="path"/> has room for the bytes plus a safety margin.</summary>
    public static void EnsureAvailable(string path, long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
        long available;
        try
        {
            available = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return;
        }

        long margin = Math.Max(256L * 1024 * 1024, bytes / 10);
        if (available < bytes + margin)
        {
            throw new RpcException(ErrorCodes.InsufficientDisk, $"Not enough disk space on {root}: {bytes + margin:N0} bytes needed, {available:N0} available.");
        }
    }
}
