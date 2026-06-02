using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace RevenantWorkspaceWarden
{
    /// <summary>
    /// Coordinates the AudioTranscriber lifecycle and mic button state.
    /// Owns the transcriber instance and wires its output to VoiceCommandHandler.
    /// </summary>
    internal sealed class AudioService : IDisposable
    {
        private readonly IWardenHost _host;
        private readonly VoiceCommandHandler _voiceHandler;
        private readonly AudioTranscriber _transcriber;
        private bool _isMicActive;
        private bool _disposed;

        public AudioService(IWardenHost host, VoiceCommandHandler voiceHandler)
        {
            _host         = host;
            _voiceHandler = voiceHandler;
            _transcriber  = new AudioTranscriber();

            // Wire transcript output: update UI then route to voice commands
            _transcriber.OnTranscriptReady = text =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _host.AddSystemMessage($"[Notes]: {text}");
                    // Fire-and-forget — voice command handler is async but we don't need to await it here
                    _ = _voiceHandler.TryHandleVoiceCommandAsync(text);
                });
            };
        }

        /// <summary>Toggles the microphone on or off. Safe to call from the UI thread.</summary>
        public async Task ToggleMicAsync()
        {
            if (!_isMicActive)
            {
                _host.SetMicState(MicState.Waiting);

                try
                {
                    string modelPath = Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "ggml-medium.en.bin");

                    if (!File.Exists(modelPath))
                    {
                        _host.AddSystemMessage(
                            $"Whisper model not found. Downloading ggml-medium.en (~1.5 GB) — " +
                            $"keep the app open, this may take a few minutes.\n" +
                            $"Model will be saved to:\n{modelPath}");
                    }

                    await _transcriber.InitializeAsync();
                    _transcriber.Start();
                    _isMicActive = true;
                    _host.SetMicState(MicState.Recording);
                    _host.AddSystemMessage(
                        "Listening for notes + voice commands. " +
                        "Press F2 to capture screen + OCR. " +
                        "Voice: 'tutor this', 'look at my screen', 'tutor my notes'...");
                }
                catch (Exception ex)
                {
                    _host.AddSystemMessage("Failed to start mic: " + ex.Message);
                    _host.SetMicState(MicState.Idle);
                }
            }
            else
            {
                _transcriber.Stop();
                _isMicActive = false;
                _host.SetMicState(MicState.Idle);
                _host.AddSystemMessage(
                    "Listening stopped. Voice commands are off. " +
                    "Use text commands or turn the mic back on to bark orders.");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _transcriber.Dispose();
        }
    }
}
