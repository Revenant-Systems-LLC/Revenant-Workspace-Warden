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
    internal sealed class OpenAICompatibleProvider : ILLMProvider
    {
        private readonly IWardenHost _host;
        private readonly string _baseUrl;
        private readonly string _keyName;
        
        public string Name { get; }

        public OpenAICompatibleProvider(IWardenHost host, string name, string baseUrl, string keyName)
        {
            _host = host;
            Name = name;
            _baseUrl = baseUrl.TrimEnd('/');
            _keyName = keyName;
        }

        public async Task<List<string>> GetAvailableModelsAsync()
        {
            var modelList = new List<string>();
            var secureApiKey = await SecretsManager.GetApiKeyAsync(_keyName, msg => _host.AddSystemMessage(msg));
            if (secureApiKey == null || secureApiKey.Length == 0) return modelList;

            string? apiKey = SecretsManager.UnsecureKey(secureApiKey);
            if (string.IsNullOrWhiteSpace(apiKey)) return modelList;

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                
                var response = await client.GetAsync($"{_baseUrl}/models");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("data", out var data))
                    {
                        foreach (var model in data.EnumerateArray())
                        {
                            string modelName = model.GetProperty("id").GetString() ?? "";
                            if (!string.IsNullOrWhiteSpace(modelName))
                                modelList.Add(modelName);
                        }
                    }
                }
                else
                {
                    _host.AddSystemMessage($"{Name} failed to load models. Status: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                _host.AddSystemMessage($"Error reaching {Name} for models: {ex.Message}");
            }
            finally
            {
                apiKey = null;
            }

            return modelList;
        }

        public async Task<string?> ChatAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            var secureApiKey = await SecretsManager.GetApiKeyAsync(_keyName, msg => _host.AddSystemMessage(msg));
            if (secureApiKey == null || secureApiKey.Length == 0) return null;

            _host.AddSystemMessage($"Awaiting response from {Name}...");
            _host.SetLoadingState(true);
            _host.ScrollToBottom();

            string? apiKey = SecretsManager.UnsecureKey(secureApiKey);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _host.AddSystemMessage($"Failed to extract {Name} API key.");
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
                    max_tokens = _host.ContextSize
                };

                string jsonBody = JsonSerializer.Serialize(requestBody);
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
                {
                    Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
                };

                apiKey = null;

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(ct);
                    _host.AddSystemMessage($"{Name} Error {response.StatusCode}: {errorContent}");
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
                _host.AddSystemMessage($"{Name} request was cancelled.");
                return null;
            }
            catch (Exception ex)
            {
                _host.AddSystemMessage($"Error reaching {Name}: {ex.Message}");
                return null;
            }
            finally
            {
                _host.SetLoadingState(false);
            }
        }
    }
}
