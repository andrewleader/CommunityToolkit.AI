# Speech to text

!!! warning "Preview"
    The `CommunityToolkit.AI.SpeechToText` API and its task contract (0.1) are published so apps can start building against
    them. The Whisper adapter lands in sprint 8, and no speech model is in the catalog yet.

```csharp
using CommunityToolkit.AI.SpeechToText;

var model = SpeechToTextModel.FromCatalog("<speech-model-id>");
await model.EnsureReadyAsync();
await using SpeechToTextClient client = await model.GetClientAsync();

await using var wav = File.OpenRead("meeting.wav"); // 16 kHz mono 16-bit PCM
await foreach (TranscriptSegment segment in client.TranscribeStreamingAsync(wav))
{
    Console.WriteLine($"[{segment.Start:mm\\:ss}] {segment.Text}");
}
```
