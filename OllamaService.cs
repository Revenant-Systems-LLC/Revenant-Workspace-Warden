using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RevenantWorkspaceWarden.Providers;

namespace RevenantWorkspaceWarden
{
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
            if (_host.SelectedProvider == "Anthropic") return new AnthropicProvider(_host);
            if (_host.SelectedProvider == "OpenAI") return new OpenAICompatibleProvider(_host, "OpenAI", "https://api.openai.com/v1", "OPENAI_API_KEY");
            if (_host.SelectedProvider == "OpenRouter") return new OpenAICompatibleProvider(_host, "OpenRouter", "https://openrouter.ai/api/v1", "OPENROUTER_API_KEY");
            if (_host.SelectedProvider == "Groq") return new OpenAICompatibleProvider(_host, "Groq", "https://api.groq.com/openai/v1", "GROQ_API_KEY");
            if (_host.SelectedProvider == "xAI") return new OpenAICompatibleProvider(_host, "xAI", "https://api.x.ai/v1", "XAI_API_KEY");
            if (_host.SelectedProvider == "MegaLLM") return new OpenAICompatibleProvider(_host, "MegaLLM", _host.MegaLlmBaseUrl, "MEGALLM_API_KEY");
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
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                    };
                    _ollamaProcess = Process.Start(startInfo);
                    _host.AddSystemMessage("Ollama server started in background.");
                    if (_ollamaProcess != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            string stderr = await _ollamaProcess.StandardError.ReadToEndAsync();
                            if (!string.IsNullOrWhiteSpace(stderr))
                                _host.AddSystemMessage($"⚠ Ollama: {stderr.Trim()}");
                        });
                    }
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

            if (_host.IsTutorMode && !string.IsNullOrWhiteSpace(text))
            {
                _host.DispatchToUiThread(() =>
                {
                    var lastMsg = _host.Messages.Count > 0 ? _host.Messages[_host.Messages.Count - 1] : null;
                    if (lastMsg != null)
                        EnrichTutorResponse(text, lastMsg);
                });
            }

            return text;
        }

        // ── Tutor Prompt Builder ──────────────────────────────────────────────────

        public string BuildFullTutorPrompt(string? additionalContext = null)
        {
            var sb = new StringBuilder();
            
            string docPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string tutorDir = Path.Combine(docPath, "RevenantWarden", "TutorMaterials");
            Directory.CreateDirectory(tutorDir);
            
            string defaultProfile = Path.Combine(tutorDir, "tutor_profile.md");
            if (!File.Exists(defaultProfile))
            {
                File.WriteAllText(defaultProfile, @"You are a precise, patient, and encouraging software engineering tutor. You are strict but useful, direct, focused on actionable fixes, and able to explain mistakes clearly. Avoid vague praise. Avoid overexplaining obvious things. Support learning by doing.");
            }

            foreach (var file in Directory.GetFiles(tutorDir, "*.*"))
            {
                if (file.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"### Context from {Path.GetFileName(file)}:");
                    try { sb.AppendLine(File.ReadAllText(file)); } catch { }
                    sb.AppendLine();
                }
            }

            sb.AppendLine("Analyze the following content and respond using these exact markdown sections (do not skip any):");
            sb.AppendLine("## Summary\nOne paragraph summary of what the user is trying to do.\n");
            sb.AppendLine("## Issues Identified\n- List any logical flaws or bugs.\n- Include severity (High / Medium / Low) for each.\n");
            sb.AppendLine("## Before / After (most important)\nShow the most valuable concrete example with the problematic code and a clearer/safer version plus a short explanation.\n");
            sb.AppendLine("## Teaching Point\nThe core principle being illustrated and why it matters.\n");
            sb.AppendLine("## Practice Follow-up\n1-2 small, focused exercises or questions the user can do to internalize the concept.\n");

            if (!string.IsNullOrWhiteSpace(additionalContext))
            {
                sb.AppendLine("### Additional Context:");
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
                @"## Before / After.*?\n```(?:\w+)?\s*(?<before>[\s\S]*?)```\s*```(?:\w+)?\s*(?<after>[\s\S]*?)""",
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

            // Syllabus area extraction removed since the program is now generic.
        }

        // ── IDisposable ───────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            KillOllamaProcess();
            _ollamaProcess?.Dispose();
            _ollamaProcess = null;
        }
    }
}
