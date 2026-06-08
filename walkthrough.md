# Revenant Workspace Warden - V1 Preparation Walkthrough

I have successfully completed the file modifications and deletions needed to prepare Warden for a V1 release as a sellable product. 

## 1. Voice Functionality Removed (Saves ~900MB)
* Deleted `AudioService.cs`, `AudioTranscriber.cs`, and `VoiceCommandHandler.cs`.
* Deleted `ggml-medium.en.bin`.
* Removed the Whisper.net and NAudio packages from `RevenantWorkspaceWarden.csproj`.
* Cleaned up `MainWindow.xaml.cs` and `MainWindow.xaml` to remove all voice command logic, mic toggling, and the "MIC" button from the UI.

## 2. Branding Assets Bundled
* Copied `RWW_SHIELD_alt2.png` and `RWW_wordmark_alt2.png` into the `Assets/` directory.
* Updated `MainWindow.xaml` to load these using robust, relative WPF Pack URIs (`pack://application:,,,/Assets/...`) rather than relying on absolute `C:\` drive paths. This ensures the branding will load correctly on any customer's machine.

## 3. Generic Username and Output Paths
* Modified `ChatSessionManager.cs` to dynamically use the current Windows user's name instead of hardcoding `Dave`.
* Changed the session save directory from the hidden local AppData folder to a much more accessible location: `Documents\RevenantWarden\ChatSessions`.

## 4. Customizable Tutor Profiles
* Overhauled `OllamaService.cs`. The hardcoded AAS AI Engineering syllabus prompt has been entirely removed.
* The app now automatically creates a `Documents\RevenantWarden\TutorMaterials` directory if one doesn't exist, populating it with a default `tutor_profile.md`.
* On dispatch, the app parses every `.md` and `.txt` file inside `TutorMaterials` and injects them into the LLM system prompt. This allows users to drop in course syllabuses, coding standards, or any other reference material, giving them a fully customizable tutor experience.

## Verification
* A final `dotnet build` was executed, and the project successfully compiled with 0 errors and 0 warnings. The WPF application is functionally intact and free of the bloated audio dependencies.
