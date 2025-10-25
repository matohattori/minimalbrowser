using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
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

        private DispatcherTimer? _autoRefreshTimer;
        private CancellationTokenSource? _autoRefreshCts;
        private bool _autoRefreshInFlight;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                LoadLastUrlAndSize();

                await Web.EnsureCoreWebView2Async();
                Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                Web.CoreWebView2.DocumentTitleChanged += CoreWebView2_DocumentTitleChanged;

                NavigateToUrl(UrlBox.Text);
            }
            catch (Exception ex)
            {
                MessageBox.Show("WebView2 の初期化に失敗しました: " + ex.Message);
            }
        }

        private void CoreWebView2_DocumentTitleChanged(object? sender, object? e)
        {
            if (Web?.CoreWebView2?.DocumentTitle != null)
            {
                Title = Web.CoreWebView2.DocumentTitle;
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
            SaveLastUrlAndSize();
            StopAutoRefresh();
        }

        private string GetSettingsFilePath()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StickyMiniWeb");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.txt");
        }

        private void SaveLastUrlAndSize()
        {
            try
            {
                var path = GetSettingsFilePath();
                var url = NormalizeUrl(UrlBox.Text);
                var width = Width.ToString(CultureInfo.InvariantCulture);
                var height = Height.ToString(CultureInfo.InvariantCulture);
                var autoEnabled = AutoRefreshToggle.IsChecked == true ? "1" : "0";
                var interval = GetIntervalSeconds().ToString(CultureInfo.InvariantCulture);
                var backgroundColor = BackgroundColorBox.Text.Trim();
                var hideContent = HideContentToggle.IsChecked == true ? "1" : "0";

                File.WriteAllText(path, $"{url}\n{width}\n{height}\n{autoEnabled}\n{interval}\n{backgroundColor}\n{hideContent}");
            }
            catch
            {
                // ignore save errors
            }
        }

        private void LoadLastUrlAndSize()
        {
            try
            {
                var path = GetSettingsFilePath();
                string url = "http://localhost:8000";
                double width = 320;
                double height = 420;
                bool autoRefreshEnabled = false;
                int intervalSeconds = DefaultAutoRefreshIntervalSeconds;
                string backgroundColor = "#FFFFFF";
                bool hideContent = false;

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
                        backgroundColor = lines[5].Trim();
                    }
                    if (lines.Length > 6)
                    {
                        hideContent = lines[6].Trim() == "1";
                    }
                }

                UrlBox.Text = url;
                Width = width;
                Height = height;
                AutoRefreshIntervalBox.Text = intervalSeconds.ToString(CultureInfo.InvariantCulture);
                AutoRefreshToggle.IsChecked = autoRefreshEnabled;
                BackgroundColorBox.Text = backgroundColor;
                HideContentToggle.IsChecked = hideContent;
                
                ApplyBackgroundColor();
                if (hideContent)
                {
                    UpdateContentVisibility();
                }
            }
            catch
            {
                UrlBox.Text = "http://localhost:8000";
                Width = 320;
                Height = 420;
                AutoRefreshIntervalBox.Text = DefaultAutoRefreshIntervalSeconds.ToString(CultureInfo.InvariantCulture);
                AutoRefreshToggle.IsChecked = false;
                BackgroundColorBox.Text = "#FFFFFF";
                HideContentToggle.IsChecked = false;
            }
        }

        private void ShowSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            bool show = UrlBox.Visibility != Visibility.Visible;
            var visibility = show ? Visibility.Visible : Visibility.Collapsed;

            UrlBox.Visibility = visibility;
            OpenButton.Visibility = visibility;
            TopmostToggle.Visibility = visibility;
            AutoRefreshPanel.Visibility = visibility;
            BackgroundColorPanel.Visibility = visibility;
            ShowSettingsButton.Background = show ? System.Windows.Media.Brushes.DodgerBlue : System.Windows.Media.Brushes.Transparent;
        }

        private void HideContentToggle_Click(object sender, RoutedEventArgs e)
        {
            UpdateContentVisibility();
        }

        private void UpdateContentVisibility()
        {
            bool hide = HideContentToggle.IsChecked == true;
            ContentArea.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
            
            // Update icon to show chevron up when content is hidden, chevron down when visible
            try
            {
                var path = FindVisualChild<Path>(HideContentToggle, "HideContentIcon");
                if (path != null)
                {
                    path.Data = Geometry.Parse(hide ? "M7 14l5-5 5 5z" : "M7 10l5 5 5-5z");
                }
            }
            catch (FormatException)
            {
                // Invalid geometry data format, ignore
            }
            catch (InvalidOperationException)
            {
                // Unable to access visual tree, ignore
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent, string name) where T : DependencyObject
        {
            if (parent == null)
            {
                return null;
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                
                if (child is T typedChild && child is FrameworkElement fe && fe.Name == name)
                {
                    return typedChild;
                }

                var result = FindVisualChild<T>(child, name);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        private void BackgroundColorBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyBackgroundColor();
        }

        private void ApplyBackgroundColor()
        {
            try
            {
                var colorText = BackgroundColorBox.Text.Trim();
                if (string.IsNullOrEmpty(colorText))
                {
                    return;
                }

                // Ensure it starts with #
                if (!colorText.StartsWith("#"))
                {
                    colorText = "#" + colorText;
                }

                var colorObj = ColorConverter.ConvertFromString(colorText);
                if (colorObj == null)
                {
                    return;
                }

                var color = (Color)colorObj;
                ContentArea.Background = new SolidColorBrush(color);
            }
            catch
            {
                // Invalid color, ignore
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
}
