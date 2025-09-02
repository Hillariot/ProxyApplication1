using System;
using System.IO;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using ProxyApplication1.Services;


#if ANDROID
using Android.App;
using Android.OS;
using Android.Content;
#endif

namespace ProxyApplication1;


public partial class Home : ComponentBase
{
    // --- DI ---
    [Inject] protected IAuthService Auth { get; set; } = default!;

    // --- Auth UI state ---
    protected string Email = "";
    protected string Password = "";
    protected string RepeatPassword = "";
    protected bool IsRegistering = false;

    protected string? ErrorMessage;   // блок под формой/панелью
    protected string? ErrorToast;     // всплывающий тост
    protected CancellationTokenSource? _toastCts;

    protected bool IsLoggedIn => Auth.IsLoggedIn;

    // ===== Proxy state (UI) =====
    protected bool IsRunning { get; set; }
    protected bool IsBusy { get; set; }
    protected string? LastError { get; set; }

    [Microsoft.AspNetCore.Components.Inject]
    public ProxyApplication1.Services.INetworkSpeedService? SpeedService { get; set; }

    protected string SpeedText { get; set; } = "—";
    private CancellationTokenSource? _speedCts;

    // Запускаем/останавливаем опрос при изменении состояния
    private void StartSpeedLoop()
    {
        StopSpeedLoop();

        _speedCts = new CancellationTokenSource();
        var token = _speedCts.Token;

        _ = Task.Run(async () =>
        {
            string? last = null;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var svc = SpeedService;
                    if (svc is null) break;

                    var text = svc.GetNetworkSpeed();

                    if (!string.Equals(text, last, StringComparison.Ordinal))
                    {
                        last = text;
                        await MainThread.InvokeOnMainThreadAsync(() =>
                        {
                            SpeedText = text ?? "—";
                            StateHasChanged();
                        });
                    }
                }
                catch (TaskCanceledException) { break; }
                catch
                {
                    // Можно вывести "Ошибка чтения скорости" один раз
                }

                try { await Task.Delay(3000, token); }
                catch (TaskCanceledException) { break; }
            }
        }, token);
    }


    private void StopSpeedLoop()
    {
        try { _speedCts?.Cancel(); } catch { /* ignore */ }
        _speedCts?.Dispose();
        _speedCts = null;
    }

    // Вызови это место всякий раз, когда меняется IsLoggedIn/IsRunning.
    // Например, после успешного ConnectFromUi/DisconnectFromUi и в OnAfterRenderAsync.

    private void ToggleSpeedLoopByState()
    {
        if (IsLoggedIn && IsRunning)
        {
            StartSpeedLoop();
        }
        else
        {
            StopSpeedLoop();
            SpeedText = "—";
        }
    }

#if WINDOWS
    // ===== Windows helpers =====
    protected Process? _sbProcWin;
    protected CancellationTokenSource? _sbWinLogCts;
    static bool IsInterfacePresent(string name)
    {
        try
        {
            var psiL = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "interface show interface",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psiL);
            if (p == null) return false;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return output.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    static bool IsPortOpenLocal(int port)
    {
        try
        {
            // Попробуем подключиться — если слушает localhost, соединение установится/отклонится быстро.
            using var client = new System.Net.Sockets.TcpClient();
            var t = client.ConnectAsync("127.0.0.1", port);
            return t.Wait(200);
        }
        catch { return false; }
    }

    protected static void ValidateWindowsPrereqsOrThrow(string exePath)
    {
        var dir = Path.GetDirectoryName(exePath)!;
        var wintun = Path.Combine(dir, "wintun.dll");
        if (!File.Exists(wintun))
            throw new Exception($"Рядом с sing-box.exe нет wintun.dll (x64). Ожидался путь:\n{wintun}");
    }

    protected static string EnsureWinLogFile()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BarbarisProxy", "logs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "singbox-win.log");
    }

    protected static bool IsProcessElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        var p = new WindowsPrincipal(id);
        return p.IsInRole(WindowsBuiltInRole.Administrator);
    }

    protected static string GetWinSingBoxExePath()
    {
        var baseDir = AppContext.BaseDirectory!;
        var p1 = Path.Combine(baseDir, "sing-box.exe");
        var p2 = Path.Combine(baseDir, "Platforms", "Windows", "sing-box.exe");
        if (File.Exists(p1)) return p1;
        if (File.Exists(p2)) return p2;
        throw new FileNotFoundException("Не найден sing-box.exe", p1);
    }

    protected static string GetWinConfigPath()
    {
        var baseDir = AppContext.BaseDirectory!;
        var p1 = Path.Combine(baseDir, "config.json");
        var p2 = Path.Combine(baseDir, "Platforms", "Windows", "config.json");
        if (File.Exists(p1)) return p1;
        if (File.Exists(p2)) return p2;
        throw new FileNotFoundException("Не найден Platforms/Windows/config.json");
    }

    protected void StartWinLogPump(Process p, string tag = "SINGBOX")
    {
        _sbWinLogCts = new CancellationTokenSource();
        var ct = _sbWinLogCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested && !p.HasExited)
                {
                    var line = await p.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    Debug.WriteLine($"{tag}: {line}");
                }
            }
            catch { }
        }, ct);
    }
#endif

#if ANDROID
    // ===== Proxy helpers (Android) =====
    protected static Android.Content.ISharedPreferences GetProxyPrefs()
        => Android.App.Application.Context!.GetSharedPreferences(ProxyApplication1.MyProxyService.PREFS, FileCreationMode.Private)!;

    protected static bool IsProxyRunningFlag()
        => GetProxyPrefs().GetBoolean(ProxyApplication1.MyProxyService.KEY_RUNNING, false);

    protected static async Task<bool> WaitForRunningAsync(bool expected, int timeoutMs = 8000, int pollMs = 120)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                if (IsProxyRunningFlag() == expected) return true;
                await Task.Delay(pollMs, cts.Token);
            }
        }
        catch { }
        return IsProxyRunningFlag() == expected;
    }

    protected static void StartForegroundServiceCompat(Intent intent)
        => AndroidX.Core.Content.ContextCompat.StartForegroundService(Android.App.Application.Context!, intent);
#endif

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
#if ANDROID
        if (firstRender)
        {
            // Синхронизируем UI со значением в SharedPreferences
            IsRunning = IsProxyRunningFlag();
            StateHasChanged();
        }
#endif
        return base.OnAfterRenderAsync(firstRender);
    }

    // ===== UI helpers =====
    protected Task SafeUIAsync(Action action)
        => InvokeAsync(() => { action(); StateHasChanged(); });

    protected Task ShowError(string msg)
    {
        LastError = msg;
        return SafeUIAsync(() => { });
    }

    // ===== Proxy actions =====
    protected async Task ConnectFromUi()
    {
#if ANDROID
        if (IsBusy) return;
        IsBusy = true; LastError = null; await SafeUIAsync(() => { });

        try
        {
            // 1) Системное разрешение на Proxy
            var ok = await MainActivity.RequestProxyPermissionAsync().ConfigureAwait(false);
            if (!ok) { await ShowError("Доступ к Proxy отклонён пользователем."); return; }

            // 2) Уже запущено?
            if (IsProxyRunningFlag()) { IsRunning = true; await SafeUIAsync(() => { }); return; }

            // 3) Стартуем наш сервис с ACTION_START
            var startIntent = new Intent(Android.App.Application.Context!, typeof(ProxyApplication1.MyProxyService))
                .SetAction(ProxyApplication1.MyProxyService.ACTION_START);
            StartForegroundServiceCompat(startIntent);

            // 4) Ждём, пока сервис выставит KEY_RUNNING=true
            var up = await WaitForRunningAsync(expected: true, timeoutMs: 8000);
            IsRunning = up;
            if (!up) await ShowError("Proxy не удалось запустить (таймаут). Проверьте логи.");
        }
        catch (Exception ex)
        {
            await ShowError("Ошибка подключения: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
            await SafeUIAsync(() => { });
        }
        ToggleSpeedLoopByState();
#elif WINDOWS
        if (IsBusy) return;
        IsBusy = true; LastError = null; await SafeUIAsync(() => { });

        try
        {
            var exe = GetWinSingBoxExePath();                 // путь к sing-box.exe
            var cfg = GetWinConfigPath();                     // путь к config.json
            ValidateWindowsPrereqsOrThrow(exe);               // проверим wintun.dll рядом с exe

            var exeDir = Path.GetDirectoryName(exe)!;

            // Имя TUN-интерфейса из конфига (по умолчанию — BarbarisProxy)
            string tunName = "BarbarisProxy";
            try
            {
                using var fs = File.OpenRead(cfg);
                using var doc = System.Text.Json.JsonDocument.Parse(fs);
                if (doc.RootElement.TryGetProperty("inbounds", out var inb) && inb.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var it in inb.EnumerateArray())
                    {
                        if (it.TryGetProperty("type", out var t) && t.GetString() == "tun")
                        {
                            if (it.TryGetProperty("interface_name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String)
                                tunName = n.GetString() ?? tunName;
                            break;
                        }
                    }
                }
            }
            catch { /* игнор — оставим дефолтное имя */ }

            // Стартуем БЕЗ cmd.exe, окно не показываем, с UAC-повышением
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"run -c \"{cfg}\" --disable-color",
                UseShellExecute = true,                  // обязательно для Verb=runas
                Verb = "runas",                          // UAC prompt
                WorkingDirectory = exeDir,
                WindowStyle = ProcessWindowStyle.Hidden, // скрыть окно
                CreateNoWindow = true
            };

            var proc = Process.Start(psi) ?? throw new Exception("Не удалось стартовать (runas).");

            // ждём убедительный сигнал (до 10 сек)
            var sw = Stopwatch.StartNew();
            bool ok = false;
            while (sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(300);

                if (proc.HasExited)
                    break; // процесс реально упал

                // 1. поднялся TUN?
                if (IsInterfacePresent(tunName)) { ok = true; break; }

                // 2. открылись порты?
                if (IsPortOpenLocal(8080) || IsPortOpenLocal(1080)) { ok = true; break; }

                // 3. живёт больше 2 секунд → считаем успешным стартом
                if (sw.Elapsed > TimeSpan.FromSeconds(2)) { ok = true; break; }
            }

            if (!ok)
            {
                if (!proc.HasExited) { try { proc.Kill(entireProcessTree: true); } catch { } }
                throw new Exception("sing-box (elevated) завершился сразу или не дал признаков запуска.");
            }

            IsRunning = true;
        }
        catch (System.ComponentModel.Win32Exception w32) when (w32.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            await ShowError("Запуск отменён в UAC.");
            IsRunning = false;
        }
        catch (Exception ex)
        {
            await ShowError("Ошибка подключения (Windows): " + ex.Message);
            IsRunning = false;
        }
        finally
        {
            IsBusy = false;
            await SafeUIAsync(() => { });
        }
        ToggleSpeedLoopByState();

#else
        await ShowError("Эта платформа не поддерживается.");
#endif
    }

    protected async Task DisconnectFromUi()
    {
#if ANDROID
        if (IsBusy) return;
        IsBusy = true; LastError = null; await SafeUIAsync(() => { });

        try
        {
            // отправляем ACTION_STOP в наш сервис
            var stopIntent = new Intent(Android.App.Application.Context!, typeof(ProxyApplication1.MyProxyService))
                .SetAction(ProxyApplication1.MyProxyService.ACTION_STOP);
            Android.App.Application.Context!.StartService(stopIntent);

            // ждём KEY_RUNNING=false
            var down = await WaitForRunningAsync(expected: false, timeoutMs: 6000);
            IsRunning = !down;
            if (!down) await ShowError("Proxy не удалось корректно остановить (таймаут).");
        }
        catch (Exception ex)
        {
            await ShowError("Ошибка отключения: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
            await SafeUIAsync(() => { });
        }
        ToggleSpeedLoopByState();
#elif WINDOWS
        if (IsBusy) return;
        IsBusy = true; LastError = null; await SafeUIAsync(() => { });

        try
        {
            var kill = new ProcessStartInfo
            {
                FileName = "taskkill.exe",                 // напрямую taskkill
                Arguments = "/IM sing-box.exe /T /F",
                UseShellExecute = true,
                Verb = "runas",                            // запросит UAC при необходимости
                WindowStyle = ProcessWindowStyle.Hidden,   // скрыть окно
                CreateNoWindow = true
            };
            Process.Start(kill);

            IsRunning = false;
        }
        catch (System.ComponentModel.Win32Exception w32) when (w32.NativeErrorCode == 1223)
        {
            await ShowError("Отключение отменено в UAC.");
        }
        catch (Exception ex)
        {
            await ShowError("Ошибка отключения (Windows): " + ex.Message);
        }
        finally
        {
            IsBusy = false;
            await SafeUIAsync(() => { });
        }
        ToggleSpeedLoopByState();

#else
        await ShowError("Эта платформа не поддерживается.");
#endif
    }

    // ===== Авторизация =====
    protected async Task HandleLoginOrRegister()
    {
        ClearToast();
        ErrorMessage = null;
        StateHasChanged();

        try
        {
            if (IsRegistering)
            {
                if (!string.Equals(Password, RepeatPassword, StringComparison.Ordinal))
                {
                    await ShowToast("Пароли не совпадают");
                    return;
                }

                await Auth.RegisterAsync(Email, Password).ConfigureAwait(false);
            }
            else
            {
                await Auth.LoginAsync(Email, Password).ConfigureAwait(false);
            }

            // очистим форму
            Password = RepeatPassword = "";
            await InvokeAsync(StateHasChanged);
        }
        catch (System.OperationCanceledException) { /* отмена */ }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            await ShowToast(ex.Message);
            await InvokeAsync(StateHasChanged);
        }
    }

    protected async Task Logout()
    {
        // 1) выключим Proxy (не блокируй выход, если тут ошибки)
        try { await DisconnectFromUi(); } catch { }

        ClearToast();
        ErrorMessage = null;
        await InvokeAsync(StateHasChanged);

        try
        {
            await Auth.LogoutAsync();       // чистит токены + MarkLoggedOut()
        }
        catch (Exception ex)
        {
            ErrorMessage = "Ошибка выхода: " + ex.Message;
            await ShowToast(ErrorMessage);
            await InvokeAsync(StateHasChanged);
            return;
        }

        // 2) локальный UI-стейт
        Email = Password = RepeatPassword = "";
        IsRegistering = false;
        await InvokeAsync(StateHasChanged);

#if ANDROID
        // безопасно: пишем prefs в фоне, чтоб ничего не зависало
        try
        {
            var ctx = Android.App.Application.Context;
            await Task.Run(() =>
            {
                var sp = ctx.GetSharedPreferences(ProxyApplication1.MyProxyService.PREFS,
                                                  Android.Content.FileCreationMode.Private)!;
                var ed = sp.Edit();
                ed.PutBoolean("logged_in", false);
                ed.PutBoolean(ProxyApplication1.MyProxyService.KEY_RUNNING, false);
                ed.Commit();  // синхронно, но мы уже не на UI-потоке
            }).ConfigureAwait(false);

            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
            {
                ProxyApplication1.MyProxyTileService.RequestRefresh(ctx);
            });
        }
        catch { /* не мешаем выходу */ }
#endif

        // 3) (опционально) перейти на экран логина/домашний
        // await Shell.Current.GoToAsync("//");
    }

    // ===== Ошибки (тост) =====
    protected async Task ShowToast(string message, int ms = 8000)
    {
        ClearToast();
        _toastCts = new CancellationTokenSource();
        var token = _toastCts.Token;

        ErrorToast = message;
        await InvokeAsync(StateHasChanged);

        try
        {
            await Task.Delay(ms, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;

            ErrorToast = null;
            await InvokeAsync(StateHasChanged);
        }
        catch (System.OperationCanceledException) { /* перетёрто новой ошибкой */ }
    }

    protected void ClearToast()
    {
        try { _toastCts?.Cancel(); } catch { }
        try { _toastCts?.Dispose(); } catch { }
        _toastCts = null;
        ErrorToast = null;
    }
}
