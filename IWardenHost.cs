using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RevenantWorkspaceWarden
{
    /// <summary>
    /// Contract that MainWindow implements so services can update the UI
    /// and trigger LLM dispatch without taking a hard dependency on MainWindow itself.
    /// </summary>
    public interface IWardenHost
    {
        // ── Messaging ────────────────────────────────────────────────────────────
        void AddSystemMessage(string text);
        void AddUserMessage(string text);

        /// <summary>
        /// Adds a complete (non-streaming) AI response, handling think-tag stripping.
        /// Used by the Gemini path.
        /// </summary>
        void AddAiMessage(string text);

        /// <summary>
        /// Creates a blank streaming MessageItem, adds it to the collection,
        /// and removes the "Awaiting..." placeholder. Returns the item so the
        /// caller can update DisplayText token-by-token.
        /// </summary>
        MessageItem BeginStreamingMessage();

        // ── UI State ─────────────────────────────────────────────────────────────
        void SetLoadingState(bool isLoading);
        void ScrollToBottom();
        void SetTutorModeChecked(bool isChecked);

        /// <summary>
        /// Marshals an action onto the UI thread synchronously.
        /// Use for token-streaming updates inside async service methods.
        /// </summary>
        void DispatchToUiThread(Action action);

        // ── Settings (read from the UI controls) ─────────────────────────────────
        string SelectedModel { get; }
        double Temperature { get; }
        int ContextSize { get; }
        string SelectedProvider { get; } // "Ollama", "MegaLLM", or "Gemini"
        string OllamaBaseUrl { get; }
        string MegaLlmBaseUrl { get; }

        // ── Application State ────────────────────────────────────────────────────
        bool IsTutorMode { get; set; }

        /// <summary>
        /// When true, the next prompt is answered by claude-haiku as a direct answer engine
        /// (quiz/homework mode) rather than routing through the configured provider.
        /// </summary>
        bool IsQuizMode { get; set; }

        /// <summary>
        /// The last text the user copied (Ctrl+Alt+C) or attached via the file picker.
        /// </summary>
        string? LastReviewableContent { get; set; }

        /// <summary>All messages currently displayed in the chat history.</summary>
        IReadOnlyList<MessageItem> Messages { get; }

        // ── LLM Dispatch ─────────────────────────────────────────────────────────
        Task<string?> DispatchLlmAsync(string prompt, bool forceTutor = false, string? lessonContext = null);
    }
}
