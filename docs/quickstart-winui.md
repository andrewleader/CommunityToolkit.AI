# Quick start: WinUI app (packaged)

Packaged apps use exactly the same API. There is no package identity requirement and no capability to declare: the
runtime lives in the user's profile, and the app talks to it over a per-user named pipe.

```powershell
dotnet add package CommunityToolkit.AI.Chat --prerelease
```

```csharp
using CommunityToolkit.AI;
using CommunityToolkit.AI.Chat;
using Microsoft.Extensions.AI;

public sealed partial class MainWindow : Window
{
    private readonly ChatModel _model = ChatModel.FromCatalog("qwen-2.5-1.5b-instruct", new ModelOptions
    {
        LicenseAcceptance = (license, ct) => ShowLicenseDialogAsync(license, ct),
    });

    private async void OnAskClicked(object sender, RoutedEventArgs e)
    {
        // Progress is raised on the UI thread because Progress<T> captures the synchronization context.
        var progress = new Progress<EnsureProgress>(p => DownloadBar.Value = p.OverallFraction * 100);
        await _model.EnsureReadyAsync(progress);

        IChatClient client = await _model.GetClientAsync();
        Answer.Text = "";
        await foreach (var update in client.GetStreamingResponseAsync(Question.Text))
        {
            Answer.Text += update.Text;
        }
    }
}
```

Build your UX from `GetStatusAsync()`: for example show a "Download model (1 GB)" button when the status is
`NeedsModel`, and go straight to chat when it is `Ready`. The runtime never shows UI of its own.
