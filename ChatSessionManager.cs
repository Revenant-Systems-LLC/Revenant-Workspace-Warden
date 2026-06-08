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
                string docPath    = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string wardenDir  = Path.Combine(docPath, "RevenantWarden");
                string stagingDir = Path.Combine(wardenDir, "ChatSessions");
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
                            sb.AppendLine($"**{Environment.UserName}:** {msg.DisplayText}\n");
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
    }
}
