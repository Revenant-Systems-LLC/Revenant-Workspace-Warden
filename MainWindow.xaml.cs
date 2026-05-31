using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Security;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Microsoft.Win32;
using Tesseract;

namespace RevenantHardening.Companion
{
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

        // Phase 0 prep for tutor features (optional, defaulted so existing code is unaffected)
        public bool IsTutorResponse { get; set; } = false;
        public string? SyllabusArea { get; set; }
        public string? BeforeCode { get; set; }
        public string? AfterCode { get; set; }
        public string? TeachingNotes { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID = 9000;
        private const int CLIPBOARD_HOTKEY_ID = 9001;
        private const int SCREEN_CAPTURE_HOTKEY_ID = 9002;   // F2 for "Capture active window + OCR"
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint VK_RETURN = 0x0D;
        private const uint VK_C = 0x43;
        private const uint VK_F2 = 0x71; // F2 key

        private IntPtr _windowHandle;
        private HwndSource? _source;
        private ObservableCollection<MessageItem> _messages = new();
        private AudioTranscriber? _transcriber;
        private bool _isMicActive = false;
        private Process? _ollamaProcess;
        private SecureString? _secureGeminiApiKey;

        // Phase 0: tutor mode flag (defaults to false = current Quick Review behavior preserved)
        private bool _isTutorMode = false;

        // Phase 1 voice commands support: remember the last thing the user copied or attached
        private string? _lastReviewableContent;

        public bool IsTutorMode
        {
            get => _isTutorMode;
            set => _isTutorMode = value;
        }

        public MainWindow()
        {
            InitializeComponent();
            
            ChatHistory.ItemsSource = _messages;
            
            // Position bottom right
            var workArea = SystemParameters.WorkArea;
            this.Left = workArea.Right - this.Width - 20;
            this.Top = workArea.Bottom - this.Height - 20;
            
            AddSystemMessage("Revenant Workspace Warden initialized. Alt+Enter to hide.");
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _windowHandle = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(_windowHandle);
            _source.AddHook(HwndHook);

            if (!RegisterHotKey(_windowHandle, HOTKEY_ID, MOD_ALT, VK_RETURN))
            {
                MessageBox.Show("Failed to register Alt+Enter hotkey. It might be in use.");
            }
            if (!RegisterHotKey(_windowHandle, CLIPBOARD_HOTKEY_ID, MOD_ALT | MOD_CONTROL, VK_C))
            {
                MessageBox.Show("Failed to register Ctrl+Alt+C hotkey.");
            }

            // New hotkey for active window / screen text capture (for class use on maestro.org)
            if (!RegisterHotKey(_windowHandle, SCREEN_CAPTURE_HOTKEY_ID, 0, VK_F2))
            {
                MessageBox.Show("Failed to register F2 screen capture hotkey.");
            }
            
            _transcriber = new AudioTranscriber();
            _transcriber.OnTranscriptReady = text => {
                Dispatcher.Invoke(() =>
                {
                    AddSystemMessage($"[Notes]: {text}");

                    // Phase 1: Try to interpret spoken text as a voice command for the tutor
                    _ = TryHandleVoiceCommandAsync(text);
                });
            };
            
            StartOllama();
            await LoadOllamaModelsAsync();
        }

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void MaximizeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
                this.WindowState = WindowState.Normal;
            else
                this.WindowState = WindowState.Maximized;
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private async Task LoadOllamaModelsAsync()
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    using var client = new HttpClient();
                    client.Timeout = TimeSpan.FromSeconds(5);
                    var response = await client.GetAsync("http://localhost:11434/api/tags");
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        var models = doc.RootElement.GetProperty("models");
                        foreach (var model in models.EnumerateArray())
                        {
                            string modelName = model.GetProperty("name").GetString() ?? "";
                            ModelSelector.Items.Add(modelName);
                        }
                        if (ModelSelector.Items.Count > 0)
                        {
                            var axiomModel = ModelSelector.Items.Cast<string>().FirstOrDefault(m => m.Contains("revenant/axiom"));
                            ModelSelector.SelectedItem = axiomModel ?? ModelSelector.Items[0];
                        }
                        return; // Success, exit retry loop
                    }
                }
                catch (Exception)
                {
                    if (i < 2) await Task.Delay(2000); // Wait before retrying
                }
            }

            AddSystemMessage("Failed to load models list after retries.");
            ModelSelector.Items.Add("revenant/axiom-14b");
            ModelSelector.SelectedIndex = 0;
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _transcriber?.Dispose();
            _source?.RemoveHook(HwndHook);
            _secureGeminiApiKey?.Dispose();
            UnregisterHotKey(_windowHandle, HOTKEY_ID);
            UnregisterHotKey(_windowHandle, CLIPBOARD_HOTKEY_ID);
            UnregisterHotKey(_windowHandle, SCREEN_CAPTURE_HOTKEY_ID);
            
            try 
            {
                if (_ollamaProcess != null && !_ollamaProcess.HasExited)
                {
                    _ollamaProcess.Kill();
                }
            } 
            catch { }
        }

        private void StartOllama()
        {
            try
            {
                var processes = Process.GetProcessesByName("ollama");
                if (processes.Length == 0)
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "ollama",
                        Arguments = "serve",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    _ollamaProcess = Process.Start(startInfo);
                    AddSystemMessage("Ollama server started in background.");
                }
                else
                {
                    AddSystemMessage("Ollama is already running.");
                }
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Failed to start Ollama automatically: {ex.Message}");
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY)
            {
                if (wParam.ToInt32() == HOTKEY_ID)
                {
                    ToggleVisibility();
                    handled = true;
                }
                else if (wParam.ToInt32() == CLIPBOARD_HOTKEY_ID)
                {
                    ProcessClipboardReview();
                    handled = true;
                }
                else if (wParam.ToInt32() == SCREEN_CAPTURE_HOTKEY_ID)
                {
                    _ = CaptureActiveWindowForTutorAsync();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        private async void ProcessClipboardReview()
        {
            try
            {
                string text = string.Empty;
                bool success = false;
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        if (Clipboard.ContainsText())
                        {
                            text = Clipboard.GetText();
                            success = true;
                        }
                        break; // Break if no text or successfully got text
                    }
                    catch (ExternalException)
                    {
                        await Task.Delay(100); // Retry on locked clipboard
                    }
                }

                if (success && !string.IsNullOrEmpty(text))
                {
                    _lastReviewableContent = text;   // Remember for voice commands

                    if (this.Visibility != Visibility.Visible) ToggleVisibility();
                    
                    AddUserMessage("[Clipboard Auto-Review]");
                    AddSystemMessage("Reviewing clipboard contents...");
                    
                    string prompt = $"Please analyze, explain, and review the following snippet I just copied:\n\n{text}";
                    
                    await DispatchLlmAsync(prompt);
                }
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Failed to read clipboard: {ex.Message}");
            }
        }

        private void ToggleVisibility()
        {
            if (this.Visibility == Visibility.Visible)
            {
                this.Hide();
            }
            else
            {
                this.Show();
                this.Activate();
                InputBox.Focus();
            }
        }

        private void AddSystemMessage(string text)
        {
            _messages.Add(new MessageItem 
            { 
                DisplayText = text, 
                TextColor = Brushes.Gray, 
                TextStyle = FontStyles.Italic, 
                Margin = new Thickness(0, 0, 0, 5) 
            });
            ChatScroller.ScrollToEnd();
        }

        private void AddUserMessage(string text)
        {
            _messages.Add(new MessageItem 
            { 
                DisplayText = text, 
                TextColor = Brushes.White, 
                Margin = new Thickness(0, 0, 0, 10) 
            });
            ChatScroller.ScrollToEnd();
        }

        private void AddAiMessage(string text)
        {
            string thinkText = "";
            string displayText = text;

            var thinkMatch = Regex.Match(text, @"<think>(.*?)</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (thinkMatch.Success)
            {
                thinkText = thinkMatch.Groups[1].Value.Trim();
                displayText = text.Replace(thinkMatch.Value, "").Trim();
            }
            else
            {
                var thoughtMatch = Regex.Match(text, @"<\|im_start\|>thought(.*?)(<\|im_end\|>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (thoughtMatch.Success)
                {
                    thinkText = thoughtMatch.Groups[1].Value.Trim();
                    displayText = text.Replace(thoughtMatch.Value, "").Trim();
                }
            }

            _messages.Add(new MessageItem 
            { 
                DisplayText = "RWW: " + displayText, 
                ThinkText = thinkText,
                TextColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ffb000")), 
                TextWeight = FontWeights.Bold, 
                Margin = new Thickness(0, 0, 0, 15) 
            });
            ChatScroller.ScrollToEnd();
        }

        private async void AttachBtn_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog();
            if (openFileDialog.ShowDialog() == true)
            {
                string path = openFileDialog.FileName;
                string content = string.Empty;
                
                try
                {
                    var fileInfo = new FileInfo(path);
                    if (fileInfo.Length > 5 * 1024 * 1024) // 5 MB limit
                    {
                        AddSystemMessage($"File '{Path.GetFileName(path)}' is too large (>{fileInfo.Length / 1024 / 1024}MB). Please attach a smaller file.");
                        return;
                    }

                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs))
                    {
                        content = await sr.ReadToEndAsync();
                    }
                }
                catch (Exception ex)
                {
                    AddSystemMessage($"Failed to read attached file: {ex.Message}");
                    return;
                }
                
                AddUserMessage($"[Attached File: {Path.GetFileName(path)}]");
                AddSystemMessage("Forwarding code to LLM for review...");
                
                _lastReviewableContent = content;   // Remember for voice commands ("tutor this")

                string prompt = $"Review this file submission. Provide a brutal, professorial code review capturing logic errors, security flaws, or bad patterns.\n\nFILE: {Path.GetFileName(path)}\n\nSOURCE CODE:\n{content}";

                await DispatchLlmAsync(prompt);
            }
        }

        private async void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(InputBox.Text))
            {
                string text = InputBox.Text;
                InputBox.Clear();
                AddUserMessage(text);

                string trimmed = text.Trim();
                string lower = trimmed.ToLowerInvariant();

                if (lower == "/summarize")
                {
                    await SummarizeNotesAsync();
                }
                else if (lower == "/save")
                {
                    SaveChatSession();
                }
                else if (lower.StartsWith("/tutornotes"))
                {
                    await HandleTutorNotesCommandAsync();
                }
                else if (lower.StartsWith("/tutor"))
                {
                    string content = trimmed.Length > 6 ? trimmed.Substring(6).Trim() : "";
                    await HandleTutorCommandAsync(content);
                }
                else if (lower.StartsWith("/quick"))
                {
                    string content = trimmed.Length > 6 ? trimmed.Substring(6).Trim() : "";
                    await HandleQuickCommandAsync(content);
                }
                else if (lower == "/screen" || lower == "/capture" || lower == "/look")
                {
                    AddSystemMessage("[Command] Capturing active window + running OCR...");
                    await CaptureActiveWindowForTutorAsync();
                }
                else
                {
                    await DispatchLlmAsync(text);
                }
            }
        }

        // ===== Phase 1 Command Handlers =====

        private async Task HandleTutorCommandAsync(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                AddSystemMessage("Usage: /tutor <your code or question>   —  forces Full Tutor mode for this request.");
                return;
            }

            AddSystemMessage("Tutor Mode (forced for this request)...");
            await DispatchLlmAsync(content, forceTutor: true);
        }

        private async Task HandleQuickCommandAsync(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                AddSystemMessage("Usage: /quick <your code or question>   —  forces Quick Review for this request.");
                return;
            }

            // Temporarily force quick even if global Tutor Mode is on
            bool previous = IsTutorMode;
            IsTutorMode = false;
            try
            {
                AddSystemMessage("Quick Review (forced for this request)...");
                await DispatchLlmAsync(content);
            }
            finally
            {
                IsTutorMode = previous; // restore global setting
            }
        }

        private async Task HandleTutorNotesCommandAsync()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string notesPath = Path.Combine(appData, "RevenantWorkspaceWarden", "lesson_notes.md");

            if (!File.Exists(notesPath))
            {
                AddSystemMessage("No lesson_notes.md found. Use the MIC button + /summarize first.");
                return;
            }

            string notes;
            using (var fs = new FileStream(notesPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
            {
                notes = await sr.ReadToEndAsync();
            }

            if (string.IsNullOrWhiteSpace(notes))
            {
                AddSystemMessage("lesson_notes.md is empty.");
                return;
            }

            AddSystemMessage("Tutoring from your latest lesson notes (structured mode)...");
            await DispatchLlmAsync("Please tutor me on the key concepts in these lesson notes and give me practice examples.", 
                                   forceTutor: true, 
                                   lessonContext: notes);
        }

        // ===== Phase 1: Voice Command Support (for barking orders while MIC is live) =====

        private async Task TryHandleVoiceCommandAsync(string spokenText)
        {
            if (string.IsNullOrWhiteSpace(spokenText)) return;

            string lower = spokenText.ToLowerInvariant().Trim();

            // === Tutor Mode toggles (very common in class) ===
            if (MatchesAny(lower, "tutor mode on", "turn tutor mode on", "enable tutor mode", "go tutor mode", "switch to tutor", "tutor on"))
            {
                Dispatcher.Invoke(() =>
                {
                    IsTutorMode = true;
                    if (TutorModeCheck != null) TutorModeCheck.IsChecked = true;
                    AddSystemMessage("[Voice] Tutor Mode enabled");
                });
                return;
            }

            if (MatchesAny(lower, "tutor mode off", "turn tutor mode off", "disable tutor mode", "exit tutor mode", "tutor off", "normal mode"))
            {
                Dispatcher.Invoke(() =>
                {
                    IsTutorMode = false;
                    if (TutorModeCheck != null) TutorModeCheck.IsChecked = false;
                    AddSystemMessage("[Voice] Tutor Mode disabled");
                });
                return;
            }

            // === Act on the last thing the user copied or attached ===
            if (MatchesAny(lower, "tutor this", "review this", "tutor the last", "look at this", "help with this", "fix this", "what's wrong with this", "make this better", "explain this", "why is this bad"))
            {
                if (!string.IsNullOrWhiteSpace(_lastReviewableContent))
                {
                    AddSystemMessage("[Voice] Running Full Tutor on the last thing you captured...");
                    await DispatchLlmAsync(_lastReviewableContent, forceTutor: true);
                }
                else
                {
                    AddSystemMessage("[Voice] I don't have anything recent. Copy or attach something first.");
                }
                return;
            }

            if (MatchesAny(lower, "quick review", "quick this", "simple review", "just review this", "fast review"))
            {
                if (!string.IsNullOrWhiteSpace(_lastReviewableContent))
                {
                    AddSystemMessage("[Voice] Quick reviewing the last thing you captured...");
                    bool prev = IsTutorMode;
                    IsTutorMode = false;
                    try { await DispatchLlmAsync(_lastReviewableContent); }
                    finally { IsTutorMode = prev; }
                }
                else
                {
                    AddSystemMessage("[Voice] Nothing recent to quick review. Copy or attach first.");
                }
                return;
            }

            // === Work with lesson notes ===
            if (MatchesAny(lower, "tutor my notes", "tutor notes", "go over my notes", "teach me from my notes", "explain my notes", "tutor from notes"))
            {
                AddSystemMessage("[Voice] Tutoring from your latest lesson notes...");
                await HandleTutorNotesCommandAsync();
                return;
            }

            if (MatchesAny(lower, "summarize my notes", "summarize notes", "make notes", "clean up my notes"))
            {
                AddSystemMessage("[Voice] Summarizing notes...");
                await SummarizeNotesAsync();
                return;
            }

            // === Other useful in-class commands ===
            if (MatchesAny(lower, "stop listening", "mic off", "turn mic off", "stop recording"))
            {
                Dispatcher.Invoke(() =>
                {
                    if (_isMicActive)
                    {
                        _transcriber?.Stop();
                        _isMicActive = false;
                        MicBtn.Content = "MIC";
                        MicBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"));
                        AddSystemMessage("[Voice] Listening stopped.");
                    }
                });
                return;
            }

            // Screen / active window capture (maestro.org class)
            if (MatchesAny(lower, "look at my screen", "read the code", "capture the screen", "see what's on screen", "look at this code", "screen capture"))
            {
                AddSystemMessage("[Voice] Capturing active window...");
                _ = CaptureActiveWindowForTutorAsync();
                return;
            }
        }

        // Helper for more natural spoken command matching
        private bool MatchesAny(string spoken, params string[] phrases)
        {
            foreach (var phrase in phrases)
            {
                if (spoken.Contains(phrase))
                    return true;
            }
            return false;
        }

        // ===== Bare-bones Active Window Capture + OCR (for maestro.org class browser window) =====

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private async Task CaptureActiveWindowForTutorAsync()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                {
                    AddSystemMessage("[Screen] No active window found.");
                    return;
                }

                // Get window title
                var titleBuilder = new System.Text.StringBuilder(256);
                GetWindowText(hwnd, titleBuilder, titleBuilder.Capacity);
                string windowTitle = titleBuilder.ToString();

                // Get client area bounds (much better for OCR - excludes title bar and borders)
                if (!GetClientRect(hwnd, out RECT clientRect))
                {
                    AddSystemMessage("[Screen] Could not get client area.");
                    return;
                }

                POINT clientTopLeft = new POINT { X = clientRect.Left, Y = clientRect.Top };
                ClientToScreen(hwnd, ref clientTopLeft);

                int width = clientRect.Right - clientRect.Left;
                int height = clientRect.Bottom - clientRect.Top;

                if (width <= 0 || height <= 0)
                {
                    AddSystemMessage("[Screen] Invalid window client size.");
                    return;
                }

                // Capture only the client area (cleaner for code/lecture content)
                using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(clientTopLeft.X, clientTopLeft.Y, 0, 0, new System.Drawing.Size(width, height), System.Drawing.CopyPixelOperation.SourceCopy);
                }

                // Save the capture
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string captureDir = Path.Combine(appData, "RevenantWorkspaceWarden", "screen_captures");
                Directory.CreateDirectory(captureDir);

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string imagePath = Path.Combine(captureDir, $"screen_{timestamp}.png");
                bitmap.Save(imagePath, System.Drawing.Imaging.ImageFormat.Png);

                AddSystemMessage($"[Screen] Captured active window: {windowTitle}");
                AddSystemMessage($"[Screen] Image saved: {imagePath}");

                // OCR the captured bitmap using Tesseract (bare bones English)
                string extractedText = "";
                try
                {
                    string tessDataPath = EnsureTesseractData(appData);

                    if (Directory.Exists(tessDataPath) && File.Exists(Path.Combine(tessDataPath, "eng.traineddata")))
                    {
                        // Preprocess for better code/lecture OCR (grayscale + contrast)
                        using var processedBitmap = PreprocessForOcr(bitmap);

                        using var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default);
                        // Good settings for blocks of code/text
                        engine.SetVariable("psm", "6"); 
                        engine.SetVariable("oem", "3");

                        using var pix = Pix.LoadFromMemory(BitmapToByteArray(processedBitmap));
                        using var page = engine.Process(pix);
                        extractedText = page.GetText().Trim();
                    }
                    else
                    {
                        AddSystemMessage("[Screen] Tesseract eng.traineddata not found. OCR disabled. Drop the file in Downloads or the tessdata folder and try again.");
                    }
                }
                catch (Exception ocrEx)
                {
                    AddSystemMessage($"[Screen] OCR error: {ocrEx.Message}");
                }

                // Feed to the tutor with strong class context
                string contextPrompt = 
                    "The user is currently in class on maestro.org. They just triggered a screen capture of the active browser window.\n" +
                    $"Window title: {windowTitle}\n\n" +
                    "Below is text extracted via OCR from what is currently visible on their screen (likely lecture content, code examples, or assignment instructions).\n\n" +
                    "Please analyze this as class material. Be helpful for someone actively learning.\n\n";

                if (!string.IsNullOrWhiteSpace(extractedText))
                {
                    contextPrompt += "=== OCR EXTRACTED TEXT FROM SCREEN ===\n\n" + extractedText;
                }
                else
                {
                    contextPrompt += "No text could be extracted via OCR. A screenshot was saved to the screen_captures folder if you want to reference it.";
                }

                AddSystemMessage("[Screen] Sending to tutor...");
                await DispatchLlmAsync(contextPrompt, forceTutor: IsTutorMode);
            }
            catch (Exception ex)
            {
                AddSystemMessage($"[Screen] Capture failed: {ex.Message}");
            }
        }

        private static byte[] BitmapToByteArray(System.Drawing.Bitmap bitmap)
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }

        /// <summary>
        /// Simple preprocessing to improve OCR on code/lecture content from browser.
        /// Converts to grayscale and boosts contrast.
        /// </summary>
        private static System.Drawing.Bitmap PreprocessForOcr(System.Drawing.Bitmap original)
        {
            var processed = new System.Drawing.Bitmap(original.Width, original.Height);

            for (int y = 0; y < original.Height; y++)
            {
                for (int x = 0; x < original.Width; x++)
                {
                    var pixel = original.GetPixel(x, y);
                    
                    // Grayscale
                    int gray = (int)(pixel.R * 0.3 + pixel.G * 0.59 + pixel.B * 0.11);
                    
                    // Boost contrast (simple threshold-ish approach)
                    gray = gray < 128 ? Math.Max(0, gray - 30) : Math.Min(255, gray + 40);

                    var newColor = System.Drawing.Color.FromArgb(gray, gray, gray);
                    processed.SetPixel(x, y, newColor);
                }
            }

            return processed;
        }

        /// <summary>
        /// Ensures the Tesseract eng.traineddata file exists in the proper location.
        /// Searches common places (including the path you just provided) and auto-copies it on first use.
        /// </summary>
        private string EnsureTesseractData(string appData)
        {
            string targetTessData = Path.Combine(appData, "RevenantWorkspaceWarden", "tessdata");
            string targetFile = Path.Combine(targetTessData, "eng.traineddata");

            if (File.Exists(targetFile))
            {
                // Data is already in the ideal location
                return targetTessData;
            }

            // Search locations (add more if you drop the file elsewhere)
            var searchPaths = new List<string>
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata"),
                Path.Combine(appData, "RevenantWorkspaceWarden", "tessdata"),
                @"F:\Downloads",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads",
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };

            // Check the exact file you just told me about
            string userProvided = @"F:\Downloads\eng.traineddata";
            if (File.Exists(userProvided))
            {
                Directory.CreateDirectory(targetTessData);
                File.Copy(userProvided, targetFile, overwrite: true);
                AddSystemMessage("[Screen] Found eng.traineddata and set up OCR. Press F2 anytime to capture the active window.");
                return targetTessData;
            }

            foreach (var dir in searchPaths)
            {
                string candidate = Path.Combine(dir, "eng.traineddata");
                if (File.Exists(candidate))
                {
                    Directory.CreateDirectory(targetTessData);
                    File.Copy(candidate, targetFile, overwrite: true);
                    AddSystemMessage("[Screen] Found eng.traineddata and set up OCR. Press F2 to use it.");
                    return targetTessData;
                }
            }

            // Not found anywhere
            Directory.CreateDirectory(targetTessData);
            string recommendedPath = Path.Combine(targetTessData, "eng.traineddata");
            AddSystemMessage($"[Screen] OCR data not found.\n\nBest place to put it:\n{recommendedPath}\n\nOnce it's there, press **F2** to capture the active window.");
            return targetTessData;
        }

        private async void MicBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_transcriber == null) return;
            
            if (!_isMicActive)
            {
                MicBtn.Content = "WAIT";
                MicBtn.Background = Brushes.DarkGoldenrod;
                try
                {
                    string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ggml-medium.en.bin");
                    if (!File.Exists(modelPath))
                    {
                        AddSystemMessage("First-time setup: Downloading Whisper Medium voice model (1.5 GB)... Please keep the app open, this may take a few minutes depending on your connection.");
                    }

                    await _transcriber.InitializeAsync();
                    _transcriber.Start();
                    _isMicActive = true;
                    MicBtn.Content = "REC";
                    MicBtn.Background = Brushes.DarkRed;
                    AddSystemMessage("Listening for notes + voice commands. Press F2 to capture screen + OCR. Voice: 'tutor this', 'look at my screen', 'tutor my notes'...");
                }
                catch (Exception ex)
                {
                    AddSystemMessage("Failed to start mic: " + ex.Message);
                    MicBtn.Content = "MIC";
                    MicBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"));
                }
            }
            else
            {
                _transcriber.Stop();
                _isMicActive = false;
                MicBtn.Content = "MIC";
                MicBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"));
                AddSystemMessage("Listening stopped. Voice commands are off. Use text commands or turn the mic back on to bark orders.");
            }
        }

        private void SaveChatSession()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string wardenDir = Path.Combine(appData, "RevenantWorkspaceWarden");
                string stagingDir = Path.Combine(wardenDir, "NotebookLM_Staging");
                Directory.CreateDirectory(stagingDir);
                
                string filename = Path.Combine(stagingDir, $"chat_session_{DateTime.Now:yyyyMMdd_HHmmss}.md");
                var sb = new StringBuilder();
                sb.AppendLine($"# Warden Chat Session - {DateTime.Now}");
                sb.AppendLine();
                
                foreach (var msg in _messages)
                {
                    if (msg.DisplayText.StartsWith("RWW:"))
                        sb.AppendLine($"**Warden:** {msg.DisplayText.Substring(4).Trim()}\n");
                    else if (msg.TextStyle == FontStyles.Italic)
                        sb.AppendLine($"*{msg.DisplayText}*\n");
                    else
                        sb.AppendLine($"**Dave:** {msg.DisplayText}\n");
                }
                
                File.WriteAllText(filename, sb.ToString());
                AddSystemMessage($"Chat session safely exported to {filename}");
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Failed to save session: {ex.Message}");
            }
        }

        private async Task SummarizeNotesAsync()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string wardenDir = Path.Combine(appData, "RevenantWorkspaceWarden");
            string path = Path.Combine(wardenDir, "lesson_transcript.md");

            if (!File.Exists(path))
            {
                AddSystemMessage("No transcript found to summarize.");
                return;
            }
            
            string content;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
            {
                content = sr.ReadToEnd();
            }
            if (string.IsNullOrWhiteSpace(content))
            {
                AddSystemMessage("Transcript is empty.");
                return;
            }
            
            AddSystemMessage("Summarizing notes... (This might take a moment)");
            string prompt = "You are a highly intelligent tutor and assistant. Below is a raw, unedited speech-to-text transcript from a lesson. Extract the most important concepts, definitions, and takeaways, and organize them into a clean, easy-to-read bulleted list. Here is the transcript:\n\n" + content;
            
            string? answer = await DispatchLlmAsync(prompt);
                
            if (!string.IsNullOrWhiteSpace(answer))
            {
                string appDataDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string notesDir = Path.Combine(appDataDir, "RevenantWorkspaceWarden");
                Directory.CreateDirectory(notesDir);
                string notesPath = Path.Combine(notesDir, "lesson_notes.md");
                
                File.WriteAllText(notesPath, answer);
                
                string stagingDir = Path.Combine(notesDir, "NotebookLM_Staging");
                Directory.CreateDirectory(stagingDir);
                File.WriteAllText(Path.Combine(stagingDir, "lesson_notes.md"), answer);
                
                AddSystemMessage("Notes successfully saved locally and to NotebookLM_Staging!");
            }
        }

        private async Task<string?> SendToOllamaAsync(string prompt)
        {
            AddSystemMessage("Awaiting response from Ollama...");
            LoadingPanel.Visibility = Visibility.Visible;
            ChatScroller.ScrollToEnd();
            
            string selectedModel = ModelSelector.SelectedItem?.ToString() ?? "revenant/axiom-14b";
            double temp = TempSlider.Value;
            int numCtx = 32768;
            if (int.TryParse(ContextBox.Text, out int parsedCtx))
            {
                numCtx = parsedCtx;
            }

            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromMinutes(10);
                var requestBody = new
                {
                    model = selectedModel, 
                    prompt = prompt,
                    stream = true,
                    options = new { 
                        think = false,
                        temperature = temp,
                        num_ctx = numCtx
                    }
                };
                
                string jsonBody = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                
                using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:11434/api/generate");
                request.Content = content;
                
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                
                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);
                
                MessageItem? aiMessage = null;
                Dispatcher.Invoke(() => {
                    aiMessage = new MessageItem 
                    { 
                        DisplayText = "RWW: ", 
                        TextColor = Brushes.White, 
                        Margin = new Thickness(10, 0, 0, 10) 
                    };
                    _messages.Add(aiMessage);
                    
                    var awaitingMsg = _messages.FirstOrDefault(m => m.DisplayText == "Awaiting response from Ollama...");
                    if (awaitingMsg != null) _messages.Remove(awaitingMsg);
                });
                
                var sb = new StringBuilder();
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        var chunk = JsonSerializer.Deserialize<JsonElement>(line);
                        if (chunk.TryGetProperty("response", out var textElem))
                        {
                            string? text = textElem.GetString();
                            if (text != null)
                            {
                                sb.Append(text);
                                
                                Dispatcher.Invoke(() => {
                                    if (aiMessage != null)
                                    {
                                        aiMessage.DisplayText = "RWW: " + sb.ToString();
                                    }
                                    ChatScroller.ScrollToEnd();
                                });
                            }
                        }
                    }
                }

                // Phase 1: If this was a tutor response, parse structured sections and enrich the MessageItem
                if (IsTutorMode && aiMessage != null)
                {
                    EnrichTutorResponse(sb.ToString(), aiMessage);
                }
                
                return sb.ToString();
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Error reaching local Ollama: {ex.Message}");
                return null;
            }
            finally
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void ProviderToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (ProviderToggle != null)
            {
                ProviderToggle.Content = "GEMINI";
                ProviderToggle.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4B0082")); // Indigo
            }
        }

        private void ProviderToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            if (ProviderToggle != null)
            {
                ProviderToggle.Content = "LOCAL";
                ProviderToggle.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"));
            }
        }

        // Phase 1: Tutor Mode handlers (wired from XAML checkbox in Settings Expander)
        private void TutorModeCheck_Checked(object sender, RoutedEventArgs e)
        {
            IsTutorMode = true;
            AddSystemMessage("Tutor Mode enabled — reviews will use structured syllabus-aware prompts with teaching focus.");
        }

        private void TutorModeCheck_Unchecked(object sender, RoutedEventArgs e)
        {
            IsTutorMode = false;
            AddSystemMessage("Tutor Mode disabled — back to quick reviews.");
        }

        private async Task<SecureString?> GetSecureGeminiApiKeyAsync()
        {
            if (_secureGeminiApiKey != null)
            {
                return _secureGeminiApiKey;
            }

            string secretsFile = @"B:\Secrets\Revenant-Workspace-Warden.env";
            
            try
            {
                var driveB = DriveInfo.GetDrives().FirstOrDefault(d => d.Name.StartsWith("B", StringComparison.OrdinalIgnoreCase));
                if (driveB == null)
                {
                    AddSystemMessage("Drive B: does not exist on this system. Cannot retrieve API key.");
                    return null;
                }

                if (!driveB.IsReady)
                {
                    AddSystemMessage("B: drive appears to be locked. Launching unlock prompt...");
                    var unlockProcess = Process.Start("bdeunlock.exe", "B:");
                    if (unlockProcess != null)
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                        try
                        {
                            await unlockProcess.WaitForExitAsync(cts.Token);
                        }
                        catch (TaskCanceledException)
                        {
                            AddSystemMessage("Unlock prompt timed out.");
                        }
                    }
                    
                    if (!driveB.IsReady)
                    {
                        AddSystemMessage("Drive B: is still locked. Cannot retrieve API key.");
                        return null;
                    }
                }

                if (!File.Exists(secretsFile))
                {
                    AddSystemMessage($"Secrets file not found at {secretsFile}");
                    return null;
                }

                var lines = await File.ReadAllLinesAsync(secretsFile);
                var keyLine = lines.FirstOrDefault(l => l.StartsWith("GEMINI_API_KEY="));
                if (keyLine != null)
                {
                    string rawKey = keyLine.Substring("GEMINI_API_KEY=".Length).Trim();
                    var secureKey = new SecureString();
                    foreach (char c in rawKey)
                    {
                        secureKey.AppendChar(c);
                    }
                    secureKey.MakeReadOnly();
                    _secureGeminiApiKey = secureKey;
                    return _secureGeminiApiKey;
                }

                AddSystemMessage("GEMINI_API_KEY not found in secrets file.");
                return null;
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Error accessing secrets: {ex.Message}");
                return null;
            }
        }

        private async Task<string?> SendToGeminiAsync(string prompt)
        {
            SecureString? secureApiKey = await GetSecureGeminiApiKeyAsync();
            if (secureApiKey == null || secureApiKey.Length == 0) return null;

            AddSystemMessage("Awaiting response from Gemini...");
            LoadingPanel.Visibility = Visibility.Visible;
            ChatScroller.ScrollToEnd();
            
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromMinutes(10);
                
                IntPtr bstr = Marshal.SecureStringToBSTR(secureApiKey);
                string? apiKey = null;
                try
                {
                    apiKey = Marshal.PtrToStringBSTR(bstr);
                }
                finally
                {
                    Marshal.ZeroFreeBSTR(bstr);
                }

                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    AddSystemMessage("Failed to decrypt API key.");
                    return null;
                }

                string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-pro:generateContent?key={apiKey}";
                
                var requestBody = new
                {
                    contents = new[]
                    {
                        new { parts = new[] { new { text = prompt } } }
                    }
                };
                
                string jsonBody = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                
                var response = await client.PostAsync(url, content);
                
                // Zero out reference in local memory
                apiKey = null;
                
                response.EnsureSuccessStatusCode();
                
                var responseString = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<JsonElement>(responseString);
                
                var candidates = result.GetProperty("candidates");
                if (candidates.GetArrayLength() > 0)
                {
                    var text = candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
                    AddAiMessage(text ?? "[Empty response from Gemini]");
                    return text;
                }
                
                AddAiMessage("[Empty response from Gemini]");
                return null;
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Error reaching Gemini API: {ex.Message}");
                return null;
            }
            finally
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Phase 1: Central dispatcher. When Tutor Mode is active (or forceTutor=true),
        /// wraps the content with syllabus-aware structured tutor instructions before sending.
        /// Gemini path remains completely untouched in routing and behavior.
        /// </summary>
        private async Task<string?> DispatchLlmAsync(string userContent, bool forceTutor = false, string? lessonContext = null)
        {
            bool useTutor = forceTutor || IsTutorMode;

            string finalPrompt = userContent;

            if (useTutor)
            {
                finalPrompt = BuildFullTutorPrompt(userContent, lessonContext);
            }
            // else: Quick Review - pass content through (existing caller prompts already contain review instructions)

            if (ProviderToggle.IsChecked == true)
                return await SendToGeminiAsync(finalPrompt);
            else
                return await SendToOllamaAsync(finalPrompt);
        }

        // ===== Phase 1 Prompt Builders =====

        /// <summary>
        /// Builds a rich, syllabus-aware tutor prompt that requests structured educational output.
        /// </summary>
        private string BuildFullTutorPrompt(string userContent, string? additionalContext = null)
        {
            var sb = new StringBuilder();

            sb.AppendLine("You are a precise, patient, and encouraging tutor for the AAS in AI Engineering degree.");
            sb.AppendLine("The student is working through Python, data structures/algorithms, OOP, Flask (REST, auth, databases, async, microservices), React/TypeScript, prompt engineering & LLMs, ML foundations, and capstone full-stack AI projects.");
            sb.AppendLine();
            sb.AppendLine("Analyze the following content and respond using these exact markdown sections (do not skip any):");
            sb.AppendLine();
            sb.AppendLine("## Summary");
            sb.AppendLine("One paragraph summary of what the student is trying to do.");
            sb.AppendLine();
            sb.AppendLine("## Issues by Syllabus Area");
            sb.AppendLine("- Map issues to specific courses where relevant (PY101-103, BE101-106, FE101-103, AI101-104).");
            sb.AppendLine("- Include severity (High / Medium / Low) for each.");
            sb.AppendLine();
            sb.AppendLine("## Before / After (most important)");
            sb.AppendLine("Show the most valuable concrete example with the problematic code and a clearer/safer version plus a short explanation.");
            sb.AppendLine();
            sb.AppendLine("## Teaching Point");
            sb.AppendLine("The core principle being illustrated and why it matters at this stage of the program.");
            sb.AppendLine();
            sb.AppendLine("## Practice Follow-up");
            sb.AppendLine("1-2 small, focused exercises or questions the student can do to internalize the concept.");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(additionalContext))
            {
                sb.AppendLine("### Recent lesson context (from student's notes):");
                sb.AppendLine(additionalContext.Trim());
                sb.AppendLine();
            }

            sb.AppendLine("Student content / code to review:");
            sb.AppendLine("```");
            sb.AppendLine(userContent);
            sb.AppendLine("```");

            return sb.ToString();
        }

        /// <summary>
        /// (Optional future use) Quick Review can get a lighter wrapper if desired.
        /// For now we mostly pass through the caller's existing review language.
        /// </summary>
        private string BuildQuickReviewPrompt(string userContent)
        {
            return userContent; // Phase 1: keep behavior close to original for Quick path
        }

        /// <summary>
        /// Phase 1: Light regex-based parser for the structured tutor output.
        /// Populates BeforeCode, AfterCode, and TeachingNotes on the MessageItem for richer rendering.
        /// </summary>
        private void EnrichTutorResponse(string fullText, MessageItem item)
        {
            if (string.IsNullOrWhiteSpace(fullText) || item == null) return;

            item.IsTutorResponse = true;

            // Try to extract Before / After code blocks
            var beforeAfterMatch = Regex.Match(fullText, @"## Before / After.*?\n```(?:\w+)?\s*(?<before>[\s\S]*?)```\s*```(?:\w+)?\s*(?<after>[\s\S]*?)```", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (beforeAfterMatch.Success)
            {
                item.BeforeCode = beforeAfterMatch.Groups["before"].Value.Trim();
                item.AfterCode = beforeAfterMatch.Groups["after"].Value.Trim();
            }

            // Extract Teaching Point + Practice as TeachingNotes
            var teachingMatch = Regex.Match(fullText, @"## Teaching Point\s*(?<teaching>[\s\S]*?)(?=## Practice Follow-up|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var practiceMatch = Regex.Match(fullText, @"## Practice Follow-up\s*(?<practice>[\s\S]*?)(?=##|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (teachingMatch.Success || practiceMatch.Success)
            {
                var notes = new StringBuilder();
                if (teachingMatch.Success) notes.AppendLine("**Teaching Point:**\n" + teachingMatch.Groups["teaching"].Value.Trim());
                if (practiceMatch.Success) notes.AppendLine("\n**Practice Follow-up:**\n" + practiceMatch.Groups["practice"].Value.Trim());
                item.TeachingNotes = notes.ToString().Trim();
            }

            // Syllabus area hint (very lightweight)
            if (fullText.Contains("PY10", StringComparison.OrdinalIgnoreCase)) item.SyllabusArea = "Python / Data Structures / OOP";
            else if (fullText.Contains("BE10", StringComparison.OrdinalIgnoreCase)) item.SyllabusArea = "Flask / Backend";
            else if (fullText.Contains("FE10", StringComparison.OrdinalIgnoreCase)) item.SyllabusArea = "Frontend / React";
            else if (fullText.Contains("AI10", StringComparison.OrdinalIgnoreCase)) item.SyllabusArea = "AI / LLMs / Prompt Engineering";
        }
    }
}