using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using System.Threading.Tasks;

namespace vpnApplication1
{
    [Activity(
        Theme = "@style/Maui.SplashTheme",
        MainLauncher = true,
        ConfigurationChanges = ConfigChanges.ScreenSize
                             | ConfigChanges.Orientation
                             | ConfigChanges.UiMode
                             | ConfigChanges.ScreenLayout
                             | ConfigChanges.SmallestScreenSize
                             | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        public const int VpnConsentRequest = 10042;
        private static TaskCompletionSource<bool>? _vpnTcs;
        public const int ReqPostNotif = 20001;

        protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            {
                if (CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Android.Content.PM.Permission.Granted)
                {
                    RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications }, ReqPostNotif);
                }
            }
        }


        public static Task<bool> RequestVpnPermissionAsync()
        {
            var ctx = Android.App.Application.Context;
            var intent = global::Android.Net.VpnService.Prepare(ctx);
            if (intent == null) return Task.FromResult(true);

            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity == null) return Task.FromResult(false);

            _vpnTcs = new TaskCompletionSource<bool>();
            activity.StartActivityForResult(intent, VpnConsentRequest);
            return _vpnTcs.Task;
        }

        protected override void OnActivityResult(int requestCode, Android.App.Result resultCode, Android.Content.Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode == VpnConsentRequest)
            {
                _vpnTcs?.TrySetResult(resultCode == Android.App.Result.Ok);
                _vpnTcs = null;
            }
        }

        public override void OnBackPressed()
        {
            var page = MainPage.Instance;

            // Если открыт оверлей — закрываем его
            if (page != null && page.IsBrowserOpen)
            {
                page.CloseBrowserFromAnySource();
                return; // не отдаём назад в WebView / Blazor
            }

            // Иначе — стандартное поведение
            base.OnBackPressed();
        }

    }
}
