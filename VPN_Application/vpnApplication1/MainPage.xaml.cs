using Microsoft.Maui.Controls;
using System;

namespace vpnApplication1
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
            UrlLabel.Text = url;
            OverlayWebView.Source = url;
            BrowserOverlay.IsVisible = true;

#if ANDROID
            // Дадим фокус нативному WebView
            Dispatcher.Dispatch(() =>
            {
                if (OverlayWebView?.Handler?.PlatformView is Android.Webkit.WebView nativeWebView)
                {
                    nativeWebView.RequestFocus();
                }
            });
#endif
        }

        // Новый «единый» метод закрытия
        public void CloseBrowserFromAnySource()
        {
            BrowserOverlay.IsVisible = false;
        }

        // Обёртка для совместимости со старым кодом
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
    }
}
