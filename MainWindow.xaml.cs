using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace RevenantWorkspaceWarden
{
    /// <summary>
    /// Thin orchestrator. Window lifecycle, hotkey wiring, UI event delegation.
    /// All heavy logic lives in the service classes. Implements IWardenHost so
    /// services can update the UI and trigger LLM dispatch without a hard
    /// dependency on this class.
    /// </summary>
    public partial class MainWindow : Window, IWardenHost
    {
        // ── Hotkey Constants ──────────────────────────────────────────────────────
        private const int  HOTKEY_ID                = 9000;
        private const int  CLIPBOARD_HOTKEY_ID      = 9001;
        private const int  SCREEN_CAPTURE_HOTKEY_ID = 9002;
        private const uint MOD_ALT                  = 0x0001;
        private const uint MOD_CONTROL              = 0x0002;
        private const uint VK_F5                   = 0x74;
        private const uint VK_C                     = 0x43;
        private const uint VK_F2                    = 0x71;
        private const uint VK_DIVIDE                = 0x6F;

        // ── Core Fields ───────────────────────────────────────────────────────────
        private IntPtr _windowHandle;
        private HwndSource? _source;
        private readonly ObservableCollection<MessageItem> _messages = new();

        // ── Services ──────────────────────────────────────────────────────────────
        private OllamaService       _ollamaService      = null!;
        private OcrService          _ocrService         = null!;
        private ChatSessionManager  _chatSessionManager = null!;

        // ── IWardenHost State ─────────────────────────────────────────────────────
        public bool    IsTutorMode          { get; set; } = false;
        public bool    IsQuizMode           { get; set; } = false;
        public string? LastReviewableContent { get; set; }
        public IReadOnlyList<MessageItem> Messages => _messages;

        // ── IWardenHost UI Properties (read from controls) ────────────────────────
        public string SelectedModel => ModelSelector.SelectedItem?.ToString() ?? "revenant/axiom-14b";
        public double Temperature   => TempSlider.Value;
        public int    ContextSize
        {
            get => int.TryParse(ContextBox.Text, out int ctx) && ctx > 0 ? ctx : 32768;
        }
        public string SelectedProvider => (ProviderSelector.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content.ToString() ?? "Ollama";
        public string OllamaBaseUrl => OllamaUrlBox.Text;
        public string MegaLlmBaseUrl => MegaLlmUrlBox.Text;
        // =========================================================================
        // Constructor
        // =========================================================================

        public MainWindow()
        {
            InitializeComponent();
            ChatHistory.ItemsSource = _messages;

            // Bottom-right corner
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right  - Width  - 20;
            Top  = workArea.Bottom - Height - 20;

            _ollamaService      = new OllamaService(this);
            _ocrService         = new OcrService(this);
            _chatSessionManager = new ChatSessionManager(this);

            AddSystemMessage("Revenant Workspace Warden initialized. Alt+Enter to hide.");
        }

        // =========================================================================
        // Window Lifecycle
        // =========================================================================

        // ── Hotkey Rebind State ───────────────────────────────────────────────────
        private int _rebindTarget = 0; // 0=none, HOTKEY_ID / CLIPBOARD_HOTKEY_ID / SCREEN_CAPTURE_HOTKEY_ID
        private Button? _rebindActiveBtn = null;

        private void ApplyHotkeyBindings(Providers.AppConfig config)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, HOTKEY_ID);
            NativeMethods.UnregisterHotKey(_windowHandle, CLIPBOARD_HOTKEY_ID);
            NativeMethods.UnregisterHotKey(_windowHandle, SCREEN_CAPTURE_HOTKEY_ID);

            if (!NativeMethods.RegisterHotKey(_windowHandle, HOTKEY_ID,
                    config.ToggleHotkey.Modifiers, config.ToggleHotkey.VirtualKey))
                AddSystemMessage($"⚠ {config.ToggleHotkey.DisplayName} already in use — toggle unavailable.");

            if (!NativeMethods.RegisterHotKey(_windowHandle, CLIPBOARD_HOTKEY_ID,
                    config.ClipboardHotkey.Modifiers, config.ClipboardHotkey.VirtualKey))
                AddSystemMessage($"⚠ {config.ClipboardHotkey.DisplayName} already in use — clipboard review unavailable.");

            if (!NativeMethods.RegisterHotKey(_windowHandle, SCREEN_CAPTURE_HOTKEY_ID,
                    config.ScreenCaptureHotkey.Modifiers, config.ScreenCaptureHotkey.VirtualKey))
                AddSystemMessage($"⚠ {config.ScreenCaptureHotkey.DisplayName} already in use — screen capture unavailable.");

            ToggleHotkeyBox.Text       = config.ToggleHotkey.DisplayName;
            ClipboardHotkeyBox.Text    = config.ClipboardHotkey.DisplayName;
            ScreenCaptureHotkeyBox.Text = config.ScreenCaptureHotkey.DisplayName;
        }

        private static string BuildHotkeyDisplayName(uint modifiers, uint vk)
        {
            var parts = new System.Collections.Generic.List<string>();
            if ((modifiers & 0x0002) != 0) parts.Add("Ctrl");
            if ((modifiers & 0x0001) != 0) parts.Add("Alt");
            if ((modifiers & 0x0008) != 0) parts.Add("Shift");
            var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey((int)vk);
            parts.Add(key.ToString());
            return string.Join("+", parts);
        }

        private void StartRebind(int hotkeyId, Button btn)
        {
            if (_rebindTarget != 0) CancelRebind();
            _rebindTarget   = hotkeyId;
            _rebindActiveBtn = btn;
            btn.Content     = "Press keys…";
            btn.Foreground  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#dfbe81"));
            HotkeyHint.Text = "Press your combo. Esc to cancel. Bare keys allowed but may conflict.";
        }

        private void CancelRebind()
        {
            if (_rebindActiveBtn != null)
            {
                _rebindActiveBtn.Content   = "Rebind";
                _rebindActiveBtn.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3ad5a1"));
            }
            _rebindTarget    = 0;
            _rebindActiveBtn = null;
            HotkeyHint.Text  = "";
        }

        private void RebindToggleBtn_Click(object sender, RoutedEventArgs e)
            => StartRebind(HOTKEY_ID, RebindToggleBtn);
        private void RebindClipboardBtn_Click(object sender, RoutedEventArgs e)
            => StartRebind(CLIPBOARD_HOTKEY_ID, RebindClipboardBtn);
        private void RebindScreenBtn_Click(object sender, RoutedEventArgs e)
            => StartRebind(SCREEN_CAPTURE_HOTKEY_ID, RebindScreenBtn);

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (_rebindTarget == 0) return;

            e.Handled = true;

            if (e.Key == System.Windows.Input.Key.Escape) { CancelRebind(); return; }

            // Ignore bare modifier keys — wait for an actual key
            var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            if (key == System.Windows.Input.Key.LeftCtrl  || key == System.Windows.Input.Key.RightCtrl  ||
                key == System.Windows.Input.Key.LeftAlt   || key == System.Windows.Input.Key.RightAlt   ||
                key == System.Windows.Input.Key.LeftShift || key == System.Windows.Input.Key.RightShift ||
                key == System.Windows.Input.Key.LWin      || key == System.Windows.Input.Key.RWin)
                return;

            uint modifiers = 0;
            if (System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LeftCtrl)  ||
                System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RightCtrl))
                modifiers |= MOD_CONTROL;
            if (System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LeftAlt)   ||
                System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RightAlt))
                modifiers |= MOD_ALT;
            if (System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LeftShift) ||
                System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RightShift))
                modifiers |= 0x0008; // MOD_SHIFT

            uint vk = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);

            var config = Providers.SecretsManager.LoadConfig();
            string displayName = BuildHotkeyDisplayName(modifiers, vk);

            switch (_rebindTarget)
            {
                case HOTKEY_ID:
                    config.ToggleHotkey = new Providers.HotkeyBinding
                        { Modifiers = modifiers, VirtualKey = vk, DisplayName = displayName };
                    break;
                case CLIPBOARD_HOTKEY_ID:
                    config.ClipboardHotkey = new Providers.HotkeyBinding
                        { Modifiers = modifiers, VirtualKey = vk, DisplayName = displayName };
                    break;
                case SCREEN_CAPTURE_HOTKEY_ID:
                    config.ScreenCaptureHotkey = new Providers.HotkeyBinding
                        { Modifiers = modifiers, VirtualKey = vk, DisplayName = displayName };
                    break;
            }

            Providers.SecretsManager.SaveConfig(config);
            CancelRebind();
            ApplyHotkeyBindings(config);

            if (modifiers == 0)
                AddSystemMessage($"⚠ {displayName} is a bare key — may conflict with other apps.");
            else
                AddSystemMessage($"Hotkey saved: {displayName}");
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _windowHandle = new WindowInteropHelper(this).Handle;
            _source       = HwndSource.FromHwnd(_windowHandle);
            _source.AddHook(HwndHook);

            var config = RevenantWorkspaceWarden.Providers.SecretsManager.LoadConfig();
            ApplyHotkeyBindings(config);
            OllamaUrlBox.Text = config.OllamaBaseUrl;
            MegaLlmUrlBox.Text = config.MegaLlmBaseUrl;
            
            foreach (System.Windows.Controls.ComboBoxItem item in ProviderSelector.Items)
            {
                if (item.Content.ToString() == config.SelectedProvider)
                {
                    ProviderSelector.SelectedItem = item;
                    break;
                }
            }

            if (SelectedProvider == "Ollama")
            {
                _ollamaService.StartOllama();
            }

            await PopulateModelListAsync(config.SelectedModel);
        }

        private int _tutorialStep = 0;

        private void EngineSettingsExpander_Expanded(object sender, RoutedEventArgs e)
        {
            var config = RevenantWorkspaceWarden.Providers.SecretsManager.LoadConfig();
            if (!config.HasCompletedTutorial)
            {
                _tutorialStep = 0;
                SpotlightOverlay.Visibility = Visibility.Visible;
                Dispatcher.BeginInvoke(new Action(() => ShowTutorialStep()), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private void ShowTutorialStep()
        {
            switch (_tutorialStep)
            {
                case 0:
                    HighlightControl(OllamaUrlBox);
                    TutorialText.Text = "This is the Base URL for your local AI engine (like Ollama).";
                    break;
                case 1:
                    HighlightControl(ProviderSelector);
                    TutorialText.Text = "Select your LLM provider here. You can use local engines or cloud APIs.";
                    break;
                case 2:
                    HighlightControl(ModelSelector);
                    TutorialText.Text = "Choose the model you want to chat with. The list updates based on your provider.";
                    break;
                case 3:
                    HighlightControl(ManageApiKeysBtn);
                    TutorialText.Text = "If you use a cloud provider, add your API keys securely in the vault here.";
                    break;
                default:
                    EndTutorial();
                    break;
            }
        }

        private async void HighlightControl(FrameworkElement target)
        {
            target.BringIntoView();
            await Task.Delay(50); // allow layout to settle after scrolling
            target.UpdateLayout();

            try
            {
                GeneralTransform transform = target.TransformToVisual(SpotlightOverlay);
                Point topLeft = transform.Transform(new Point(0, 0));
                
                double padding = 5;
                SpotlightHole.Rect = new Rect(
                    topLeft.X - padding,
                    topLeft.Y - padding,
                    target.ActualWidth + (padding * 2),
                    target.ActualHeight + (padding * 2));

                TutorialPopup.Margin = new Thickness(0, topLeft.Y + target.ActualHeight + padding + 15, 0, 0);
            }
            catch (InvalidOperationException ex)
            {
                // Fallback if not connected to visual tree
                System.Diagnostics.Debug.WriteLine($"Highlight error: {ex.Message}");
                SpotlightHole.Rect = new Rect(0, 0, 0, 0);
            }
        }

        private void NextTutorial_Click(object sender, RoutedEventArgs e)
        {
            _tutorialStep++;
            ShowTutorialStep();
        }

        private void SkipTutorial_Click(object sender, RoutedEventArgs e)
        {
            EndTutorial();
        }

        private void EndTutorial()
        {
            SpotlightOverlay.Visibility = Visibility.Collapsed;
            var config = RevenantWorkspaceWarden.Providers.SecretsManager.LoadConfig();
            config.HasCompletedTutorial = true;
            RevenantWorkspaceWarden.Providers.SecretsManager.SaveConfig(config);
        }

        private async Task PopulateModelListAsync(string? targetModel = null)
        {
            Dispatcher.Invoke(() => ModelSelector.Items.Clear());
            
            await _ollamaService.LoadModelsAsync(modelName =>
            {
                Dispatcher.Invoke(() =>
                {
                    ModelSelector.Items.Add(modelName);
                    if (targetModel != null && modelName == targetModel)
                    {
                        ModelSelector.SelectedItem = modelName;
                    }
                    else if (ModelSelector.SelectedItem == null && ModelSelector.Items.Count > 0)
                    {
                        ModelSelector.SelectedIndex = 0;
                    }
                });
            });

            Dispatcher.Invoke(() =>
            {
                if (ModelSelector.Items.Count == 0 && SelectedProvider == "Ollama")
                {
                    ModelSelector.Items.Add("revenant/axiom-14b");
                    ModelSelector.SelectedIndex = 0;
                }
            });
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _source?.RemoveHook(HwndHook);

            NativeMethods.UnregisterHotKey(_windowHandle, HOTKEY_ID);
            NativeMethods.UnregisterHotKey(_windowHandle, CLIPBOARD_HOTKEY_ID);
            NativeMethods.UnregisterHotKey(_windowHandle, SCREEN_CAPTURE_HOTKEY_ID);

            try
            {
                _ollamaService.KillOllamaProcess();
            }
            catch (Exception ex)
            {
                // Window is closing — log to debug output rather than updating the UI
                Debug.WriteLine($"[Warden] Failed to kill Ollama process on shutdown: {ex.Message}");
            }

            _ollamaService.Dispose();
        }

        // =========================================================================
        // IWardenHost — Messaging
        // =========================================================================

        public void AddSystemMessage(string text)
        {
            Dispatcher.Invoke(() =>
            {
                _messages.Add(new MessageItem
                {
                    DisplayText = text,
                    TextColor   = Brushes.Gray,
                    TextStyle   = FontStyles.Italic,
                    Margin      = new Thickness(0, 0, 0, 5),
                    Type        = MessageType.System
                });
                ChatScroller.ScrollToEnd();
            });
        }

        public void AddUserMessage(string text)
        {
            Dispatcher.Invoke(() =>
            {
                _messages.Add(new MessageItem
                {
                    DisplayText = text,
                    TextColor   = Brushes.White,
                    Margin      = new Thickness(0, 0, 0, 10),
                    Type        = MessageType.User
                });
                ChatScroller.ScrollToEnd();
            });
        }

        public void AddAiMessage(string text)
        {
            Dispatcher.Invoke(() =>
            {
                string thinkText   = "";
                string displayText = text;

                var thinkMatch = Regex.Match(text,
                    @"<think>(.*?)</think>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (thinkMatch.Success)
                {
                    thinkText   = thinkMatch.Groups[1].Value.Trim();
                    displayText = text.Replace(thinkMatch.Value, "").Trim();
                }
                else
                {
                    var thoughtMatch = Regex.Match(text,
                        @"<\|im_start\|>thought(.*?)(<\|im_end\|>|$)",
                        RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    if (thoughtMatch.Success)
                    {
                        thinkText   = thoughtMatch.Groups[1].Value.Trim();
                        displayText = text.Replace(thoughtMatch.Value, "").Trim();
                    }
                }

                _messages.Add(new MessageItem
                {
                    DisplayText = "RWW: " + displayText,
                    ThinkText   = thinkText,
                    TextColor   = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3ad5a1")),
                    TextWeight  = FontWeights.Bold,
                    Margin      = new Thickness(0, 0, 0, 15),
                    Type        = MessageType.AI
                });
                ChatScroller.ScrollToEnd();
            });
        }

        public MessageItem BeginStreamingMessage()
        {
            MessageItem? aiMessage = null;
            Dispatcher.Invoke(() =>
            {
                aiMessage = new MessageItem
                {
                    DisplayText = "RWW: ",
                    TextColor   = Brushes.White,
                    Margin      = new Thickness(10, 0, 0, 10),
                    Type        = MessageType.AI
                };
                _messages.Add(aiMessage);

                // Remove the "Awaiting..." placeholder (covers both Ollama and Gemini variants)
                var placeholder = _messages.FirstOrDefault(
                    m => m.DisplayText.StartsWith("Awaiting response", StringComparison.Ordinal));
                if (placeholder != null) _messages.Remove(placeholder);
            });
            return aiMessage!;
        }

        // =========================================================================
        // IWardenHost — UI State
        // =========================================================================

        public void SetLoadingState(bool isLoading)
        {
            Dispatcher.Invoke(() =>
                LoadingPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed);
        }

        public void SetWardenState(bool isError)
        {
            Dispatcher.Invoke(() =>
            {
                if (isError)
                {
                    this.Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/RWW_RAGE.ico"));
                }
                else
                {
                    this.Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/RWW_SAGEGOLD.ico"));
                }
            });
        }

        public void ScrollToBottom() => Dispatcher.Invoke(() => ChatScroller.ScrollToEnd());

        public void DispatchToUiThread(Action action) => Dispatcher.Invoke(action);

        public void SetTutorModeChecked(bool isChecked)
        {
            Dispatcher.Invoke(() =>
            {
                if (TutorModeBtn != null) TutorModeBtn.IsChecked = isChecked;
            });
        }

        // =========================================================================
        // IWardenHost — LLM Dispatch (central router)
        // =========================================================================

        public async Task<string?> DispatchLlmAsync(
            string prompt, bool forceTutor = false, string? lessonContext = null)
        {
            try
            {
                string? aiResponse = await _ollamaService.SendToProviderAsync(prompt, lessonContext);
                SetWardenState(false); // Success -> Sage Gold
                return aiResponse;
            }
            catch (Exception ex)
            {
                SetWardenState(true); // Error -> Rage Mode
                AddSystemMessage($"[Warden Error]: {ex.Message}");
                return null;
            }
        }

        // =========================================================================
        // Hotkey / Window Message Pump
        // =========================================================================

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if      (id == HOTKEY_ID)                { ToggleVisibility();                                   handled = true; }
                else if (id == CLIPBOARD_HOTKEY_ID)      { ProcessClipboardReview();                             handled = true; }
                else if (id == SCREEN_CAPTURE_HOTKEY_ID) { _ = _ocrService.CaptureActiveWindowForTutorAsync();  handled = true; }
            }
            return IntPtr.Zero;
        }

        private void ToggleVisibility()
        {
            if (Visibility == Visibility.Visible) { Hide(); }
            else { Show(); Activate(); InputBox.Focus(); }
        }

        private async void ProcessClipboardReview()
        {
            try
            {
                string text  = string.Empty;
                bool success = false;

                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        if (Clipboard.ContainsText()) { text = Clipboard.GetText(); success = true; }
                        break;
                    }
                    catch (ExternalException)
                    {
                        await Task.Delay(100);
                    }
                }

                if (success && !string.IsNullOrEmpty(text))
                {
                    LastReviewableContent = text;
                    if (Visibility != Visibility.Visible) ToggleVisibility();

                    AddUserMessage("[Clipboard Auto-Review]");
                    AddSystemMessage("Reviewing clipboard contents...");

                    await DispatchLlmAsync(
                        $"Please analyze, explain, and review the following snippet I just copied:\n\n{text}");
                }
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Failed to read clipboard: {ex.Message}");
            }
        }

        // =========================================================================
        // UI Event Handlers — delegate to services
        // =========================================================================

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void MaximizeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                if (sender is Button btn) btn.Content = "⛶";
            }
            else
            {
                WindowState = WindowState.Maximized;
                if (sender is Button btn) btn.Content = "⧉";
            }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private async void AttachBtn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog();
            if (dialog.ShowDialog() != true) return;

            string path = dialog.FileName;

            try
            {
                var info = new FileInfo(path);
                if (info.Length > 5 * 1024 * 1024)
                {
                    AddSystemMessage(
                        $"File '{Path.GetFileName(path)}' is too large " +
                        $"(>{info.Length / 1024 / 1024}MB). Please attach a smaller file.");
                    return;
                }

                string content;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                    content = await sr.ReadToEndAsync();

                AddUserMessage($"[Attached File: {Path.GetFileName(path)}]");
                AddSystemMessage("Forwarding code to LLM for review...");
                LastReviewableContent = content;

                await DispatchLlmAsync(
                    $"Review this file submission. Provide a thorough, constructive code review " +
                    $"capturing logic errors, security flaws, or bad patterns.\n\n" +
                    $"FILE: {Path.GetFileName(path)}\n\nSOURCE CODE:\n{content}");
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Failed to read attached file: {ex.Message}");
            }
        }

        private async void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            if (Keyboard.Modifiers == ModifierKeys.Shift) return;

            e.Handled = true;

            if (string.IsNullOrWhiteSpace(InputBox.Text)) return;

            string text   = InputBox.Text;
            string trimmed = text.Trim();
            string lower  = trimmed.ToLowerInvariant();
            InputBox.Clear();
            AddUserMessage(text);

            if      (lower == "/save")                  _chatSessionManager.SaveChatSession();
            else if (lower.StartsWith("/activate"))     await HandleActivateCommandAsync(trimmed.Length > 9 ? trimmed[9..].Trim() : "");
            else if (lower.StartsWith("/tutor"))        await HandleTutorCommandAsync(trimmed.Length > 6 ? trimmed[6..].Trim() : "");
            else if (lower.StartsWith("/quick"))        await HandleQuickCommandAsync(trimmed.Length > 6 ? trimmed[6..].Trim() : "");
            else if (lower is "/screen" or "/capture" or "/look")
            {
                AddSystemMessage("[Command] Capturing active window + running OCR...");
                await _ocrService.CaptureActiveWindowForTutorAsync();
            }
            else                                        await DispatchLlmAsync(text);
        }

        // =========================================================================
        // Command Handlers
        // =========================================================================

        private async Task HandleTutorCommandAsync(string content)
        {
            if (!Providers.LicenseManager.IsActivated)
            {
                AddSystemMessage("Tutor Mode requires Workspace Warden Pro. Use /activate <key> or click TUTOR to unlock.");
                ShowActivationWindow();
                return;
            }
            if (string.IsNullOrWhiteSpace(content))
            {
                AddSystemMessage("Usage: /tutor <your code or question>   —  forces Full Tutor mode for this request.");
                return;
            }
            AddSystemMessage("Tutor Mode (forced for this request)...");
            await DispatchLlmAsync(content, forceTutor: true);
        }

        private async Task HandleActivateCommandAsync(string licenseKey)
        {
            if (string.IsNullOrWhiteSpace(licenseKey))
            {
                AddSystemMessage("Usage: /activate <license-key>");
                return;
            }
            AddSystemMessage("Contacting activation server...");
            var (success, message) = await Providers.LicenseManager.ActivateAsync(licenseKey);
            AddSystemMessage(success ? $"[Pro] {message}" : $"[Error] {message}");
        }

        private void ShowActivationWindow()
        {
            var win = new UI.ActivationWindow { Owner = this };
            if (win.ShowDialog() == true)
                AddSystemMessage("[Pro] Workspace Warden Pro is now active. TUTOR and QUIZ modes unlocked.");
        }

        private async Task HandleQuickCommandAsync(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                AddSystemMessage("Usage: /quick <your code or question>   —  forces Quick Review for this request.");
                return;
            }
            bool previous = IsTutorMode;
            IsTutorMode = false;
            try
            {
                AddSystemMessage("Quick Review (forced for this request)...");
                await DispatchLlmAsync(content);
            }
            finally { IsTutorMode = previous; }
        }

        // =========================================================================
        // Settings Panel Handlers
        // =========================================================================

        private void SettingsBox_LostFocus(object sender, RoutedEventArgs e)
        {
            SaveCurrentConfig();
            _ = PopulateModelListAsync();
        }

        private void ProviderSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            SaveCurrentConfig();
            _ = PopulateModelListAsync();
        }

        private void SaveCurrentConfig()
        {
            if (OllamaUrlBox == null || MegaLlmUrlBox == null || ModelSelector == null) return; // Not fully initialized yet

            // Load existing config first so fields we don't set here
            // (HasCompletedTutorial, API keys, etc.) survive the save.
            var config = RevenantWorkspaceWarden.Providers.SecretsManager.LoadConfig();
            config.OllamaBaseUrl = OllamaUrlBox.Text;
            config.MegaLlmBaseUrl = MegaLlmUrlBox.Text;
            config.SelectedProvider = SelectedProvider;
            config.SelectedModel = ModelSelector.SelectedItem?.ToString() ?? "";
            RevenantWorkspaceWarden.Providers.SecretsManager.SaveConfig(config);
        }

        private void ManageApiKeysBtn_Click(object sender, RoutedEventArgs e)
        {
            var apiKeysWindow = new RevenantWorkspaceWarden.UI.ApiKeysWindow
            {
                Owner = this
            };
            apiKeysWindow.ShowDialog();
        }

        private void QuizModeBtn_Checked(object sender, RoutedEventArgs e)
        {
            if (!Providers.LicenseManager.IsActivated)
            {
                QuizModeBtn.IsChecked = false;
                ShowActivationWindow();
                return;
            }

            IsQuizMode  = true;
            IsTutorMode = false;
            if (TutorModeBtn != null) TutorModeBtn.IsChecked = false;
            QuizModeBtn.Foreground  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#dfbe81"));
            QuizModeBtn.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#dfbe81"));
            if (TutorModeBtn != null)
            {
                TutorModeBtn.Foreground  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#888888"));
                TutorModeBtn.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"));
            }
            AddSystemMessage("Quiz Mode — answer + one-line why. Routed to Haiku.");
        }

        private void QuizModeBtn_Unchecked(object sender, RoutedEventArgs e)
        {
            IsQuizMode = false;
            QuizModeBtn.Foreground  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#888888"));
            QuizModeBtn.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"));
        }

        private void TutorModeBtn_Checked(object sender, RoutedEventArgs e)
        {
            if (!Providers.LicenseManager.IsActivated)
            {
                TutorModeBtn.IsChecked = false;
                ShowActivationWindow();
                return;
            }

            IsTutorMode = true;
            IsQuizMode  = false;
            if (QuizModeBtn != null) QuizModeBtn.IsChecked = false;
            TutorModeBtn.Foreground  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3ad5a1"));
            TutorModeBtn.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3ad5a1"));
            if (QuizModeBtn != null)
            {
                QuizModeBtn.Foreground  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#888888"));
                QuizModeBtn.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"));
            }
            AddSystemMessage("Tutor/Code Mode — full breakdown, working code, structured review.");
        }

        private void TutorModeBtn_Unchecked(object sender, RoutedEventArgs e)
        {
            IsTutorMode = false;
            TutorModeBtn.Foreground  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#888888"));
            TutorModeBtn.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"));
        }

        public void SetQuizModeChecked(bool isChecked)
        {
            Dispatcher.Invoke(() =>
            {
                if (QuizModeBtn != null) QuizModeBtn.IsChecked = isChecked;
            });
        }

        private int GetMaxContextLimit(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return 32768;
            string lower = modelName.ToLowerInvariant();
            
            if (lower.Contains("deepseek-coder")) return 16384;
            if (lower.Contains("deepseek-r1")) return 131072;
            if (lower.Contains("embed")) return 8192;
            if (lower.Contains("qwen") || lower.Contains("axiom") || lower.Contains("coder-v-9b") || lower.Contains("chat-v-9b") || lower.Contains("v-9b") || lower.Contains("v-4b") || lower.Contains("omnicoder") || lower.Contains("vanessa")) return 32768;
            
            return 32768; // Default fallback
        }

        private void ModelSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ModelSelector.SelectedItem is string selectedModel && ContextBox != null)
            {
                int maxLimit = GetMaxContextLimit(selectedModel);
                ContextBox.Text = maxLimit.ToString();
            }
        }

        private void ContextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (ModelSelector.SelectedItem is string selectedModel)
            {
                int maxLimit = GetMaxContextLimit(selectedModel);
                if (int.TryParse(ContextBox.Text, out int currentValue))
                {
                    if (currentValue > maxLimit)
                    {
                        ContextBox.Text = maxLimit.ToString();
                        AddSystemMessage($"Context limit clamped to {maxLimit} for {selectedModel}.");
                    }
                }
                else
                {
                    ContextBox.Text = maxLimit.ToString();
                }
            }
        }
    }
}