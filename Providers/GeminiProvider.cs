using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RevenantWorkspaceWarden.Providers
{
    internal sealed class GeminiProvider : ILLMProvider
    {
        private readonly IWardenHost _host;
        
        public string Name => "Gemini";

        public GeminiProvider(IWardenHost host)
        {
            _host = host;
        }

        public Task<List<string>> GetAvailableModelsAsync()
        {
            // For now, hardcode the Gemini models we want to expose
            return Task.FromResult(new List<string> { "gemini-1.5-pro", "gemini-1.5-flash" });
        }

        public async Task<string?> ChatAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            var secureApiKey = await SecretsManager.GetApiKeyAsync("GEMINI_API_KEY", msg => _host.AddSystemMessage(msg));
            if (secureApiKey == null || secureApiKey.Length == 0) return null;

            _host.AddSystemMessage("Awaiting response from Gemini...");
            _host.SetLoadingState(true);
            _host.ScrollToBottom();

            string? apiKey = SecretsManager.UnsecureKey(secureApiKey);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _host.AddSystemMessage("Failed to extract Gemini API key.");
                _host.SetLoadingState(false);
                return null;
            }

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

                // Determine model name from selection, fallback to pro
                string modelName = _host.SelectedModel;
                if (!modelName.Contains("gemini")) modelName = "gemini-1.5-pro";

                string url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={apiKey}";
                apiKey = null; // Clear key from memory before await

                var requestBody = new
                {
                    system_instruction = new { parts = new[] { new { text = systemPrompt } } },
                    contents = new[] { new { parts = new[] { new { text = userPrompt } } } },
                    generationConfig = new
                    {
                        temperature = _host.Temperature,
                        maxOutputTokens = _host.ContextSize
                    }
                };

                string jsonBody = JsonSerializer.Serialize(requestBody);
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
                };

                using var response = await client.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                var responseString = await response.Content.ReadAsStringAsync(ct);
                var result = JsonSerializer.Deserialize<JsonElement>(responseString);

                var candidates = result.GetProperty("candidates");
                if (candidates.GetArrayLength() > 0)
                {
                    var text = candidates[0]
                        .GetProperty("content")
                        .GetProperty("parts")[0]
                        .GetProperty("text")
                        .GetString();

                    _host.AddAiMessage(text ?? "[Empty response from Gemini]");
                    return text;
                }

                _host.AddAiMessage("[Empty response from Gemini]");
                return null;
            }
            catch (OperationCanceledException)
            {
                _host.AddSystemMessage("Gemini request was cancelled.");
                return null;
            }
            catch (Exception ex)
            {
                _host.AddSystemMessage($"Error reaching Gemini API: {ex.Message}");
                return null;
            }
            finally
            {
                _host.SetLoadingState(false);
            }
        }
    }
}
