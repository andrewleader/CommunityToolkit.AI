# OCR

!!! warning "Preview"
    The `CommunityToolkit.AI.Ocr` API and its task contract (0.1) are published so apps can start building against them.
    The runtime's OCR adapter and the shared-memory data plane land in sprint 7, and no OCR model is in the catalog yet.
    Until then `EnsureReadyAsync` raises `ModelUnknownException` for OCR ids.

```csharp
using CommunityToolkit.AI.Ocr;

var model = OcrModel.FromCatalog("<ocr-model-id>");
await model.EnsureReadyAsync();
await using OcrClient client = await model.GetClientAsync();

await using var image = File.OpenRead("receipt.png");
OcrResult result = await client.RecognizeAsync(image, new OcrOptions { Language = "en-US" });
foreach (OcrLine line in result.Lines)
{
    Console.WriteLine($"{line.Text} at {line.Bounds}");
}
```

Input and result types (`OcrResult`, `OcrLine`, `OcrBounds`) belong to this package; they are not shared with other tasks.
