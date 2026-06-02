using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RevenantWorkspaceWarden.Providers
{
    internal sealed class OllamaProvider : ILLMProvider
    {
        private readonly IWardenHost _host;
        private readonly string _baseUrl;
        
        public string Name => "Local Ollama";

        public OllamaProvider(IWardenHost host, string baseUrl = "http://localhost:11434")
        {
            _host = host;
            _baseUrl = baseUrl.TrimEnd('/');
        }

        public async Task<List<string>> GetAvailableModelsAsync()
        {
            var modelList = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var response = await client.GetAsync($"{_baseUrl}/api/tags");
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        var models = doc.RootElement.GetProperty("models");
                        foreach (var model in models.EnumerateArray())
                        {
                            string modelName = model.GetProperty("name").GetString() ?? "";
                            if (!string.IsNullOrWhiteSpace(modelName))
                                modelList.Add(modelName);
                        }
                        return modelList;
                    }
                }
                catch (Exception)
                {
                    if (i < 2) await Task.Delay(2000);
                }
            }

            _host.AddSystemMessage("Failed to load models list from Ollama after retries.");
            return modelList;
        }

        public async Task<string?> ChatAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            _host.AddSystemMessage("Awaiting response from Ollama...");
            _host.SetLoadingState(true);
            _host.ScrollToBottom();

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

                // Ollama /api/generate takes a single prompt. For system prompt, we format it as an instruction.
                // Alternatively, we can use /api/chat which takes an array of messages.
                // Using /api/chat is more robust for system prompts.
                
                var requestBody = new
                {
                    model = _host.SelectedModel,
                    messages = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt }
                    },
                    stream = true,
                    options = new
                    {
                        temperature = _host.Temperature,
                        num_ctx = _host.ContextSize
                    }
                };

                string jsonBody = JsonSerializer.Serialize(requestBody);
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
                {
                    Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
                };

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);

                MessageItem? aiMessage = _host.BeginStreamingMessage();

                var sb = new StringBuilder();
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var chunk = JsonSerializer.Deserialize<JsonElement>(line);
                    if (chunk.TryGetProperty("message", out var msgElem) && msgElem.TryGetProperty("content", out var contentElem))
                    {
                        string? token = contentElem.GetString();
                        if (token != null)
                        {
                            sb.Append(token);
                            string snapshot = "RWW: " + sb.ToString();
                            _host.DispatchToUiThread(() =>
                            {
                                if (aiMessage != null)
                                    aiMessage.DisplayText = snapshot;
                                _host.ScrollToBottom();
                            });
                        }
                    }
                }

                return sb.ToString();
            }
            catch (OperationCanceledException)
            {
                _host.AddSystemMessage("Ollama request was cancelled.");
                return null;
            }
            catch (Exception ex)
            {
                _host.AddSystemMessage($"Error reaching local Ollama: {ex.Message}");
                return null;
            }
            finally
            {
                _host.SetLoadingState(false);
            }
        }
    }
}
