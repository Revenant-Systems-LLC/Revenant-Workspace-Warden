using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RevenantWorkspaceWarden.Providers
{
    internal sealed class MegaLLMProvider : ILLMProvider
    {
        private readonly IWardenHost _host;
        private readonly string _baseUrl;
        
        public string Name => "MegaLLM";

        public MegaLLMProvider(IWardenHost host, string baseUrl = "https://ai.megallm.io")
        {
            _host = host;
            _baseUrl = baseUrl.TrimEnd('/');
        }

        public async Task<List<string>> GetAvailableModelsAsync()
        {
            var modelList = new List<string>();
            var secureApiKey = await SecretsManager.GetApiKeyAsync("ANTHROPIC_API_KEY", msg => _host.AddSystemMessage(msg));
            if (secureApiKey == null || secureApiKey.Length == 0) return modelList;

            string? apiKey = SecretsManager.UnsecureKey(secureApiKey);
            if (string.IsNullOrWhiteSpace(apiKey)) return modelList;

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                
                var response = await client.GetAsync($"{_baseUrl}/v1/models");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var data = doc.RootElement.GetProperty("data");
                    foreach (var model in data.EnumerateArray())
                    {
                        string modelName = model.GetProperty("id").GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(modelName))
                            modelList.Add(modelName);
                    }
                }
                else
                {
                    _host.AddSystemMessage($"MegaLLM failed to load models. Status: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                _host.AddSystemMessage($"Error reaching MegaLLM for models: {ex.Message}");
            }
            finally
            {
                // Clear the key from memory
                apiKey = null;
            }

            return modelList;
        }

        public async Task<string?> ChatAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            var secureApiKey = await SecretsManager.GetApiKeyAsync("ANTHROPIC_API_KEY", msg => _host.AddSystemMessage(msg));
            if (secureApiKey == null || secureApiKey.Length == 0) return null;

            _host.AddSystemMessage("Awaiting response from MegaLLM...");
            _host.SetLoadingState(true);
            _host.ScrollToBottom();

            string? apiKey = SecretsManager.UnsecureKey(secureApiKey);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _host.AddSystemMessage("Failed to extract MegaLLM API key.");
                _host.SetLoadingState(false);
                return null;
            }

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var requestBody = new
                {
                    model = _host.SelectedModel,
                    messages = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt }
                    },
                    stream = true,
                    temperature = _host.Temperature,
                    max_tokens = _host.ContextSize // Note: OpenAI uses max_tokens for generation limit, but sometimes num_ctx isn't supported standard
                };

                string jsonBody = JsonSerializer.Serialize(requestBody);
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
                {
                    Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
                };

                // Clear the key variable from memory before await
                apiKey = null;

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(ct);
                    _host.AddSystemMessage($"MegaLLM Error {response.StatusCode}: {errorContent}");
                    return null;
                }

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);

                MessageItem? aiMessage = _host.BeginStreamingMessage();

                var sb = new StringBuilder();
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("data: [DONE]")) break;
                    if (line.StartsWith("data: "))
                    {
                        string jsonChunk = line.Substring(6);
                        if (string.IsNullOrWhiteSpace(jsonChunk)) continue;

                        try
                        {
                            using var chunk = JsonDocument.Parse(jsonChunk);
                            var choices = chunk.RootElement.GetProperty("choices");
                            if (choices.GetArrayLength() > 0)
                            {
                                var delta = choices[0].GetProperty("delta");
                                if (delta.TryGetProperty("content", out var contentElem))
                                {
                                    string? token = contentElem.GetString();
                                    if (token != null)
                                    {
                                        sb.Append(token);
                                        string snapshot = "RWW: " + sb.ToString();
                                        _host.DispatchToUiThread(() =>
                                        {
                                            if (aiMessage != null) aiMessage.DisplayText = snapshot;
                                            _host.ScrollToBottom();
                                        });
                                    }
                                }
                            }
                        }
                        catch { /* Ignore malformed chunks */ }
                    }
                }

                return sb.ToString();
            }
            catch (OperationCanceledException)
            {
                _host.AddSystemMessage("MegaLLM request was cancelled.");
                return null;
            }
            catch (Exception ex)
            {
                _host.AddSystemMessage($"Error reaching MegaLLM: {ex.Message}");
                return null;
            }
            finally
            {
                _host.SetLoadingState(false);
            }
        }
    }
}
