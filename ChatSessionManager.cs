using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace RevenantWorkspaceWarden
{
    /// <summary>
    /// Handles saving the chat session to disk and summarizing lesson notes via the LLM.
    /// </summary>
    internal sealed class ChatSessionManager
    {
        private readonly IWardenHost _host;

        public ChatSessionManager(IWardenHost host)
        {
            _host = host;
        }

        /// <summary>
        /// Saves the current chat to a markdown file in NotebookLM_Staging.
        /// Uses MessageType enum instead of the old TextStyle == FontStyles.Italic check.
        /// </summary>
        public void SaveChatSession()
        {
            try
            {
                string appData    = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string wardenDir  = Path.Combine(appData, "RevenantWorkspaceWarden");
                string stagingDir = Path.Combine(wardenDir, "NotebookLM_Staging");
                Directory.CreateDirectory(stagingDir);

                string filename = Path.Combine(stagingDir, $"chat_session_{DateTime.Now:yyyyMMdd_HHmmss}.md");
                var sb = new StringBuilder();
                sb.AppendLine($"# Warden Chat Session - {DateTime.Now}");
                sb.AppendLine();

                foreach (var msg in _host.Messages)
                {
                    switch (msg.Type)
                    {
                        case MessageType.AI:
                            // Strip leading "RWW: " prefix for cleaner export
                            string aiText = msg.DisplayText.StartsWith("RWW: ", StringComparison.Ordinal)
                                ? msg.DisplayText.Substring(5).Trim()
                                : msg.DisplayText;
                            sb.AppendLine($"**Warden:** {aiText}\n");
                            break;

                        case MessageType.System:
                            sb.AppendLine($"*{msg.DisplayText}*\n");
                            break;

                        default: // MessageType.User
                            sb.AppendLine($"**Dave:** {msg.DisplayText}\n");
                            break;
                    }
                }

                File.WriteAllText(filename, sb.ToString());
                _host.AddSystemMessage($"Chat session safely exported to {filename}");
            }
            catch (Exception ex)
            {
                _host.AddSystemMessage($"Failed to save session: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads the lesson transcript, sends it to the LLM for summarization,
        /// and saves the result to lesson_notes.md and NotebookLM_Staging.
        /// </summary>
        public async Task SummarizeNotesAsync()
        {
            string appData   = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string wardenDir = Path.Combine(appData, "RevenantWorkspaceWarden");
            string path      = Path.Combine(wardenDir, "lesson_transcript.md");

            if (!File.Exists(path))
            {
                _host.AddSystemMessage("No transcript found to summarize.");
                return;
            }

            string content;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
            {
                content = await sr.ReadToEndAsync();
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                _host.AddSystemMessage("Transcript is empty.");
                return;
            }

            _host.AddSystemMessage("Summarizing notes... (This might take a moment)");

            string prompt =
                "You are a highly intelligent tutor and assistant. Below is a raw, unedited speech-to-text " +
                "transcript from a lesson. Extract the most important concepts, definitions, and takeaways, " +
                "and organize them into a clean, easy-to-read bulleted list. Here is the transcript:\n\n" +
                content;

            string? answer = await _host.DispatchLlmAsync(prompt);

            if (!string.IsNullOrWhiteSpace(answer))
            {
                string notesPath  = Path.Combine(wardenDir, "lesson_notes.md");
                string stagingDir = Path.Combine(wardenDir, "NotebookLM_Staging");
                Directory.CreateDirectory(stagingDir);

                File.WriteAllText(notesPath, answer);
                File.WriteAllText(Path.Combine(stagingDir, "lesson_notes.md"), answer);

                _host.AddSystemMessage("Notes successfully saved locally and to NotebookLM_Staging!");
            }
        }
    }
}
