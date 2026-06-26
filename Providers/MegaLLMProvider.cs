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
            // MegaLLM acts as a proxy for Anthropic models and does not support standard /v1/models.
            // We hardcode the supported models as per MegaLLM documentation.
            var modelList = new List<string>
            {
                "claude-opus-4-1-20250805",
                "claude-3.5-sonnet",
                "claude-3.7-sonnet",
                "claude-sonnet-4"
            };
            
            return await Task.FromResult(modelList);
        }

        public async Task<string?> ChatAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            var secureApiKey = await SecretsManager.GetApiKeyAsync("MEGALLM_API_KEY", msg => _host.AddSystemMessage(msg));
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
                client.DefaultRequestHeaders.Add("x-api-key", apiKey);
                client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

                var requestBody = new
                {
                    model = _host.SelectedModel,
                    max_tokens = Math.Min(_host.ContextSize > 0 ? _host.ContextSize : 4096, 8192),
                    system = systemPrompt,
                    messages = new[]
                    {
                        new { role = "user", content = userPrompt }
                    },
                    stream = true,
                    temperature = _host.Temperature
                };

                string jsonBody = JsonSerializer.Serialize(requestBody);
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/messages")
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
                            if (chunk.RootElement.TryGetProperty("type", out var typeElem))
                            {
                                string? eventType = typeElem.GetString();
                                if (eventType == "content_block_delta")
                                {
                                    if (chunk.RootElement.TryGetProperty("delta", out var deltaElem))
                                    {
                                        if (deltaElem.TryGetProperty("text", out var textElem))
                                        {
                                            string? token = textElem.GetString();
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
                                else if (eventType == "message_stop")
                                {
                                    break;
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
