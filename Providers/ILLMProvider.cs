using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RevenantWorkspaceWarden.Providers
{
    internal interface ILLMProvider
    {
        string Name { get; }

        /// <summary>
        /// Sends a chat request to the LLM provider.
        /// Providers should use IWardenHost to stream tokens if possible.
        /// </summary>
        /// <param name="systemPrompt">The system instruction (e.g. the Axiom teacher persona).</param>
        /// <param name="userPrompt">The user's code/content.</param>
        /// <param name="ct">Cancellation token for aborting the request.</param>
        /// <returns>The fully generated response string, or null if failed.</returns>
        Task<string?> ChatAsync(string systemPrompt, string userPrompt, CancellationToken ct);

        /// <summary>
        /// Fetches the list of available models dynamically.
        /// </summary>
        Task<List<string>> GetAvailableModelsAsync();
    }
}
