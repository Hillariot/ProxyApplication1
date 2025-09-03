using Microsoft.Maui.Controls;
using System;
using System.Linq;               // FirstOrDefault
using System.Net;
using System.Threading.Tasks;    // Task

namespace ProxyApplication1
{
    public partial class MainPage : ContentPage
    {
        public static MainPage? Instance { get; private set; }

        public MainPage()
        {
            InitializeComponent();
            Instance = this;
        }

        public bool IsBrowserOpen => BrowserOverlay?.IsVisible == true;

        public void ShowBrowser(string url)
        {
            OverlayWebView.Source = url;
            BrowserOverlay.IsVisible = true;

            _ = UpdateAddressBarAsync(url);      // не блокируем UI

#if ANDROID
            // отдаём фокус нативному WebView, чтобы Back шёл в Activity
            Dispatcher.Dispatch(() =>
            {
                if (OverlayWebView?.Handler?.PlatformView is Android.Webkit.WebView native)
                    native.RequestFocus();
            });
#endif
        }

        private async Task UpdateAddressBarAsync(string url)
        {
            try
            {
                var uri = new Uri(url);
                var host = uri.Host;
                var https = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);

                SecureIcon.Text = https ? "🔒" : "🌐";
                HostLabel.Text = host;


                    var addrs = await Dns.GetHostAddressesAsync(host);
                    var ip = addrs.FirstOrDefault(a => a.AddressFamily ==
                                 System.Net.Sockets.AddressFamily.InterNetwork)
                             ?? addrs.FirstOrDefault();
            }
            catch
            {
                SecureIcon.Text = "🌐";
                HostLabel.Text = "неизвестно";
            }
        }

        // Единый метод закрытия
        public void CloseBrowserFromAnySource()
        {
            BrowserOverlay.IsVisible = false;
            OverlayWebView.Source = null;
        }

        // Обёртка для совместимости
        public void HideBrowser() => CloseBrowserFromAnySource();

        private void OnCloseBrowserClicked(object sender, EventArgs e) => CloseBrowserFromAnySource();

        protected override bool OnBackButtonPressed()
        {
            if (IsBrowserOpen)
            {
                CloseBrowserFromAnySource();
                return true;
            }
            return base.OnBackButtonPressed();
        }

        // Копирование IP в буфер
        private async void OnCopyIpClicked(object sender, EventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(HostLabel?.Text))
            {
                await Clipboard.SetTextAsync(HostLabel.Text);
#if ANDROID || IOS
                await DisplayAlert("", "Домен скопирован", "OK");
#endif
            }
        }
    }
}
