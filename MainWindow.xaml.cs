using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
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
        private CancellationTokenSource? _activeGeneration; // in-flight LLM generation; Stop button cancels it

        // ── Services ──────────────────────────────────────────────────────────────
        private OllamaService       _ollamaService      = null!;
        private OcrService          _ocrService         = null!;
        private ChatSessionManager  _chatSessionManager = null!;

        // ── Sidekick / Code Telemetry / State Cache ───────────────────────────────
        private readonly ObservableCollection<SidekickFindingVm> _sidekickFindings = new();
        private string _sidekickScanPath = "";
        private readonly DispatcherTimer _saveCourseCacheTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _memTimer      = new() { Interval = TimeSpan.FromSeconds(5) };
        private readonly DispatcherTimer _saveCacheTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private string _currentFilePath    = "";
        private string _currentLang        = "";
        private string _currentFileContent = "";
        private string _lastWorkspaceRoot  = "";

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

        // DWM window attributes: paint the title bar in dark mode with the house
        // colors. Without this Windows draws the caption in the user's accent
        // color, which on a fully obsidian app renders as an olive bar across the
        // top and makes the theme look like it stops at the frame.
        // Ported from Sidekick 2026-08-03 so the pair match at the frame too.
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaBorderColor          = 34;
        private const int DwmwaCaptionColor         = 35;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

        public MainWindow()
        {
            InitializeComponent();
            ChatHistory.ItemsSource      = _messages;
            SidekickFindingsList.ItemsSource = _sidekickFindings;

            _ollamaService      = new OllamaService(this);
            _ocrService         = new OcrService(this);
            _chatSessionManager = new ChatSessionManager(this);

            SourceInitialized += ApplyDarkCaption;

            AddSystemMessage("Revenant Workspace Warden initialized. Alt+Enter to hide.");
        }

        private void ApplyDarkCaption(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == 0)
                return;

            // Best effort only. Older Windows builds return a failure HRESULT, in
            // which case the caption just stays default and nothing else breaks.
            var enabled = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));

            // COLORREF is 0x00BBGGRR, NOT RGB. Obsidian #050506 -> 0x00060505.
            var caption = 0x00060505;
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));

            // Gold hairline frame, #7A633E -> 0x003E637A.
            var border = 0x003E637A;
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
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
                _rebindActiveBtn.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#dfbe81"));
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

            LoadStateCache();

            _memTimer.Tick += (s, ev) =>
            {
                var mb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
                Dispatcher.Invoke(() => MemoryLabel.Text = $"Memory: {mb} MB");
            };
            _memTimer.Start();

            _saveCacheTimer.Tick   += (s, ev) => { _saveCacheTimer.Stop();       SaveStateCache();  };
            _saveCourseCacheTimer.Tick += (s, ev) => { _saveCourseCacheTimer.Stop(); SaveCourseData(); };

            LoadCourseData();

            await PopulateModelListAsync(config.SelectedModel);

            // First run: walk them to the model settings. This lives here rather
            // than on the expander's Expanded event, and runs after the model
            // list is populated so the ModelSelector step isn't pointing at an
            // empty combo box.
            if (!config.HasCompletedTutorial)
                await StartTutorialAsync();
        }

        // ── First-run tutorial ────────────────────────────────────────────────
        // Rewritten 2026-08-09. Four faults in the original:
        //   1. It was triggered by EngineSettingsExpander_Expanded, yet every
        //      step points at a control INSIDE that expander — so it only ran
        //      for someone who had already found the panel it exists to teach.
        //      A new user never saw it. That is why it looked "off".
        //   2. HighlightControl was `async void` with `await Task.Delay(50)`
        //      guessing at layout, so nothing awaited it and steps could race.
        //   3. The popup was HorizontalAlignment="Center", parking it mid-window
        //      instead of beside the thing it was pointing at.
        //   4. On failure it set the hole to Rect(0,0,0,0) — a full-screen black
        //      overlay with no cutout, which is the visible symptom Dave hit.
        // Scope is deliberately the get-a-model-working path and stops at the
        // API key step; the chat box is self-evident and carries its own hint.

        private int _tutorialStep = 0;

        private (FrameworkElement? target, string text, bool needsSettingsOpen) GetTutorialStep(int i) => i switch
        {
            0 => (EngineSettingsExpander,
                  "Warden needs a model before it can answer anything. It's all under Engine Settings — press Next and I'll open it for you.", false),
            1 => (OllamaUrlBox,
                  "Running a local engine like Ollama? This is its address. The default is right for a standard Ollama install.", true),
            2 => (ProviderSelector,
                  "Pick your provider — local Ollama, or a cloud service like Anthropic, OpenAI, Gemini, Groq or xAI.", true),
            3 => (ModelSelector,
                  "Then choose a model. This list refreshes to match the provider you picked.", true),
            4 => (ManageApiKeysBtn,
                  "Using a cloud provider? Add your API key here. Keys are encrypted with Windows DPAPI and never leave this machine.", true),
            _ => (null, string.Empty, false)
        };

        private async Task StartTutorialAsync()
        {
            _tutorialStep = 0;
            SpotlightOverlay.Visibility = Visibility.Visible;
            await ShowTutorialStepAsync();
        }

        private async Task ShowTutorialStepAsync()
        {
            // Skip a step whose control cannot be resolved rather than blacking
            // out the window with a zero-size hole.
            while (true)
            {
                var (target, text, needsSettingsOpen) = GetTutorialStep(_tutorialStep);
                if (target is null) { EndTutorial(); return; }

                if (needsSettingsOpen && !EngineSettingsExpander.IsExpanded)
                {
                    EngineSettingsExpander.IsExpanded = true;
                    await WaitForLayoutAsync();
                }

                if (await HighlightControlAsync(target, text))
                    return;

                _tutorialStep++;
            }
        }

        // ContextIdle runs after layout and render have completed. That is what
        // the old Task.Delay(50) was trying to approximate by feel.
        private Task WaitForLayoutAsync()
        {
            UpdateLayout();
            return Dispatcher.InvokeAsync(() => { },
                System.Windows.Threading.DispatcherPriority.ContextIdle).Task;
        }

        private async Task<bool> HighlightControlAsync(FrameworkElement target, string text)
        {
            target.BringIntoView();
            await WaitForLayoutAsync();

            if (!target.IsVisible || target.ActualWidth <= 0 || target.ActualHeight <= 0)
                return false;

            try
            {
                Point topLeft = target.TransformToVisual(SpotlightOverlay).Transform(new Point(0, 0));
                const double pad = 5;
                const double gap = 12;

                SpotlightHole.Rect = new Rect(
                    topLeft.X - pad,
                    topLeft.Y - pad,
                    target.ActualWidth + (pad * 2),
                    target.ActualHeight + (pad * 2));

                // Set the text and let the card measure BEFORE positioning it.
                // ActualHeight is 0 until it has been through a layout pass, and
                // clamping against a zero height is what pushed the Skip/Next
                // buttons off the bottom of the window on the first step.
                TutorialText.Text = text;
                await WaitForLayoutAsync();

                double cardH = TutorialPopup.ActualHeight;
                double cardW = TutorialPopup.ActualWidth;
                if (cardH <= 0) cardH = 150;   // belt and braces if it still hasn't measured
                if (cardW <= 0) cardW = 300;   // matches MaxWidth in the XAML

                double belowY = topLeft.Y + target.ActualHeight + pad + gap;
                double aboveY = topLeft.Y - pad - gap - cardH;

                // Prefer below the target; flip above when there isn't room —
                // the Engine Settings header sits near the bottom of the window,
                // so "below" runs off the edge for the very first step.
                double top =
                    (belowY + cardH + gap <= SpotlightOverlay.ActualHeight) ? belowY
                    : (aboveY >= gap) ? aboveY
                    : Math.Max(gap, SpotlightOverlay.ActualHeight - cardH - gap);

                double left = Math.Max(gap,
                    Math.Min(topLeft.X, Math.Max(gap, SpotlightOverlay.ActualWidth - cardW - gap)));

                TutorialPopup.Margin = new Thickness(left, top, 0, 0);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false; // not in the visual tree — caller moves to the next step
            }
        }

        private async void NextTutorial_Click(object sender, RoutedEventArgs e)
        {
            _tutorialStep++;
            await ShowTutorialStepAsync();
        }

        private void SkipTutorial_Click(object sender, RoutedEventArgs e) => EndTutorial();

        private void EndTutorial()
        {
            SpotlightOverlay.Visibility = Visibility.Collapsed;
            SpotlightHole.Rect = new Rect(0, 0, 0, 0);
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
                // PNGs, not the .ico. WPF's BitmapImage takes Frames[0] of a
                // multi-frame icon — the 16x16 — and Windows would upscale that
                // into alt-tab and the taskbar. The .ico is for the exe only.
                if (isError)
                {
                    this.Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/RWW_RAGE.png"));
                }
                else
                {
                    this.Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/rww_logo.png"));
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
            // Cancel any generation still in flight, then own a fresh token for this one.
            _activeGeneration?.Cancel();
            var cts = new CancellationTokenSource();
            _activeGeneration = cts;
            try
            {
                string? aiResponse = await _ollamaService.SendToProviderAsync(prompt, lessonContext, cts.Token);
                SetWardenState(false); // Success -> Sage Gold
                return aiResponse;
            }
            catch (OperationCanceledException)
            {
                // User hit Stop — a clean halt, not an error. No Rage state.
                AddSystemMessage("[Warden] Stopped.");
                return null;
            }
            catch (Exception ex)
            {
                SetWardenState(true); // Error -> Rage Mode
                AddSystemMessage($"[Warden Error]: {ex.Message}");
                return null;
            }
            finally
            {
                // Only clear the field if a newer dispatch hasn't already replaced it.
                if (ReferenceEquals(_activeGeneration, cts)) _activeGeneration = null;
                cts.Dispose();
                SetLoadingState(false); // safety net — providers normally handle this
            }
        }

        private void StopGeneration_Click(object sender, RoutedEventArgs e) => _activeGeneration?.Cancel();

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
            else { Show(); Activate(); CodeViewer.Focus(); }
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
            await SubmitInputAsync();
        }

        private async void SendBtn_Click(object sender, RoutedEventArgs e) => await SubmitInputAsync();

        // WPF has no placeholder, so the hint is a TextBlock behind the box.
        private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
            => InputHint.Visibility = string.IsNullOrEmpty(InputBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;

        // Shared by Enter and the SEND button so both take exactly the same path.
        private async Task SubmitInputAsync()
        {
            if (string.IsNullOrWhiteSpace(InputBox.Text)) return;

            string text   = InputBox.Text;
            string trimmed = text.Trim();
            string lower  = trimmed.ToLowerInvariant();
            InputBox.Clear();
            InputBox.Focus();
            AddUserMessage(text);

            if      (lower == "/save")                  _chatSessionManager.SaveChatSession();
            else if (lower.StartsWith("/activate"))     await HandleActivateCommandAsync(trimmed.Length > 9 ? trimmed[9..].Trim() : "");
            // Exact match MUST precede the /tutor prefix check below, or
            // "/tutorial" matches StartsWith("/tutor") and gets parsed as a
            // tutor request for the content "ial".
            else if (lower == "/tutorial")              await StartTutorialAsync();
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
            AddSystemMessage("Validating license key...");
            var success = Providers.LicenseManager.ValidateAndActivate(licenseKey);
            AddSystemMessage(success
                ? "[Pro] Activation successful. Pro features are now unlocked."
                : "[Error] Invalid license key. Check your key and try again.");
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
            WarnIfProviderKeyMissing();
            _ = PopulateModelListAsync();
        }

        // Cloud providers that require a stored API key (Ollama is local and needs none).
        private static readonly System.Collections.Generic.Dictionary<string, string> ProviderKeyNames = new()
        {
            ["OpenAI"]     = "OPENAI_API_KEY",
            ["OpenRouter"] = "OPENROUTER_API_KEY",
            ["Anthropic"]  = "ANTHROPIC_API_KEY",
            ["Groq"]       = "GROQ_API_KEY",
            ["xAI"]        = "XAI_API_KEY",
            ["Gemini"]     = "GEMINI_API_KEY",
            ["MegaLLM"]    = "MEGALLM_API_KEY",
        };

        private void WarnIfProviderKeyMissing()
        {
            string provider = SelectedProvider;
            if (ProviderKeyNames.TryGetValue(provider, out var keyName)
                && !RevenantWorkspaceWarden.Providers.SecretsManager.HasEncryptedKey(keyName))
            {
                AddSystemMessage($"[Setup] {provider} has no API key yet — add one via Manage API Keys, or pick a provider you've already configured.");
            }
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

        // =========================================================================
        // Workspace Tree
        // =========================================================================

        private void SetWorkspaceRoot_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select workspace root" };
            if (dialog.ShowDialog() != true) return;
            LoadWorkspaceRoot(dialog.FolderName);
            DeferSaveStateCache();
        }

        private void LoadWorkspaceRoot(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            _lastWorkspaceRoot = path;
            var nodes = new List<FileSystemNode>();
            try
            {
                foreach (var dir in Directory.GetDirectories(path))
                    nodes.Add(new FileSystemNode(dir, true));
                foreach (var file in Directory.GetFiles(path))
                    nodes.Add(new FileSystemNode(file, false));
            }
            catch { }
            WorkspaceTree.ItemsSource = nodes;
            AddSystemMessage($"[Workspace] {path}");
        }

        private void WorkspaceTree_SelectedItemChanged(
            object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is FileSystemNode node && !node.IsDirectory)
                LoadFileToCenter(node.FullPath);
        }

        private void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
        {
            if (sender is TreeViewItem item && item.DataContext is FileSystemNode node)
                node.LoadChildren();
        }

        // =========================================================================
        // Code Telemetry
        // =========================================================================

        private void LoadFileToCenter(string path)
        {
            _currentFilePath = path;
            _currentLang     = GetLanguageLabel(Path.GetExtension(path));
            FileBreadcrumb.Text = path;
            LangBadge.Text      = _currentLang;

            try
            {
                var info = new FileInfo(path);
                if (info.Length > 512 * 1024)
                {
                    _currentFileContent = "";
                    var shortDoc = new FlowDocument();
                    shortDoc.Blocks.Add(new Paragraph(
                        new Run($"[File too large: {info.Length / 1024} KB — open in editor]")));
                    CodeViewer.Document = shortDoc;
                    MetricsBar.Children.Clear();
                    return;
                }
                _currentFileContent = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                AddSystemMessage($"[Code Viewer] {ex.Message}");
                return;
            }

            var flowDoc = new FlowDocument { FontFamily = new FontFamily("Consolas"), FontSize = 12 };
            flowDoc.Blocks.Add(new Paragraph(new Run(_currentFileContent)));
            CodeViewer.Document = flowDoc;

            MetricsBar.Children.Clear();
            foreach (var (label, value) in ComputeMetrics(_currentFileContent, Path.GetExtension(path)))
                MetricsBar.Children.Add(MakeMetricChip($"{label}: {value}"));
        }

        private static string GetLanguageLabel(string ext) => ext.ToLowerInvariant() switch
        {
            ".cs"   => "C#",
            ".py"   => "PY",
            ".ts"   => "TS",
            ".tsx"  => "TSX",
            ".js"   => "JS",
            ".jsx"  => "JSX",
            ".rs"   => "RS",
            ".go"   => "GO",
            ".xaml" => "XAML",
            ".xml"  => "XML",
            ".json" => "JSON",
            ".md"   => "MD",
            ".html" => "HTML",
            ".css"  => "CSS",
            ".sql"  => "SQL",
            _       => "TXT"
        };

        private List<(string, string)> ComputeMetrics(string content, string ext)
        {
            int lines      = content.Split('\n').Length;
            int todos      = Regex.Matches(content, @"//\s*TODO", RegexOptions.IgnoreCase).Count;
            int complexity = Regex.Matches(content,
                @"\b(if|else if|for|foreach|while|switch|case|catch)\b").Count;
            string complexLabel = complexity < 10 ? "Low" : complexity < 30 ? "Medium" : "High";
            long bytes = 0;
            try { bytes = new FileInfo(_currentFilePath).Length; } catch { }
            string sizeStr = bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024} KB";
            return new List<(string, string)>
            {
                ("Lines",      lines.ToString()),
                ("TODOs",      todos.ToString()),
                ("Complexity", complexLabel),
                ("Size",       sizeStr)
            };
        }

        private static Border MakeMetricChip(string text) => new Border
        {
            BorderBrush     = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D3748")),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(3),
            Padding         = new Thickness(6, 2, 6, 2),
            Margin          = new Thickness(2, 0, 2, 0),
            Child = new TextBlock
            {
                Text       = text,
                FontFamily = new FontFamily("Consolas"),
                FontSize   = 11,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A8B5C8"))
            }
        };

        private async void AnalyzeWithWarden_Click(object sender, RoutedEventArgs e)
        {
            // Prefer loaded-file content; fall back to whatever is in the chalkboard
            string code = _currentFileContent;
            if (string.IsNullOrEmpty(code))
            {
                var range = new TextRange(CodeViewer.Document.ContentStart, CodeViewer.Document.ContentEnd);
                code = range.Text.Trim();
            }

            if (string.IsNullOrEmpty(code))
            {
                AddSystemMessage("[Warden] Paste or load code into the chalkboard first.");
                return;
            }

            string fileLabel = string.IsNullOrEmpty(_currentFilePath)
                ? "pasted code"
                : Path.GetFileName(_currentFilePath);
            string langLabel = string.IsNullOrEmpty(_currentLang) ? "code" : _currentLang;

            AddUserMessage($"[Check: {fileLabel}]");
            AddSystemMessage("Sending to Warden for review...");

            string modeHint = IsTutorMode
                ? "Give a thorough teaching review with before/after code examples and teaching notes."
                : "Give a concise plain-text review: what's wrong and what to fix.";

            string prompt =
                $"{modeHint}\n\n" +
                $"FILE: {fileLabel}  LANGUAGE: {langLabel}\n\n" +
                $"CODE:\n{code}";

            // Inject course context if the COURSE panel is open and has content
            if (Providers.LicenseManager.IsActivated && CourseContent.Visibility == Visibility.Visible)
            {
                string syllabus   = CourseSyllabusBox.Text.Trim();
                string assignment = CourseAssignmentBox.Text.Trim();
                if (!string.IsNullOrEmpty(syllabus) || !string.IsNullOrEmpty(assignment))
                {
                    prompt += "\n\n--- COURSE CONTEXT ---";
                    if (!string.IsNullOrEmpty(syllabus))   prompt += $"\nSyllabus:\n{syllabus}";
                    if (!string.IsNullOrEmpty(assignment)) prompt += $"\n\nAssignment:\n{assignment}";
                }
            }

            await DispatchLlmAsync(prompt);
        }

        // =========================================================================
        // Workspace Panel Toggle
        // =========================================================================

        private void WorkspaceToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            bool collapsed = WorkspaceCol.Width.Value == 0;
            WorkspaceCol.Width         = new GridLength(collapsed ? 220 : 0);
            WorkspaceToggleBtn.Content = collapsed ? "◄ WS" : "WS ►";
        }

        // =========================================================================
        // Sidekick Panel (RWS — Revenant Workspace Sidekick)
        // =========================================================================

        private void SidekickBtn_Click(object sender, RoutedEventArgs e)
        {
            bool opening = SidekickCol.Width.Value == 0;
            SidekickCol.Width   = new GridLength(opening ? 300 : 0);
            SidekickBtn.Content = opening ? "◄ SIDEKICK" : "SIDEKICK ►";

            if (opening)
            {
                bool licensed = Providers.LicenseManager.IsActivated;
                SidekickUpsell.Visibility  = licensed ? Visibility.Collapsed : Visibility.Visible;
                SidekickScanner.Visibility = licensed ? Visibility.Visible   : Visibility.Collapsed;
            }
        }

        private void SidekickActivate_Click(object sender, RoutedEventArgs e)
        {
            var win = new UI.ActivationWindow();
            if (win.ShowDialog() == true)
            {
                SidekickUpsell.Visibility  = Visibility.Collapsed;
                SidekickScanner.Visibility = Visibility.Visible;
            }
        }

        private void SidekickBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Select Project Folder to Scan" };
            if (dialog.ShowDialog() == true)
            {
                _sidekickScanPath    = dialog.FolderName;
                SidekickPathBox.Text = dialog.FolderName;
            }
        }

        private async void SidekickScan_Click(object sender, RoutedEventArgs e)
        {
            string path = SidekickPathBox.Text.Trim();
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                SidekickStatusLbl.Text = "Select a project folder first.";
                return;
            }

            SidekickScanBtn.IsEnabled      = false;
            SidekickScanBtn.Content        = "⏳  SCANNING...";
            SidekickGradeBanner.Visibility = Visibility.Collapsed;
            _sidekickFindings.Clear();
            SidekickStatusLbl.Text         = "Scanning...";

            try
            {
                var options = RevenantWorkspaceSidekick.Core.ScanOptions.Default(path);
                var result  = await Task.Run(() =>
                    RevenantWorkspaceSidekick.Core.RuleEngine.Scan(
                        RevenantWorkspaceSidekick.Rules.RuleRegistry.All, options));

                foreach (var f in result.Findings)
                    _sidekickFindings.Add(new SidekickFindingVm(f));

                var gradeColor = GradeColor(result.Grade);
                SidekickGradeLbl.Text             = result.Grade.ToString();
                SidekickGradeLbl.Foreground        = gradeColor;
                SidekickScoreLbl.Text             = $"Score: {result.Score}/100";
                SidekickSummaryLbl.Text           = $"{result.Findings.Count} findings  ·  {result.FilesScanned} files";
                SidekickGradeBanner.BorderBrush   = gradeColor;
                SidekickGradeBanner.Visibility    = Visibility.Visible;

                SidekickStatusLbl.Text = $"Done in {result.Duration.TotalSeconds:F1}s  ·  hover a finding for Why/Fix";
                SidekickFindingsScroller.ScrollToTop();
            }
            catch (Exception ex)
            {
                SidekickStatusLbl.Text = $"Scan error: {ex.Message}";
            }
            finally
            {
                SidekickScanBtn.IsEnabled = true;
                SidekickScanBtn.Content   = "▶  SCAN PROJECT";
            }
        }

        private static SolidColorBrush GradeColor(char grade) => grade switch
        {
            'A' => new SolidColorBrush(Color.FromRgb(0x3A, 0xD5, 0xA1)),
            'B' => new SolidColorBrush(Color.FromRgb(0x6B, 0xC4, 0x6B)),
            'C' => new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)),
            'D' => new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x40)),
            _   => new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4D)),
        };

        // =========================================================================
        // AppData State Cache
        // =========================================================================

        private void DeferSaveStateCache()
        {
            _saveCacheTimer.Stop();
            _saveCacheTimer.Start();
        }

        private void LoadStateCache()
        {
            var cachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevenantWorkspaceWarden", "workspacestate.json");
            if (!File.Exists(cachePath)) return;
            try
            {
                using var doc  = JsonDocument.Parse(File.ReadAllText(cachePath));
                var root = doc.RootElement;
                if (root.TryGetProperty("workspaceRoot", out var wr) &&
                    wr.GetString() is string path && Directory.Exists(path))
                    LoadWorkspaceRoot(path);
            }
            catch { }
        }

        private void SaveStateCache()
        {
            try
            {
                var cacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RevenantWorkspaceWarden");
                Directory.CreateDirectory(cacheDir);
                var state = new { workspaceRoot = _lastWorkspaceRoot };
                File.WriteAllText(
                    Path.Combine(cacheDir, "workspacestate.json"),
                    JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        // =========================================================================
        // COURSE Panel
        // =========================================================================

        private void CourseBtn_Click(object sender, RoutedEventArgs e)
        {
            bool opening = CourseCol.Width.Value == 0;
            CourseCol.Width   = new GridLength(opening ? 300 : 0);
            CourseBtn.Content = opening ? "◄ COURSE" : "COURSE ►";

            if (opening)
            {
                bool licensed = Providers.LicenseManager.IsActivated;
                CourseUpsell.Visibility  = licensed ? Visibility.Collapsed : Visibility.Visible;
                CourseContent.Visibility = licensed ? Visibility.Visible   : Visibility.Collapsed;
            }
        }

        private void CourseActivate_Click(object sender, RoutedEventArgs e)
        {
            var win = new UI.ActivationWindow { Owner = this };
            if (win.ShowDialog() == true)
            {
                CourseUpsell.Visibility  = Visibility.Collapsed;
                CourseContent.Visibility = Visibility.Visible;
                AddSystemMessage("[Pro] Course Mode unlocked. Upload your syllabus and current assignment.");
            }
        }

        private void CourseSyllabusUpload_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title  = "Select Syllabus File",
                Filter = "Text files|*.txt;*.md|All files|*.*"
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                CourseSyllabusBox.Text = File.ReadAllText(dialog.FileName);
                AddSystemMessage($"[Course] Syllabus loaded from {Path.GetFileName(dialog.FileName)}");
            }
            catch (Exception ex)
            {
                AddSystemMessage($"[Course] Failed to read file: {ex.Message}");
            }
        }

        private void CourseText_Changed(object sender, TextChangedEventArgs e)
        {
            _saveCourseCacheTimer.Stop();
            _saveCourseCacheTimer.Start();
        }

        private async void CourseWardenNotes_Click(object sender, RoutedEventArgs e)
        {
            string syllabus   = CourseSyllabusBox.Text.Trim();
            string assignment = CourseAssignmentBox.Text.Trim();

            if (string.IsNullOrEmpty(syllabus) && string.IsNullOrEmpty(assignment))
            {
                AddSystemMessage("[Course] Load a syllabus or paste an assignment first.");
                return;
            }

            AddUserMessage("[Course] Warden Notes");

            string prompt = "You are a study assistant. Based on the course material below, produce concise, useful notes:\n" +
                            "1. Key topics and concepts by unit/week\n" +
                            "2. Likely difficulty spikes — what to spend extra time on\n" +
                            "3. Study priority order\n";

            if (!string.IsNullOrEmpty(assignment))
                prompt += "4. Specific guidance for the current assignment\n";

            prompt += "\n";

            if (!string.IsNullOrEmpty(syllabus))
                prompt += $"SYLLABUS:\n{syllabus}\n\n";

            if (!string.IsNullOrEmpty(assignment))
                prompt += $"CURRENT ASSIGNMENT:\n{assignment}";

            await DispatchLlmAsync(prompt);
        }

        private void CourseClear_Click(object sender, RoutedEventArgs e)
        {
            CourseSyllabusBox.Text   = "";
            CourseAssignmentBox.Text = "";
            SaveCourseData();
            AddSystemMessage("[Course] Course data cleared.");
        }

        private void LoadCourseData()
        {
            var cachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevenantWorkspaceWarden", "coursestate.json");
            if (!File.Exists(cachePath)) return;
            try
            {
                using var doc  = JsonDocument.Parse(File.ReadAllText(cachePath));
                var root = doc.RootElement;
                if (root.TryGetProperty("syllabus",   out var s)) CourseSyllabusBox.Text   = s.GetString() ?? "";
                if (root.TryGetProperty("assignment", out var a)) CourseAssignmentBox.Text = a.GetString() ?? "";
            }
            catch { }
        }

        private void SaveCourseData()
        {
            try
            {
                var cacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RevenantWorkspaceWarden");
                Directory.CreateDirectory(cacheDir);
                var state = new { syllabus = CourseSyllabusBox.Text, assignment = CourseAssignmentBox.Text };
                File.WriteAllText(
                    Path.Combine(cacheDir, "coursestate.json"),
                    JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    // =========================================================================
    // SidekickFindingVm — view-model wrapper for RWS Finding records
    // =========================================================================

    internal sealed class SidekickFindingVm
    {
        public string RuleId        { get; }
        public string Title         { get; }
        public string FileLine      { get; }
        public string Why           { get; }
        public string Fix           { get; }
        public string SeverityLabel { get; }
        public Brush  SeverityBrush { get; }

        public SidekickFindingVm(RevenantWorkspaceSidekick.Core.Models.Finding f)
        {
            RuleId        = f.RuleId;
            Title         = f.Title;
            FileLine      = f.Line.HasValue ? $"{f.File}:{f.Line}" : f.File;
            Why           = f.Why;
            Fix           = f.Fix;
            SeverityLabel = f.Severity.ToString().ToUpper();
            // Revenant house severity ramp, set by Dave 2026-07-30. Five levels,
            // five colors: light blue, yellow, orange, red, deep crimson. Matches
            // Sidekick's Theme/RevenantTheme.xaml, its SeverityBrushes.cs, and the
            // HtmlReporter in Sidekick.Core that this project already references — so
            // a finding reads the same color in Warden, in Sidekick, and in a report.
            //
            // Before this, Warden ran its own palette (#FF4D4D / #FFA040 / #FFD700)
            // and collapsed BOTH Low and Info into grey. They are distinct now.
            //
            // ⚠ The XAML paints this brush as a SOLID badge fill with black 9px text
            // on it, and Critical #8C1436 measures about 2.3:1 against black — well
            // under the 4.5 AA line and effectively unreadable at that size. Left as
            // is on purpose: the Warden UI revamp is the next piece of work and will
            // replace this badge treatment wholesale. Fix it there, not here.
            SeverityBrush = f.Severity switch
            {
                RevenantWorkspaceSidekick.Core.Models.Severity.Critical =>
                    new SolidColorBrush(Color.FromRgb(0x8C, 0x14, 0x36)),   // deep crimson
                RevenantWorkspaceSidekick.Core.Models.Severity.High =>
                    new SolidColorBrush(Color.FromRgb(0xDA, 0x2B, 0x26)),   // red
                RevenantWorkspaceSidekick.Core.Models.Severity.Medium =>
                    new SolidColorBrush(Color.FromRgb(0xF5, 0x9A, 0x20)),   // orange
                RevenantWorkspaceSidekick.Core.Models.Severity.Low =>
                    new SolidColorBrush(Color.FromRgb(0xF0, 0xDE, 0x1B)),   // yellow
                _ =>
                    new SolidColorBrush(Color.FromRgb(0x7F, 0xA6, 0xC9)),   // light blue (Info)
            };
        }
    }

    // =========================================================================
    // FileSystemNode — lazy-loading workspace tree node
    // =========================================================================

    public class FileSystemNode
    {
        public string Name      { get; set; }
        public string FullPath  { get; set; }
        public bool   IsDirectory { get; set; }
        public ObservableCollection<FileSystemNode> Children { get; set; } = new();
        private bool _loaded;

        public FileSystemNode(string fullPath, bool isDirectory)
        {
            FullPath    = fullPath;
            Name        = System.IO.Path.GetFileName(fullPath);
            IsDirectory = isDirectory;
            if (isDirectory)
                Children.Add(new FileSystemNode("", false) { Name = "..." });
        }

        public void LoadChildren()
        {
            if (_loaded || !IsDirectory) return;
            _loaded = true;
            Children.Clear();
            try
            {
                foreach (var dir in Directory.GetDirectories(FullPath))
                    Children.Add(new FileSystemNode(dir, true));
                foreach (var file in Directory.GetFiles(FullPath))
                    Children.Add(new FileSystemNode(file, false));
            }
            catch { }
        }
    }
}