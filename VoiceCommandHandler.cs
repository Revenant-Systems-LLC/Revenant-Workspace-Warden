using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RevenantWorkspaceWarden
{
    /// <summary>
    /// Parses spoken text from the microphone and routes it to the appropriate
    /// Warden action. Extracted from MainWindow so it can be tested independently.
    /// </summary>
    internal sealed class VoiceCommandHandler
    {
        private readonly IWardenHost _host;
        private readonly Func<Task> _toggleMicAsync;
        private readonly Func<Task> _captureScreenAsync;

        /// <param name="host">The host window providing UI callbacks and LLM dispatch.</param>
        /// <param name="toggleMicAsync">Callback to start/stop the microphone (AudioService.ToggleMicAsync).</param>
        /// <param name="captureScreenAsync">Callback to trigger a screen capture (OcrService.CaptureActiveWindowForTutorAsync).</param>
        public VoiceCommandHandler(IWardenHost host, Func<Task> toggleMicAsync, Func<Task> captureScreenAsync)
        {
            _host              = host;
            _toggleMicAsync    = toggleMicAsync;
            _captureScreenAsync = captureScreenAsync;
        }

        public async Task TryHandleVoiceCommandAsync(string spokenText)
        {
            if (string.IsNullOrWhiteSpace(spokenText)) return;

            // Normalize before matching: collapse whitespace, strip spoken punctuation
            string lower = NormalizeSpoken(spokenText);

            // ── Tutor Mode toggles ────────────────────────────────────────────────
            if (MatchesAny(lower,
                "tutor mode on", "turn tutor mode on", "enable tutor mode",
                "go tutor mode", "switch to tutor", "tutor on"))
            {
                _host.IsTutorMode = true;
                _host.SetTutorModeChecked(true);
                _host.AddSystemMessage("[Voice] Tutor Mode enabled");
                return;
            }

            if (MatchesAny(lower,
                "tutor mode off", "turn tutor mode off", "disable tutor mode",
                "exit tutor mode", "tutor off", "normal mode"))
            {
                _host.IsTutorMode = false;
                _host.SetTutorModeChecked(false);
                _host.AddSystemMessage("[Voice] Tutor Mode disabled");
                return;
            }

            // ── Act on last copied / attached content ─────────────────────────────
            if (MatchesAny(lower,
                "tutor this", "review this", "tutor the last", "look at this",
                "help with this", "fix this", "what's wrong with this",
                "make this better", "explain this", "why is this bad"))
            {
                if (!string.IsNullOrWhiteSpace(_host.LastReviewableContent))
                {
                    _host.AddSystemMessage("[Voice] Running Full Tutor on the last thing you captured...");
                    await _host.DispatchLlmAsync(_host.LastReviewableContent, forceTutor: true);
                }
                else
                {
                    _host.AddSystemMessage("[Voice] I don't have anything recent. Copy or attach something first.");
                }
                return;
            }

            if (MatchesAny(lower,
                "quick review", "quick this", "simple review",
                "just review this", "fast review"))
            {
                if (!string.IsNullOrWhiteSpace(_host.LastReviewableContent))
                {
                    _host.AddSystemMessage("[Voice] Quick reviewing the last thing you captured...");
                    bool prev = _host.IsTutorMode;
                    _host.IsTutorMode = false;
                    try { await _host.DispatchLlmAsync(_host.LastReviewableContent); }
                    finally { _host.IsTutorMode = prev; }
                }
                else
                {
                    _host.AddSystemMessage("[Voice] Nothing recent to quick review. Copy or attach first.");
                }
                return;
            }

            // ── Lesson notes ──────────────────────────────────────────────────────
            if (MatchesAny(lower,
                "tutor my notes", "tutor notes", "go over my notes",
                "teach me from my notes", "explain my notes", "tutor from notes"))
            {
                _host.AddSystemMessage("[Voice] Tutoring from your latest lesson notes...");
                // Route back through host so the full HandleTutorNotesCommandAsync logic runs
                await _host.DispatchLlmAsync(
                    "Please tutor me on the key concepts in these lesson notes and give me practice examples.",
                    forceTutor: true);
                return;
            }

            if (MatchesAny(lower,
                "summarize my notes", "summarize notes", "make notes", "clean up my notes"))
            {
                _host.AddSystemMessage("[Voice] Summarizing notes — use /summarize to save them.");
                return;
            }

            // ── Mic control ───────────────────────────────────────────────────────
            if (MatchesAny(lower,
                "stop listening", "mic off", "turn mic off", "stop recording"))
            {
                _host.AddSystemMessage("[Voice] Stopping microphone...");
                await _toggleMicAsync();
                return;
            }

            // ── Screen capture ────────────────────────────────────────────────────
            if (MatchesAny(lower,
                "look at my screen", "read the code", "capture the screen",
                "see what's on screen", "look at this code", "screen capture"))
            {
                _host.AddSystemMessage("[Voice] Capturing active window...");
                await _captureScreenAsync();
                return;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Normalizes spoken text before phrase matching:
        /// lowercases, collapses whitespace, strips common punctuation.
        /// </summary>
        private static string NormalizeSpoken(string spoken)
        {
            string s = spoken.ToLowerInvariant().Trim();
            s = Regex.Replace(s, @"\s+", " ");          // collapse multiple spaces
            s = Regex.Replace(s, @"[.,!?;:]", "");     // strip spoken punctuation
            return s;
        }

        private static bool MatchesAny(string normalizedSpoken, params string[] phrases)
        {
            foreach (var phrase in phrases)
            {
                if (normalizedSpoken.Contains(phrase, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}
