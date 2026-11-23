# Uninstall

The runtime is per user and has a visible uninstall:

- **Settings > Apps > Installed apps > Local AI Runtime (CommunityToolkit.AI) > Uninstall**, or
- run `%LocalAppData%\CommunityToolkit\LocalAI\runtime\localai.cmd --uninstall`.

Uninstall stops the broker and every model host, removes the scheduled update task and the Apps entry, and deletes the
runtime, models, downloads, state and logs.

Apps that use the runtime install it again the next time they call `EnsureReadyAsync`.

To remove only models, delete folders under `%LocalAppData%\CommunityToolkit\LocalAI\models` while no app is using
them; the runtime also removes superseded model versions on its own.
