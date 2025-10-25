using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using System.Windows.Markup;

namespace StickyMiniWeb
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        // ...existing code...
    private void CoreWebView2_DocumentTitleChanged(object? sender, object? e)
        {
            if (Web?.CoreWebView2?.DocumentTitle != null)
            {
                this.Title = Web.CoreWebView2.DocumentTitle;
            }
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Restore last URL and window size if available
                LoadLastUrlAndSize();

                await Web.EnsureCoreWebView2Async();
                Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                // Webページタイトル変更時にウィンドウタイトルへ反映
                Web.CoreWebView2.DocumentTitleChanged += CoreWebView2_DocumentTitleChanged;
                try
                {
                    Web.Source = new Uri(UrlBox.Text);
                }
                catch
                {
                    // 不正なURLでもアプリは落とさない
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("WebView2 の初期化に失敗しました: " + ex.Message);
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

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveLastUrlAndSize();
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
                var url = UrlBox.Text ?? string.Empty;
                if (!url.Contains("://")) url = "http://" + url;
                var width = this.Width.ToString();
                var height = this.Height.ToString();
                File.WriteAllText(path, $"{url}\n{width}\n{height}");
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
                string? url = null;
                double width = 320, height = 420;
                if (File.Exists(path))
                {
                    var lines = File.ReadAllLines(path);
                    if (lines.Length > 0)
                    {
                        url = lines[0].Trim();
                    }
                    if (lines.Length > 1 && double.TryParse(lines[1], out var w))
                    {
                        width = w;
                    }
                    if (lines.Length > 2 && double.TryParse(lines[2], out var h))
                    {
                        height = h;
                    }
                }
                if (string.IsNullOrEmpty(url))
                {
                    url = "http://localhost:8000";
                }
                else if (!url.Contains("://"))
                {
                    url = "http://" + url;
                }
                UrlBox.Text = url;
                this.Width = width;
                this.Height = height;
            }
            catch
            {
                UrlBox.Text = "http://localhost:8000";
                this.Width = 320;
                this.Height = 420;
            }
        }
        private void ShowSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            bool show = UrlBox.Visibility != Visibility.Visible;
            UrlBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            OpenButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            TopmostToggle.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            ShowSettingsButton.Background = show ? System.Windows.Media.Brushes.DodgerBlue : System.Windows.Media.Brushes.Transparent;
        }


        private bool IsValidUrl(string url)
        {
            // 何かしら文字列があればOKとする
            return !string.IsNullOrWhiteSpace(url);
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var urlText = UrlBox.Text.Trim();
                if (!urlText.Contains("://"))
                    urlText = "http://" + urlText;
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
                    // 不正なURLでもアプリは落とさない
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("URL が不正です: " + ex.Message);
            }
        }

        private void TopmostToggle_Checked(object sender, RoutedEventArgs e)
        {
            this.Topmost = TopmostToggle.IsChecked == true;
        }
    }
}
