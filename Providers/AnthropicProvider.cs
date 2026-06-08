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
    internal sealed class AnthropicProvider : ILLMProvider
    {
        private readonly IWardenHost _host;
        private readonly string _baseUrl = "https://api.anthropic.com/v1";
        
        public string Name => "Anthropic";

        public AnthropicProvider(IWardenHost host)
        {
            _host = host;
        }

        public async Task<List<string>> GetAvailableModelsAsync()
        {
            // Anthropic doesn't have a simple public /models endpoint that works with API keys for dynamic listing.
            // We will hardcode their currently supported models.
            return new List<string>
            {
                "claude-3-5-sonnet-20241022",
                "claude-3-5-haiku-20241022",
                "claude-3-opus-20240229",
                "claude-3-sonnet-20240229",
                "claude-3-haiku-20240307"
            };
        }

        public async Task<string?> ChatAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            var secureApiKey = await SecretsManager.GetApiKeyAsync("ANTHROPIC_API_KEY", msg => _host.AddSystemMessage(msg));
            if (secureApiKey == null || secureApiKey.Length == 0) return null;

            _host.AddSystemMessage("Awaiting response from Anthropic...");
            _host.SetLoadingState(true);
            _host.ScrollToBottom();

            string? apiKey = SecretsManager.UnsecureKey(secureApiKey);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _host.AddSystemMessage("Failed to extract Anthropic API key.");
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
                    system = systemPrompt,
                    messages = new[]
                    {
                        new { role = "user", content = userPrompt }
                    },
                    stream = true,
                    temperature = _host.Temperature,
                    max_tokens = _host.ContextSize
                };

                string jsonBody = JsonSerializer.Serialize(requestBody);
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/messages")
                {
                    Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
                };

                apiKey = null;

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(ct);
                    _host.AddSystemMessage($"Anthropic Error {response.StatusCode}: {errorContent}");
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
                    
                    if (line.StartsWith("event:")) continue; // skip event lines
                    
                    if (line.StartsWith("data: "))
                    {
                        string jsonChunk = line.Substring(6);
                        if (string.IsNullOrWhiteSpace(jsonChunk)) continue;

                        try
                        {
                            using var chunk = JsonDocument.Parse(jsonChunk);
                            if (chunk.RootElement.TryGetProperty("type", out var typeElem))
                            {
                                string? type = typeElem.GetString();
                                if (type == "content_block_delta")
                                {
                                    var delta = chunk.RootElement.GetProperty("delta");
                                    if (delta.TryGetProperty("text", out var textElem))
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
                        }
                        catch { /* Ignore malformed chunks */ }
                    }
                }

                return sb.ToString();
            }
            catch (OperationCanceledException)
            {
                _host.AddSystemMessage("Anthropic request was cancelled.");
                return null;
            }
            catch (Exception ex)
            {
                _host.AddSystemMessage($"Error reaching Anthropic: {ex.Message}");
                return null;
            }
            finally
            {
                _host.SetLoadingState(false);
            }
        }
    }
}
