using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RevenantWorkspaceWarden.Providers;

namespace RevenantWorkspaceWarden
{
    /// <summary>
    /// Handles all LLM communication (Ollama, MegaLLM, Gemini) using the ILLMProvider abstraction.
    /// Also owns the tutor prompt builder and response enrichment logic.
    /// </summary>
    internal sealed class OllamaService : IDisposable
    {
        private readonly IWardenHost _host;
        private Process? _ollamaProcess;
        private bool _disposed;

        public OllamaService(IWardenHost host)
        {
            _host = host;
        }

        private ILLMProvider GetCurrentProvider()
        {
            if (_host.SelectedProvider == "Gemini") return new GeminiProvider(_host);
            if (_host.SelectedProvider == "MegaLLM") return new MegaLLMProvider(_host, _host.MegaLlmBaseUrl);
            return new OllamaProvider(_host, _host.OllamaBaseUrl);
        }

        // ── Ollama Process Management ─────────────────────────────────────────────

        public void StartOllama()
        {
            try
            {
                var processes = Process.GetProcessesByName("ollama");
                if (processes.Length == 0)
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "ollama",
                        Arguments = "serve",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    _ollamaProcess = Process.Start(startInfo);
                    _host.AddSystemMessage("Ollama server started in background.");
                }
                else
                {
                    _host.AddSystemMessage("Ollama is already running.");
                }
            }
            catch (Exception ex)
            {
                _host.AddSystemMessage($"Failed to start Ollama automatically: {ex.Message}");
            }
        }

        public void KillOllamaProcess()
        {
            if (_ollamaProcess != null && !_ollamaProcess.HasExited)
            {
                _ollamaProcess.Kill();
                Debug.WriteLine("[Warden] Ollama process terminated on shutdown.");
            }
        }

        // ── Provider Orchestration ───────────────────────────────────────────────

        public async Task LoadModelsAsync(Action<string> addModelCallback)
        {
            var provider = GetCurrentProvider();
            var models = await provider.GetAvailableModelsAsync();
            foreach (var model in models)
            {
                addModelCallback(model);
            }
            if (models.Count == 0)
            {
                _host.AddSystemMessage($"No models found for {provider.Name}.");
            }
        }

        public async Task<string?> SendToProviderAsync(string prompt, string? lessonContext = null)
        {
            var provider = GetCurrentProvider();
            var ct = CancellationToken.None; // Add a real CT if you want cancel support in UI

            string systemPrompt = "";
            if (_host.IsTutorMode)
            {
                systemPrompt = BuildFullTutorPrompt(lessonContext); // Base instruction
                prompt = $"Student content to review:\n```\n{prompt}\n```";
            }
            else
            {
                systemPrompt = "You are a helpful, expert AI coding assistant. Provide concise, accurate answers.";
            }

            var text = await provider.ChatAsync(systemPrompt, prompt, ct);

            // Enrichment is currently coupled to the tutor response format
            if (_host.IsTutorMode && !string.IsNullOrWhiteSpace(text))
            {
                // We need to fetch the last UI message item to enrich it
                var lastMsg = _host.Messages[_host.Messages.Count - 1];
                if (lastMsg != null)
                {
                    EnrichTutorResponse(text, lastMsg);
                }
            }

            return text;
        }

        // ── Tutor Prompt Builder ──────────────────────────────────────────────────

        public string BuildFullTutorPrompt(string? additionalContext = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a precise, patient, and encouraging tutor for the AAS in AI Engineering degree. You are strict but useful, direct, focused on actionable fixes, and able to explain mistakes clearly. Avoid vague praise. Avoid overexplaining obvious things. Support learning by doing.");
            sb.AppendLine("The student is working through Python, data structures/algorithms, OOP, Flask (REST, auth, databases, async, microservices), React/TypeScript, prompt engineering & LLMs, ML foundations, and capstone full-stack AI projects.");
            sb.AppendLine();
            sb.AppendLine("Analyze the following content and respond using these exact markdown sections (do not skip any):");
            sb.AppendLine();
            sb.AppendLine("## Summary");
            sb.AppendLine("One paragraph summary of what the student is trying to do.");
            sb.AppendLine();
            sb.AppendLine("## Issues by Syllabus Area");
            sb.AppendLine("- Map issues to specific courses where relevant (PY101-103, BE101-106, FE101-103, AI101-104).");
            sb.AppendLine("- Include severity (High / Medium / Low) for each.");
            sb.AppendLine();
            sb.AppendLine("## Before / After (most important)");
            sb.AppendLine("Show the most valuable concrete example with the problematic code and a clearer/safer version plus a short explanation.");
            sb.AppendLine();
            sb.AppendLine("## Teaching Point");
            sb.AppendLine("The core principle being illustrated and why it matters at this stage of the program.");
            sb.AppendLine();
            sb.AppendLine("## Practice Follow-up");
            sb.AppendLine("1-2 small, focused exercises or questions the student can do to internalize the concept.");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(additionalContext))
            {
                sb.AppendLine("### Recent lesson context (from student's notes):");
                sb.AppendLine(additionalContext.Trim());
                sb.AppendLine();
            }

            return sb.ToString();
        }

        // ── Tutor Response Enrichment ─────────────────────────────────────────────

        public void EnrichTutorResponse(string fullText, MessageItem item)
        {
            if (string.IsNullOrWhiteSpace(fullText) || item == null) return;

            item.IsTutorResponse = true;

            var beforeAfterMatch = Regex.Match(fullText,
                @"## Before / After.*?\n```(?:\w+)?\s*(?<before>[\s\S]*?)```\s*```(?:\w+)?\s*(?<after>[\s\S]*?)```",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (beforeAfterMatch.Success)
            {
                item.BeforeCode = beforeAfterMatch.Groups["before"].Value.Trim();
                item.AfterCode  = beforeAfterMatch.Groups["after"].Value.Trim();
            }

            var teachingMatch = Regex.Match(fullText,
                @"## Teaching Point\s*(?<teaching>[\s\S]*?)(?=## Practice Follow-up|$)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var practiceMatch = Regex.Match(fullText,
                @"## Practice Follow-up\s*(?<practice>[\s\S]*?)(?=##|$)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (teachingMatch.Success || practiceMatch.Success)
            {
                var notes = new StringBuilder();
                if (teachingMatch.Success) notes.AppendLine("**Teaching Point:**\n" + teachingMatch.Groups["teaching"].Value.Trim());
                if (practiceMatch.Success)  notes.AppendLine("\n**Practice Follow-up:**\n" + practiceMatch.Groups["practice"].Value.Trim());
                item.TeachingNotes = notes.ToString().Trim();
            }

            if      (fullText.Contains("PY10", StringComparison.OrdinalIgnoreCase)) item.SyllabusArea = "Python / Data Structures / OOP";
            else if (fullText.Contains("BE10", StringComparison.OrdinalIgnoreCase)) item.SyllabusArea = "Flask / Backend";
            else if (fullText.Contains("FE10", StringComparison.OrdinalIgnoreCase)) item.SyllabusArea = "Frontend / React";
            else if (fullText.Contains("AI10", StringComparison.OrdinalIgnoreCase)) item.SyllabusArea = "AI / LLMs / Prompt Engineering";
        }

        // ── IDisposable ───────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}
