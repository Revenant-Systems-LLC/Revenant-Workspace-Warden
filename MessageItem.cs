using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace RevenantWorkspaceWarden
{
    /// <summary>
    /// Identifies what produced a chat message. Replaces the fragile TextStyle == FontStyles.Italic check.
    /// </summary>
    public enum MessageType { System, User, AI }

    public class MessageItem : INotifyPropertyChanged
    {
        private string _displayText = "";
        public string DisplayText
        {
            get => _displayText;
            set { _displayText = value; OnPropertyChanged(); }
        }

        private string _thinkText = "";
        public string ThinkText
        {
            get => _thinkText;
            set { _thinkText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasThinkText)); }
        }

        public bool HasThinkText => !string.IsNullOrEmpty(ThinkText);

        public Brush TextColor { get; set; } = Brushes.White;
        public FontWeight TextWeight { get; set; } = FontWeights.Normal;
        public FontStyle TextStyle { get; set; } = FontStyles.Normal;
        public Thickness Margin { get; set; } = new Thickness(0, 0, 0, 10);

        /// <summary>Who produced this message. Use this instead of inspecting TextStyle.</summary>
        public MessageType Type { get; set; } = MessageType.User;

        // Tutor feature properties (defaulted so existing code is unaffected)
        public bool IsTutorResponse { get; set; } = false;
        public string? SyllabusArea { get; set; }
        public string? BeforeCode { get; set; }
        public string? AfterCode { get; set; }
        public string? TeachingNotes { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
