using System.Text.Json;
using CommunityToolkit.AI.Protocol.Ocr;

namespace CommunityToolkit.AI.Ocr;

/// <summary>A local OCR model from the catalog.</summary>
public sealed class OcrModel : Model
{
    private OcrModel(string id)
        : base(id, OcrContract.TaskId)
    {
    }

    /// <summary>Returns a handle to a catalog model. Does no I/O.</summary>
    public static OcrModel FromCatalog(string id) => new(id);

    /// <inheritdoc cref="FromCatalog(string)"/>
    public static OcrModel FromCatalog(string id, ModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new OcrModel(id) { Options = options };
    }

    /// <summary>Finds or starts the shared model host and returns a session-bound client.</summary>
    public async Task<OcrClient> GetClientAsync(CancellationToken ct = default) =>
        new(await ConnectToHostAsync(OcrContract.Version, ct).ConfigureAwait(false));
}

/// <summary>Recognizes text in images.</summary>
public sealed class OcrClient : IAsyncDisposable
{
    private const int MaxImageBytes = 32 * 1024 * 1024;
    private readonly ModelHostConnection _connection;

    internal OcrClient(ModelHostConnection connection) => _connection = connection;

    /// <summary>Recognizes text in an encoded image (PNG, JPEG or BMP).</summary>
    public async Task<OcrResult> RecognizeAsync(Stream image, OcrOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var buffer = new MemoryStream();
        await image.CopyToAsync(buffer, ct).ConfigureAwait(false);
        if (buffer.Length > MaxImageBytes)
        {
            throw new ArgumentException($"Images larger than {MaxImageBytes} bytes are not supported.", nameof(image));
        }

        var request = new OcrRecognizeRequest { Image = Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length), Language = options?.Language };
        var result = await _connection.InvokeAsync(OcrContract.Recognize, JsonSerializer.SerializeToElement(request, OcrJsonContext.Default.OcrRecognizeRequest), ct).ConfigureAwait(false);
        var wire = result?.Deserialize(OcrJsonContext.Default.OcrRecognizeResult) ?? new OcrRecognizeResult();
        return new OcrResult(wire.Lines.Select(l => new OcrLine(l.Text, new OcrBounds(l.X, l.Y, l.Width, l.Height), l.Confidence)).ToList());
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}

public sealed class OcrOptions
{
    /// <summary>A BCP-47 language hint, for example en-US.</summary>
    public string? Language { get; set; }
}

/// <summary>A box in image pixel coordinates.</summary>
public readonly record struct OcrBounds(float X, float Y, float Width, float Height);

public sealed record OcrLine(string Text, OcrBounds Bounds, float Confidence);

public sealed record OcrResult(IReadOnlyList<OcrLine> Lines)
{
    public string Text => string.Join(Environment.NewLine, Lines.Select(l => l.Text));
}
