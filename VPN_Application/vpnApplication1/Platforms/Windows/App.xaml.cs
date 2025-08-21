using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using WinRT.Interop;
using Windows.Graphics;
using Microsoft.Maui.Controls;


namespace vpnApplication1.WinUI
{
    public partial class App : MauiWinUIApplication
    {
        public App()
        {
            this.InitializeComponent();
        }

        protected override MauiApp CreateMauiApp() => AuthTokens.CreateMauiApp();

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            base.OnLaunched(args);

#if WINDOWS
            var window = Application.Windows.FirstOrDefault()?.Handler.PlatformView as Microsoft.UI.Xaml.Window;
            if (window != null)
            {
                window.Closed += async (sender, e) =>
                {
                    await VpnCloser.StopSingBoxAsync();
                };
            }
#endif
        }
    }
}
