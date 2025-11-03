using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace StickyMiniWeb
{
    public partial class MainWindow : Window
    {
        private const int DefaultAutoRefreshIntervalSeconds = 30;
        private const int MinimumAutoRefreshIntervalSeconds = 5;
        private const string DefaultBackgroundHex = "FFFFFF";
        private const string TypingStateMessageType = "typing-state";
        private static readonly string TypingStateMonitoringScript = @"
(function () {
    if (!window.chrome || !window.chrome.webview) {
        return;
    }
    const send = (isTyping) => {
        window.chrome.webview.postMessage({ type: 'typing-state', isTyping });
    };
    let lastValue = null;
    const isTypingElement = (el) => {
        if (!el) {
            return false;
        }
        if (el.isContentEditable) {
            return true;
        }
        if (!el.tagName) {
            return false;
        }
        if (el.tagName === 'TEXTAREA') {
            return true;
        }
        if (el.tagName !== 'INPUT') {
            return false;
        }
        const inputType = (el.type || '').toLowerCase();
        const nonTypingTypes = ['button', 'submit', 'reset', 'checkbox', 'radio', 'range', 'color', 'file'];
        return !nonTypingTypes.includes(inputType);
    };
    const report = () => {
        const typing = isTypingElement(document.activeElement);
        if (typing !== lastValue) {
            lastValue = typing;
            send(typing);
        }
    };
    document.addEventListener('focus', report, true);
    document.addEventListener('blur', report, true);
    document.addEventListener('input', report, true);
    document.addEventListener('keydown', report, true);
    document.addEventListener('keyup', report, true);
    document.addEventListener('compositionstart', report, true);
    document.addEventListener('compositionend', report, true);
    report();
})();
";

        private DispatcherTimer? _autoRefreshTimer;
        private CancellationTokenSource? _autoRefreshCts;
        private bool _autoRefreshInFlight;
        private bool _isTypingInWebView;

        private bool _isTaskbarOnlyMode;
        private bool _settingsVisibleBeforeCompact;
        private bool _suppressTaskbarToggleHandler;
        private bool _suppressBackgroundTextUpdate;
        private double _previousWindowHeight = double.NaN;
        private double _previousMinHeight;
        private double _previousMaxHeight = double.PositiveInfinity;
        private Color _currentBackgroundColor = Colors.White;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += (_, _) => UpdateTaskbarToggleHeight();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                LoadLastState();

                await Web.EnsureCoreWebView2Async();
                Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                Web.CoreWebView2.DocumentTitleChanged += CoreWebView2_DocumentTitleChanged;
                Web.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                Web.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
                await Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(TypingStateMonitoringScript);

                NavigateToUrl(UrlBox.Text);

                UpdateTaskbarToggleHeight();
                UpdateTaskbarModeToggle(_isTaskbarOnlyMode);
            }
            catch (Exception ex)
            {
                MessageBox.Show("WebView2 の初期化に失敗しました: " + ex.Message);
            }
        }

        private void CoreWebView2_DocumentTitleChanged(object? sender, object? e)
        {
            if (!string.IsNullOrEmpty(Web?.CoreWebView2?.DocumentTitle))
            {
                Title = Web!.CoreWebView2!.DocumentTitle;
            }
        }

        private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            var message = e.WebMessageAsJson;
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(message);
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var typeElement) &&
                    string.Equals(typeElement.GetString(), TypingStateMessageType, StringComparison.Ordinal) &&
                    root.TryGetProperty("isTyping", out var typingElement))
                {
                    _isTypingInWebView = typingElement.GetBoolean();
                }
            }
            catch (JsonException)
            {
                // ignore malformed messages
            }
        }

        private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            // Cancel the default behavior of opening in WebView2
            e.Handled = true;

            // Open the URL in the OS default browser
            try
            {
                var uri = e.Uri;
                if (!string.IsNullOrEmpty(uri))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = uri,
                        UseShellExecute = true
                    });
                }
            }
            catch
            {
                // ignore errors when opening in default browser
            }
        }

        private void UrlBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Open_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            SaveCurrentState();
            StopAutoRefresh();
        }

        private void ShowSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SetSettingsVisibility(!AreSettingsVisible);
        }

        private bool AreSettingsVisible => UrlBox != null && UrlBox.Visibility == Visibility.Visible;

        private void SetSettingsVisibility(bool show)
        {
            if (UrlBox == null) return;

            var visibility = show ? Visibility.Visible : Visibility.Collapsed;

            UrlBox.Visibility = visibility;
            OpenButton.Visibility = visibility;
            TopmostToggle.Visibility = visibility;
            AutoRefreshPanel.Visibility = visibility;
            BackgroundColorPanel.Visibility = visibility;
            ShowSettingsButton.IsChecked = show;
            ShowSettingsButton.Background = show ? Brushes.DodgerBlue : Brushes.Transparent;
        }

        private void TaskbarModeToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressTaskbarToggleHandler) return;
            SetTaskbarOnlyMode(true);
        }

        private void TaskbarModeToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_suppressTaskbarToggleHandler) return;
            SetTaskbarOnlyMode(false);
        }

        private void UpdateTaskbarModeToggle(bool enable)
        {
            if (TaskbarModeToggle == null) return;
            _suppressTaskbarToggleHandler = true;
            TaskbarModeToggle.IsChecked = enable;
            _suppressTaskbarToggleHandler = false;

            if (TaskbarModeIcon != null)
            {
                TaskbarModeIcon.Text = enable ? "\uE96E" : "\uE96D";
            }
        }

        private void UpdateTaskbarToggleHeight()
        {
            if (TaskbarModeToggle == null) return;
            var resolved = Math.Max(FontSize * 0.6, 14);
            TaskbarModeToggle.Height = resolved * 1.15;
        }

        private void SetTaskbarOnlyMode(bool enable)
        {
            UpdateTaskbarModeToggle(enable);

            if (_isTaskbarOnlyMode == enable)
            {
                return;
            }

            if (enable)
            {
                _previousWindowHeight = Height;
                _previousMinHeight = MinHeight;
                _previousMaxHeight = MaxHeight;
                _settingsVisibleBeforeCompact = AreSettingsVisible;
                SetSettingsVisibility(false);
                ShowSettingsButton.IsEnabled = false;

                if (ContentRow != null)
                {
                    ContentRow.Height = new GridLength(0);
                }
                if (Web != null)
                {
                    Web.Visibility = Visibility.Collapsed;
                }

                var toggleHeight = TaskbarModeToggle?.ActualHeight ?? 0;
                if (toggleHeight <= 0)
                {
                    toggleHeight = Math.Max(TaskbarModeToggle?.MinHeight ?? 0, FontSize * 0.6) * 1.2;
                }
                var toggleMargin = TaskbarModeToggle?.Margin ?? new Thickness(0);
                var layoutMargin = LayoutRoot?.Margin ?? new Thickness(0);
                var chromeHeight = SystemParameters.WindowCaptionHeight
                                   + SystemParameters.WindowResizeBorderThickness.Top
                                   + SystemParameters.WindowResizeBorderThickness.Bottom;
                var targetHeight = toggleHeight
                    + toggleMargin.Top + toggleMargin.Bottom
                    + layoutMargin.Top + layoutMargin.Bottom
                    + chromeHeight;
                targetHeight *= 1.2;
                if (targetHeight < 60) targetHeight = 60;

                MinHeight = targetHeight;
                MaxHeight = targetHeight;
                Height = targetHeight;

                if (ToolbarRow != null)
                {
                    ToolbarRow.Height = new GridLength(0);
                }
                if (ToolbarPanel != null)
                {
                    ToolbarPanel.Visibility = Visibility.Collapsed;
                }

                _isTaskbarOnlyMode = true;
            }
            else
            {
                if (ContentRow != null)
                {
                    ContentRow.Height = new GridLength(1, GridUnitType.Star);
                }
                if (Web != null)
                {
                    Web.Visibility = Visibility.Visible;
                }

                MinHeight = _previousMinHeight > 0 ? _previousMinHeight : 280;
                MaxHeight = double.IsPositiveInfinity(_previousMaxHeight) ? double.PositiveInfinity : _previousMaxHeight;
                if (!double.IsNaN(_previousWindowHeight) && _previousWindowHeight > 0)
                {
                    Height = Math.Max(_previousWindowHeight, MinHeight);
                }
                else
                {
                    Height = Math.Max(Height, MinHeight);
                }

                if (ToolbarRow != null)
                {
                    ToolbarRow.Height = GridLength.Auto;
                }
                if (ToolbarPanel != null)
                {
                    ToolbarPanel.Visibility = Visibility.Visible;
                }

                ShowSettingsButton.IsEnabled = true;
                SetSettingsVisibility(_settingsVisibleBeforeCompact);

                _isTaskbarOnlyMode = false;
            }
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var url = NormalizeUrl(UrlBox.Text);
                if (string.IsNullOrWhiteSpace(url))
                {
                    MessageBox.Show("URL が不正です。");
                    return;
                }

                NavigateToUrl(url);
            }
            catch (Exception ex)
            {
                MessageBox.Show("URL が不正です: " + ex.Message);
            }
        }

        private void NavigateToUrl(string? urlText)
        {
            if (string.IsNullOrWhiteSpace(urlText))
            {
                return;
            }

            try
            {
                if (Web.CoreWebView2 != null)
                {
                    var request = Web.CoreWebView2.Environment.CreateWebResourceRequest(
                        urlText,
                        "GET",
                        null,
                        "Cache-Control: no-cache\r\nPragma: no-cache\r\nExpires: 0\r\n");
                    Web.CoreWebView2.NavigateWithWebResourceRequest(request);
                }
                else
                {
                    Web.Source = new Uri(urlText);
                }
            }
            catch
            {
                // ignore navigation errors
            }
        }

        private void TopmostToggle_Checked(object sender, RoutedEventArgs e)
        {
            Topmost = TopmostToggle.IsChecked == true;
        }

        private void AutoRefreshToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (AutoRefreshToggle.IsChecked == true)
            {
                StartAutoRefresh();
            }
            else
            {
                StopAutoRefresh();
            }
        }

        private void StartAutoRefresh()
        {
            ApplyAutoRefreshIntervalText();

            if (_autoRefreshTimer == null)
            {
                _autoRefreshTimer = new DispatcherTimer();
                _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
            }

            RestartAutoRefreshTimer();

            _autoRefreshCts?.Cancel();
            _autoRefreshCts = new CancellationTokenSource();
            _ = TriggerAutoRefreshAsync(_autoRefreshCts.Token);
        }

        private void StopAutoRefresh()
        {
            if (_autoRefreshTimer != null)
            {
                _autoRefreshTimer.Stop();
            }
            _autoRefreshCts?.Cancel();
            _autoRefreshCts = null;
        }

        private async void AutoRefreshTimer_Tick(object? sender, EventArgs e)
        {
            if (_autoRefreshCts == null)
            {
                _autoRefreshCts = new CancellationTokenSource();
            }
            await TriggerAutoRefreshAsync(_autoRefreshCts.Token);
        }

        private void RestartAutoRefreshTimer()
        {
            if (_autoRefreshTimer == null)
            {
                return;
            }

            var interval = TimeSpan.FromSeconds(GetIntervalSeconds());
            _autoRefreshTimer.Stop();
            _autoRefreshTimer.Interval = interval;
            if (AutoRefreshToggle.IsChecked == true)
            {
                _autoRefreshTimer.Start();
            }
        }

        private async Task TriggerAutoRefreshAsync(CancellationToken token)
        {
            if (_autoRefreshInFlight)
            {
                return;
            }

            if (_isTypingInWebView)
            {
                return;
            }

            var url = NormalizeUrl(UrlBox.Text);
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            _autoRefreshInFlight = true;
            try
            {
                token.ThrowIfCancellationRequested();
                await Dispatcher.InvokeAsync(() => ReloadCurrentPage(url));
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
            catch
            {
                // ignore auto-refresh errors
            }
            finally
            {
                _autoRefreshInFlight = false;
            }
        }

        private void ReloadCurrentPage(string url)
        {
            if (Web.CoreWebView2 != null)
            {
                var current = Web.CoreWebView2.Source;
                if (!string.IsNullOrEmpty(current) && string.Equals(current, url, StringComparison.OrdinalIgnoreCase))
                {
                    Web.CoreWebView2.Reload();
                }
                else
                {
                    NavigateToUrl(url);
                }
            }
            else
            {
                Web.Source = new Uri(url);
            }
        }

        private void AutoRefreshIntervalBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            foreach (var ch in e.Text)
            {
                if (!char.IsDigit(ch))
                {
                    e.Handled = true;
                    return;
                }
            }
        }

        private void AutoRefreshIntervalBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyAutoRefreshIntervalText();
            RestartAutoRefreshTimer();
        }

        private void AutoRefreshIntervalBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (AutoRefreshToggle?.IsChecked == true &&
                AutoRefreshIntervalBox.IsFocused &&
                int.TryParse(AutoRefreshIntervalBox.Text, out var seconds) &&
                seconds >= MinimumAutoRefreshIntervalSeconds)
            {
                RestartAutoRefreshTimer();
            }
        }

        private void BackgroundColorBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ApplyBackgroundColorFromInput();
                e.Handled = true;
            }
        }

        private void BackgroundColorBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyBackgroundColorFromInput();
        }

        private void ApplyBackgroundColorFromInput()
        {
            var text = NormalizeHexColorText(BackgroundColorBox?.Text);
            ApplyBackgroundColor(text);
        }

        private void ApplyBackgroundColor(string hex)
        {
            if (!TryParseHexColor(hex, out var color))
            {
                UpdateBackgroundColorText(_currentBackgroundColor);
                return;
            }

            _currentBackgroundColor = color;

            var brush = new SolidColorBrush(color);
            LayoutRoot.Background = brush;
            Background = brush;

            if (ToolbarPanel != null)
            {
                var toolbarColor = Color.FromArgb(210, color.R, color.G, color.B);
                ToolbarPanel.Background = new SolidColorBrush(toolbarColor);
            }

            UpdateBackgroundColorText(color);
        }

        private void UpdateBackgroundColorText(Color color)
        {
            if (BackgroundColorBox == null || _suppressBackgroundTextUpdate) return;

            _suppressBackgroundTextUpdate = true;
            BackgroundColorBox.Text = $"{color.R:X2}{color.G:X2}{color.B:X2}";
            _suppressBackgroundTextUpdate = false;
        }

        private static bool TryParseHexColor(string text, out Color color)
        {
            color = Colors.White;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var span = text.Trim();
            if (span.StartsWith("#", StringComparison.Ordinal))
            {
                span = span[1..];
            }

            if (span.Length != 6 || !int.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            byte r = (byte)((value >> 16) & 0xFF);
            byte g = (byte)((value >> 8) & 0xFF);
            byte b = (byte)(value & 0xFF);
            color = Color.FromRgb(r, g, b);
            return true;
        }

        private static string NormalizeHexColorText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return DefaultBackgroundHex;
            }

            var span = text.Trim();
            if (span.StartsWith("#", StringComparison.Ordinal))
            {
                span = span[1..];
            }

            if (span.Length != 6 || !int.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            {
                return DefaultBackgroundHex;
            }

            return span.ToUpperInvariant();
        }

        private void ApplyAutoRefreshIntervalText()
        {
            var seconds = GetIntervalSeconds();
            AutoRefreshIntervalBox.Text = seconds.ToString(CultureInfo.InvariantCulture);
        }

        private int GetIntervalSeconds()
        {
            if (int.TryParse(AutoRefreshIntervalBox.Text, out var seconds) && seconds >= MinimumAutoRefreshIntervalSeconds)
            {
                return seconds;
            }

            return DefaultAutoRefreshIntervalSeconds;
        }

        private void SaveCurrentState()
        {
            try
            {
                var path = GetSettingsFilePath();
                var url = NormalizeUrl(UrlBox.Text);
                var width = Width.ToString(CultureInfo.InvariantCulture);
                var height = Height.ToString(CultureInfo.InvariantCulture);
                var autoEnabled = AutoRefreshToggle.IsChecked == true ? "1" : "0";
                var interval = GetIntervalSeconds().ToString(CultureInfo.InvariantCulture);
                var background = NormalizeHexColorText(BackgroundColorBox?.Text);

                File.WriteAllText(path, $"{url}\n{width}\n{height}\n{autoEnabled}\n{interval}\n{background}");
            }
            catch
            {
                // ignore
            }
        }

        private void LoadLastState()
        {
            try
            {
                var path = GetSettingsFilePath();
                string url = "http://localhost:8000";
                double width = 320;
                double height = 420;
                bool autoRefreshEnabled = false;
                int intervalSeconds = DefaultAutoRefreshIntervalSeconds;
                string backgroundText = DefaultBackgroundHex;

                if (File.Exists(path))
                {
                    var lines = File.ReadAllLines(path);
                    if (lines.Length > 0 && !string.IsNullOrWhiteSpace(lines[0]))
                    {
                        url = NormalizeUrl(lines[0]);
                    }
                    if (lines.Length > 1 && double.TryParse(lines[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var w))
                    {
                        width = w;
                    }
                    if (lines.Length > 2 && double.TryParse(lines[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var h))
                    {
                        height = h;
                    }
                    if (lines.Length > 3)
                    {
                        autoRefreshEnabled = lines[3].Trim() == "1";
                    }
                    if (lines.Length > 4 && int.TryParse(lines[4], out var savedInterval))
                    {
                        intervalSeconds = Math.Max(MinimumAutoRefreshIntervalSeconds, savedInterval);
                    }
                    if (lines.Length > 5 && !string.IsNullOrWhiteSpace(lines[5]))
                    {
                        backgroundText = NormalizeHexColorText(lines[5]);
                    }
                }

                UrlBox.Text = url;
                Width = width;
                Height = height;
                AutoRefreshIntervalBox.Text = intervalSeconds.ToString(CultureInfo.InvariantCulture);
                AutoRefreshToggle.IsChecked = autoRefreshEnabled;
                BackgroundColorBox.Text = backgroundText;
                ApplyBackgroundColor(backgroundText);
            }
            catch
            {
                UrlBox.Text = "http://localhost:8000";
                Width = 320;
                Height = 420;
                AutoRefreshIntervalBox.Text = DefaultAutoRefreshIntervalSeconds.ToString(CultureInfo.InvariantCulture);
                AutoRefreshToggle.IsChecked = false;
                BackgroundColorBox.Text = DefaultBackgroundHex;
                ApplyBackgroundColor(DefaultBackgroundHex);
            }
        }

        private string GetSettingsFilePath()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StickyMiniWeb");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.txt");
        }

        private static string NormalizeUrl(string? urlText)
        {
            var trimmed = (urlText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return string.Empty;
            }

            if (!trimmed.Contains("://", StringComparison.Ordinal))
            {
                trimmed = "http://" + trimmed;
            }

            return trimmed;
        }
    }

    internal static class ColorExtensions
    {
        public static double GetLuminance(this Color color)
        {
            return (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255d;
        }
    }

}
