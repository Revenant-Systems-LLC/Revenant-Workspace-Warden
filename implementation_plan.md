# Robustness Improvements for Revenant-Workspace-Warden

This plan outlines the steps to make the Workspace Warden more robust against common failures, edge cases, and runtime exceptions.

## User Review Required

> [!IMPORTANT]
> Since this modifies existing files, Dave's rules require explicit permission before I edit any files. Please review the proposed changes below and let me know if you approve.
>
> Additionally, note that I plan to change the storage path for `lesson_transcript.md` and `lesson_notes.md` from the executable directory to the user's `AppData\Local` folder to prevent permissions issues. Let me know if you'd prefer to keep them in the executable directory or move them to a different specific path.

## Proposed Changes

### Revenant-Workspace-Warden

#### [MODIFY] MainWindow.xaml.cs
- **Ollama Startup Synchronization**: `LoadOllamaModelsAsync` will be updated to include a retry loop. Currently, it fires immediately after `StartOllama()`, which often fails because the Ollama server takes a few seconds to boot up.
- **Clipboard Access Retry**: `ProcessClipboardReview` will use a retry mechanism (e.g., a loop with `Task.Delay`) when calling `Clipboard.GetText()`. The Windows clipboard is frequently locked by other apps, causing `ExternalException`s.
- **Large File Attachment Safeguard**: In `AttachBtn_Click`, I will add a file size check before reading the content to prevent `OutOfMemoryException` or hanging on massive files (e.g., > 5MB).
- **bdeunlock.exe Timeout**: The wait for `bdeunlock.exe` in `GetSecureGeminiApiKeyAsync` will be updated with a timeout (e.g., 60 seconds) so the app doesn't hang indefinitely if the prompt is ignored or stuck.
- **File Path Safety**: File paths for notes and chat sessions (`lesson_transcript.md`, `NotebookLM_Staging`, etc.) will be moved from `AppDomain.CurrentDomain.BaseDirectory` to a safe data directory like `Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)` to avoid UnauthorizedAccessExceptions if run from restricted folders.

#### [MODIFY] AudioTranscriber.cs
- **File Path Safety**: `lesson_transcript.md` path will be updated to match the safe data directory used in `MainWindow.xaml.cs`.
- **Exception Handling in Audio Queue**: Add specific catch blocks or logging for `_waveIn` exceptions in case the microphone gets disconnected mid-recording.

## Verification Plan

### Manual Verification
- Test audio transcription to ensure files are written to the correct new directory.
- Test attaching a large file (> 5MB) to verify the new size safeguard works.
- Verify Ollama models load successfully even if the server was not running before the app started.
- Copy text rapidly to trigger the clipboard review and ensure it doesn't crash on contention.
