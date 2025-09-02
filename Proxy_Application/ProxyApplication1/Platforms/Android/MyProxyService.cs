using Android.App;
using Android.Content;
using Android.Media;
using Android.OS;
using AndroidX.Core.App;
using IntelliJ.Lang.Annotations;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
// алиасы для устранения конфликтов имён
using JProcess = Java.Lang.Process;

namespace ProxyApplication1;

[Service(
    Name = "Company.Hillariot.BarbarisVPN.MyProxyService",
    Exported = true,
    Permission = "android.permission.BIND_VPN_SERVICE",
    ForegroundServiceType = Android.Content.PM.ForegroundService.TypeDataSync
)]
public sealed class MyProxyService : Android.Net.VpnService
{
    private NotificationManager? _nm;
    private NotificationCompat.Builder? _notifBuilder;
    private CancellationTokenSource? _speedCts;
    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
    private int _tunFd = -1;

    private const string TAG = "VPN";
    private const int NotifId = 9999;
    private const string ChannelId = "vpn_foreground";

    // === SharedPreferences (для плитки и статуса) ===
    public const string PREFS = "vpn_prefs";
    public const string KEY_RUNNING = "running";

    public const string ACTION_START = "Hillariot.BarbarisVPN.ACTION_START";
    public const string ACTION_STOP = "Hillariot.BarbarisVPN.ACTION_STOP";

    private ParcelFileDescriptor? _tunPfd;
    private JProcess? _sbProc;
    private Thread? _t2sThread;

    private const string ASSET_CONFIG = "sing-box/config.json"; // Конфиг-шаблон в Assets
    private const int SOCKS_PORT = 10808;                       // Локальный SOCKS sing-box
    private const int HEV_MTU = 1380;                           // MTU для туннеля (и Hev)
                                                                // если пользуешься INetworkSpeedService из DI
    private ProxyApplication1.Services.INetworkSpeedService? _speedSvc
        => Microsoft.Maui.MauiApplication.Current.Services.GetService<ProxyApplication1.Services.INetworkSpeedService>();


    // ================= P/Invoke HevSocks5Tunnel =================
    private static class HevTun
    {
        private const string Dll = "hev-socks5-tunnel";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hev_socks5_tunnel_main_from_str(byte[] configStr, uint configLen, int tunFd);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void hev_socks5_tunnel_quit();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void hev_socks5_tunnel_stats(out UIntPtr txPackets, out UIntPtr txBytes, out UIntPtr rxPackets, out UIntPtr rxBytes);
    }

    // ================= Управление состоянием =================

    public void SetRunning(bool value)
    {
        var sp = GetSharedPreferences(PREFS, FileCreationMode.Private)!;
        using var e = sp.Edit();
        e.PutBoolean(KEY_RUNNING, value);
        e.Apply();
    }

    private bool IsAuthorized()
    {
        try
        {
            var auth = Microsoft.Maui.MauiApplication.Current.Services.GetService<IAuthService>();
            if (auth != null) return auth.IsLoggedIn;

            var sp = GetSharedPreferences(PREFS, FileCreationMode.Private)!;
            return sp.GetBoolean("logged_in", false);
        }
        catch { return false; }
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var action = intent?.Action ?? ACTION_START;

        if (action == ACTION_STOP)
        {
            StopVpn();
            StopForegroundCompat();
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        if (!IsAuthorized())
        {
            Android.Widget.Toast.MakeText(this, "Войдите в аккаунт, чтобы использовать VPN", Android.Widget.ToastLength.Short).Show();
            StopForegroundCompat();
            StopSelf();
            // обновим плитку – станет Unavailable
            MyProxyTileService.RequestRefresh(this);
            return StartCommandResult.NotSticky;
        }

        ShowForeground("Запуск VPN…");

        var prep = Prepare(this);
        if (prep != null)
        {
            Android.Util.Log.Warn(TAG, "VpnService.Prepare() != null — нет согласия пользователя");
            StopForegroundCompat();
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        _ = RunAsync();
        return StartCommandResult.NotSticky;
    }

    public override void OnRevoke()
    {
        StopVpn();
        base.OnRevoke();
    }

    // ================== Основной запуск ==================

    private async System.Threading.Tasks.Task RunAsync()
    {
        try
        {
            // 1) Поднимаем TUN через VpnService
            var builder = new Android.Net.VpnService.Builder(this)
                .SetSession("barbaris")
                .SetMtu(1380)
                .AddAddress("10.0.0.1", 24)
                .AddDnsServer("1.1.1.1")
                .AddRoute("0.0.0.0", 0);

            // важно: исключаем наш процесс из VPN, чтобы не ловить петлю
            try { builder.AddDisallowedApplication(PackageName); } catch { /* API/perm guard */ }

            _tunPfd = builder.Establish() ?? throw new Exception("Не удалось создать TUN");

            // важное: дублируем fd для нативки, оригинал остаётся у сервиса
            var dup = ParcelFileDescriptor.Dup(_tunPfd.FileDescriptor);
            _tunFd = dup.DetachFd(); // сохраняем в поле, чтобы потом закрыть
            try { dup.Close(); } catch { }


            // 2) Готовим конфиг sing-box (SOCKS inbound на 127.0.0.1:10808)
            string cfgPath = await WriteConfigAsync(this);

            // 3) Запускаем sing-box (как раньше)
            string sbPath = Path.Combine(ApplicationInfo.NativeLibraryDir, "libsingbox.so");
            _sbProc = new Java.Lang.ProcessBuilder()
                .Command(sbPath, "run", "-c", cfgPath, "--disable-color")
                .RedirectErrorStream(true)
                .Start();

            // поток для логов sing-box
            new Thread(() =>
            {
                try
                {
                    using var reader = new Java.IO.BufferedReader(new Java.IO.InputStreamReader(_sbProc.InputStream));
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                        Android.Util.Log.Info("SINGBOX", line);
                }
                catch (Exception ex) { Android.Util.Log.Warn("SINGBOX", "log reader err: " + ex); }
            })
            { IsBackground = true }.Start();

            Android.Util.Log.Info(TAG, $"sing-box запущен, cfg={cfgPath}");

            // 4) Запускаем HevSocks5Tunnel (блокирующая функция — в отдельном потоке)
            string hevYaml = $@"
socks5:
  address: 127.0.0.1
  port: {SOCKS_PORT}
  udp: udp
".Trim();

            var hevBytes = System.Text.Encoding.UTF8.GetBytes(hevYaml);
            _t2sThread = new Thread(() =>
            {
                try
                {
                    int rc = HevTun.hev_socks5_tunnel_main_from_str(hevBytes, (uint)hevBytes.Length, _tunFd);
                    Android.Util.Log.Info("T2S", $"hev_socks5_tunnel exited rc={rc}");
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Error("T2S", "hev run error: " + ex);
                }
            })
            { IsBackground = true };
            _t2sThread.Start();

            SetRunning(true);
            MyProxyTileService.ForceRefresh(this);
            ShowForeground("VPN подключён");
            StartSpeedUpdates();

        }
        catch (System.Exception ex)
        {
            Android.Util.Log.Error(TAG, "Ошибка запуска: " + ex);
            StopVpn();
            ShowForeground("Ошибка VPN: " + ex.Message);
            new Handler(Looper.MainLooper).PostDelayed(() => { StopForegroundCompat(); StopSelf(); }, 2000);
        }
    }

    private void StopVpn()
    {
        try { HevTun.hev_socks5_tunnel_quit(); } catch { }
        if (_t2sThread != null && !_t2sThread.Join(3000))
        {
            Android.Util.Log.Warn(TAG, "Hev didn't stop in time; forcing app kill");
            Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
            return;
        }
        if (_tunFd >= 0)
        {
            try
            {
                Android.Util.Log.Info(TAG, $"Closing tun fd manually: fd={_tunFd}");
                close(_tunFd);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn(TAG, $"Error closing tun fd: {ex}");
            }
            _tunFd = -1;
        }

        try { _t2sThread?.Join(5000); } catch { }
        bool stuck = _t2sThread?.IsAlive == true;
        _t2sThread = null;

        try { _sbProc?.Destroy(); } catch { }
        _sbProc = null;
        Android.Util.Log.Info(TAG, $"Stopping VPN: hevAlive={_t2sThread?.IsAlive == true} sbProc={_sbProc != null}");

        try
        {
            HevTun.hev_socks5_tunnel_stats(out var txp, out var txb, out var rxp, out var rxb);
            Android.Util.Log.Info(TAG, $"Hev stats before quit: tx={txp}/{txb} rx={rxp}/{rxb}");
        }
        catch { }
        // закрываем именно свой оригинальный PFD
        try { _tunPfd?.Close(); } catch { }
        _tunPfd = null;

        Android.Util.Log.Info(TAG, $"Stopping VPN: hevAlive={_t2sThread?.IsAlive == true} sbProc={_sbProc != null}");

        try
        {
            HevTun.hev_socks5_tunnel_stats(out var txp, out var txb, out var rxp, out var rxb);
            Android.Util.Log.Info(TAG, $"Hev stats before quit: tx={txp}/{txb} rx={rxp}/{rxb}");
        }
        catch { }

        // если Hev застрял и всё ещё держит dup(fd) — добиваем процесс
        if (stuck)
        {
            Android.Util.Log.Warn(TAG, "Hev stuck — killing process to release TUN fds");
            Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
        }

        SetRunning(false);
        MyProxyTileService.ForceRefresh(this);
        Android.Util.Log.Info(TAG, "VPN остановлен");
        StopSpeedUpdates();

        StopForegroundCompat();
        StopSelf();
    }




    // ================== Конфиг sing-box ==================

    /// <summary>
    /// Читает шаблон из Assets и гарантированно формирует inbounds = SOCKS(127.0.0.1:10808).
    /// Остальные секции (outbounds/dns/route) оставляем как в шаблоне, при необходимости добавляем route.
    /// </summary>
    private static async System.Threading.Tasks.Task<string> WriteConfigAsync(Context ctx)
    {
        string workDir = Path.Combine(ctx.FilesDir!.AbsolutePath, "sb");
        Directory.CreateDirectory(workDir);
        string cfgPath = Path.Combine(workDir, "sb.json");

        using var s = ctx.Assets.Open(ASSET_CONFIG);
        using var r = new StreamReader(s, System.Text.Encoding.UTF8, true);
        string json = await r.ReadToEndAsync();

        await File.WriteAllTextAsync(cfgPath, json, new UTF8Encoding(false));
        Android.Util.Log.Info("SINGBOX", $"sb.json copied from assets ({json.Length} bytes)");
        return cfgPath;
    }

    private static async System.Threading.Tasks.Task<JsonObject> ReadConfigTemplateAsync(Context ctx)
    {
        using var s = ctx.Assets.Open(ASSET_CONFIG);
        using var r = new StreamReader(s, System.Text.Encoding.UTF8, true);
        string json = await r.ReadToEndAsync();

        var docOptions = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        var node = JsonNode.Parse(json, nodeOptions: default, documentOptions: docOptions);
        return (node as JsonObject) ?? new JsonObject();
    }

    // ================== Foreground уведомление ==================

    private const string LoudChannelId = "barbaris_vpn_loud_v2"; // НОВЫЙ ID канала

    private void ShowForeground(string text)
    {
        _nm = (NotificationManager)GetSystemService(NotificationService)!;

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var ch = new NotificationChannel(LoudChannelId, "Barbaris VPN", NotificationImportance.High)
            {
                Description = "Состояние VPN-подключения"
            };

            var audioAttrs = new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Notification)
                .SetContentType(AudioContentType.Sonification)
                .Build();

            ch.EnableVibration(true);
            ch.EnableLights(true);
            ch.SetSound(RingtoneManager.GetDefaultUri(RingtoneType.Notification), audioAttrs);

            _nm.CreateNotificationChannel(ch);
        }

        var stopIntent = new Intent(this, typeof(MyProxyService)).SetAction(ACTION_STOP);
        var pStop = PendingIntent.GetService(this, 1, stopIntent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        _notifBuilder = new NotificationCompat.Builder(this, LoudChannelId)
            .SetContentTitle("BarbarisVPN")
            .SetContentText(text)
            .SetSmallIcon(Microsoft.Maui.Resource.Drawable.ic_vpn_small)
            .SetOngoing(true)
            .SetCategory(NotificationCompat.CategoryService)
            .SetPriority((int)NotificationPriority.High)
            // звук/вибра — только при первом показе; дальнейшие Notify() — тихо
            .SetOnlyAlertOnce(true)
            .SetDefaults((int)NotificationDefaults.All)
            .AddAction(0, "Отключить", pStop)
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(text));

        StartForeground(NotifId, _notifBuilder.Build());
    }

    private void StopForegroundCompat()
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(24))
                StopForeground(StopForegroundFlags.Remove);
            else
                StopForeground(true);
        }
        catch { }
    }
    private void UpdateNotification(string main, string? extra = null)
    {
        if (_nm is null || _notifBuilder is null) return;

        if (string.IsNullOrEmpty(extra))
        {
            _notifBuilder
                .SetContentText(main)
                .SetStyle(new NotificationCompat.BigTextStyle().BigText(main));
        }
        else
        {
            // многострочный вид: заголовок + скорость
            _notifBuilder
                .SetContentText(main)
                .SetStyle(new NotificationCompat.BigTextStyle().BigText($"{main}\n{extra}"));
        }

        _nm.Notify(NotifId, _notifBuilder.Build());
    }
    private void StartSpeedUpdates()
    {
        StopSpeedUpdates();

        _speedCts = new CancellationTokenSource();
        var token = _speedCts.Token;

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var speed = _speedSvc?.GetNetworkSpeed() ?? "—";
                    UpdateNotification("VPN подключён", speed);
                }
                catch
                {
                    UpdateNotification("VPN подключён", "Ошибка скорости");
                }

                try { await System.Threading.Tasks.Task.Delay(1000, token); } catch { }
            }
        }, token);
    }

    private void StopSpeedUpdates()
    {
        try { _speedCts?.Cancel(); }
        catch { }
        finally { _speedCts?.Dispose(); _speedCts = null; }
    }
}