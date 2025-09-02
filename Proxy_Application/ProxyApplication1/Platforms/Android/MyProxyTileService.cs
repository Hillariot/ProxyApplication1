using Android.App;
using Android.Content;
using Android.OS;
using Android.Service.QuickSettings;
using Android.Util;
using Java.Lang;

namespace ProxyApplication1;

[Service(
    Name = "Company.Hillariot.BarbarisVPN.MyProxyTileService",
    Permission = "android.permission.BIND_QUICK_SETTINGS_TILE",
    Exported = true,
    Icon = "@drawable/ic_vpn_small",
    Enabled = true
)]
[IntentFilter(new[] { "android.service.quicksettings.action.QS_TILE" })]
public sealed class MyProxyTileService : TileService
{
    private const string TAG = "VPN_TILE";

    // Защита от дабл-кликов по плитке
    private static bool _toggling;

    public override void OnTileAdded()
    {
        base.OnTileAdded();
        UpdateTileFromPrefs();
    }

    private bool IsAuthorized()
    {
        try
        {
            // 1) через DI, если есть IAuthService / ITokenStore
            var auth = Microsoft.Maui.MauiApplication.Current.Services.GetService<IAuthService>();
            if (auth != null) return auth.IsLoggedIn; // сделай такое свойство

            // 2) fallback: флаг в SharedPreferences, обновляй его при логине/выходе
            var sp = GetSharedPreferences(MyProxyService.PREFS, FileCreationMode.Private)!;
            return sp.GetBoolean("logged_in", false);
        }
        catch { return false; }
    }

    public override void OnStartListening()
    {
        base.OnStartListening();
        var tile = QsTile;
        if (tile == null) return;

        var running = GetSharedPreferences(MyProxyService.PREFS, FileCreationMode.Private)!
                      .GetBoolean(MyProxyService.KEY_RUNNING, false);

        if (!IsAuthorized())
        {
            tile.Label = "Войдите в аккаунт";
            tile.State = TileState.Unavailable; // серый, не нажимается
        }
        else
        {
            tile.Label = "Barbaris VPN";
            tile.State = running ? TileState.Active : TileState.Inactive;
        }
        tile.UpdateTile();
        UpdateTileFromPrefs();
    }

    public override void OnClick()
    {
        base.OnClick();

        if (!IsAuthorized())
        {
            // Откроем приложение на экране авторизации
            var intent = new Intent(this, typeof(MainActivity))
                .SetAction("ACTION_SHOW_LOGIN")
                .AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ClearTop);

            StartActivityAndCollapse(intent); // свернёт шторку QS и откроет активити
            return;
        }

        if (_toggling) return;
        _toggling = true;

        try
        {
            var sp = GetSharedPreferences(MyProxyService.PREFS, FileCreationMode.Private)!;
            bool running = sp.GetBoolean(MyProxyService.KEY_RUNNING, false);

            if (running)
            {
                // Выключаем VPN
                SendServiceCommand(MyProxyService.ACTION_STOP);
                ShowBusyState(false);
                return;
            }

            // Включаем VPN через прокси-активность согласия
            var intent = new Intent(this, typeof(ProxyConsentActivity))
                .AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu) // API 33+
            {
                var pi = PendingIntent.GetActivity(
                    this, 0, intent,
                    PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

                if (IsLocked)
                    UnlockAndRun(new Runnable(() => StartActivityAndCollapse(pi)));
                else
                    StartActivityAndCollapse(pi);
            }
            else
            {
                if (IsLocked)
                    UnlockAndRun(new Runnable(() => StartActivityAndCollapse(intent)));
                else
                    StartActivityAndCollapse(intent);
            }

            ShowBusyState(true);
        }
        finally
        {
            new Handler(Looper.MainLooper).PostDelayed(
                new Runnable(() => { _toggling = false; }),
                350
            );
        }
    }

    // === Точка входа для обновления плитки извне (из MyProxyService) ===
    public static void ForceRefresh(Context ctx)
    {
        try
        {
            var comp = new ComponentName(ctx, Java.Lang.Class.FromType(typeof(MyProxyTileService)).Name);
            RequestListeningState(ctx, comp);
        }
        catch (System.Exception ex)
        {
            Log.Warn(TAG, "ForceRefresh failed: " + ex);
        }
    }

    // === Внутреннее: включить/выключить VPN и обновить визуал ===
    private void ToggleVpn(bool running)
    {
        if (running)
        {
            // Отключаем VPN
            SendServiceCommand(MyProxyService.ACTION_STOP);
            // Визуально покажем «отключение…» до фактической синхронизации
            ShowBusyState(false);
            return;
        }

        // Включаем VPN: сначала согласие пользователя
        var prep = Android.Net.VpnService.Prepare(this);
        if (prep != null)
        {
            // Откроем системный диалог согласия и схлопнем шторку
            StartActivityAndCollapse(prep.AddFlags(ActivityFlags.NewTask));
            // Отрисуем «ожидание» (пока пользователь нажимает «ОК»)
            ShowBusyState(true);
            return;
        }

        // Согласие уже дано — запускаем сервис
        SendServiceCommand(MyProxyService.ACTION_START);
        ShowBusyState(true);
    }

    private void SendServiceCommand(string action)
    {
        var intent = new Intent(this, typeof(MyProxyService)).SetAction(action);
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                StartForegroundService(intent);
            else
                StartService(intent);
        }
        catch (System.Exception ex)
        {
            Log.Warn(TAG, "Start/Stop service failed: " + ex);
        }
    }

    // === Отрисовка состояний плитки ===

    private void UpdateTileFromPrefs()
    {
        var tile = QsTile;
        if (tile == null) return;

        bool running = GetSharedPreferences(MyProxyService.PREFS, FileCreationMode.Private)!
                       .GetBoolean(MyProxyService.KEY_RUNNING, false);

        tile.State = running ? TileState.Active : TileState.Inactive;
        tile.Label = "Barbaris VPN";

        // При желании задай иконку ресурса для плитки:
        tile.Icon = Android.Graphics.Drawables.Icon.CreateWithResource(this, Resource.Drawable.ic_vpn_small);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            tile.Subtitle = running ? "Подключено" : "Отключено";

        tile.UpdateTile();
    }

    private void ShowBusyState(bool enabling)
    {
        var tile = QsTile;
        if (tile == null) return;

        // Временно показываем «подключение… / отключение…»
        tile.State = enabling ? TileState.Active : TileState.Inactive;
        tile.Label = "Barbaris VPN";
        tile.Icon = Android.Graphics.Drawables.Icon.CreateWithResource(this, Resource.Drawable.ic_vpn_small);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            tile.Subtitle = enabling ? "Подключение…" : "Отключение…";
        tile.UpdateTile();
    }

    public static void RequestRefresh(Context ctx)
    {
        var cn = new ComponentName(ctx, Java.Lang.Class.FromType(typeof(MyProxyTileService)).Name);
        RequestListeningState(ctx, cn);
    }
}
