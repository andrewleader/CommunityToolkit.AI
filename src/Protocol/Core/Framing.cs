using System.Buffers.Binary;

namespace CommunityToolkit.AI.Protocol;

/// <summary>
/// Length-prefixed frames: a 4-byte little-endian length followed by a UTF-8 JSON envelope.
/// </summary>
internal static class Framing
{
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        if (body.Length > MaxFrameBytes)
        {
            throw new InvalidDataException($"Frame of {body.Length} bytes exceeds the {MaxFrameBytes} byte limit.");
        }

        var buffer = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, body.Length);
        body.CopyTo(buffer.AsMemory(4));
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one frame, or returns null on a clean end of stream.</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactlyOrEofAsync(stream, header, ct).ConfigureAwait(false))
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxFrameBytes)
        {
            throw new InvalidDataException($"Invalid frame length {length}.");
        }

        var body = new byte[length];
        if (!await ReadExactlyOrEofAsync(stream, body, ct).ConfigureAwait(false))
        {
            throw new EndOfStreamException("The stream ended in the middle of a frame.");
        }

        return body;
    }

    private static async Task<bool> ReadExactlyOrEofAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("The stream ended in the middle of a frame.");
            }

            read += n;
        }

        return true;
    }
}
