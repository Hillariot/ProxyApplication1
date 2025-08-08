using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace vpnApplication1
{
    public class AuthResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
    }


    public class AuthService
    {
        private readonly HttpClient _httpClient = new();

        public async Task<AuthResult> RegisterAsync(string email, string password, string repeatPassword)
        {
            if (password != repeatPassword)
                return new AuthResult { Success = false, ErrorMessage = "Passwords do not match" };

            if (!ValidateEmail(email) || !ValidatePassword(password))
                return new AuthResult { Success = false, ErrorMessage = "Invalid email or password format" };

            var content = JsonContent.Create(new { email, password });
            try
            {
                var response = await _httpClient.PostAsync("http://185.184.122.25:5001/auth_reg", content);
                var json = await response.Content.ReadAsStringAsync();

                if (json.Contains("\"success\":true"))
                    return new AuthResult { Success = false,ErrorMessage= "Пользователь уже зарегистрирован" };

                if (json.Contains("email_sent"))
                    return new AuthResult { Success = true };

                return new AuthResult { Success = false, ErrorMessage = "Возникла неизвестная ошибка, повторите регистрацию позже или напишите на почту hillariot2070@gmail.com" };
            }
            catch (Exception ex)
            {
                return new AuthResult { Success = false, ErrorMessage = $"Network error: {ex.Message}" };
            }
        }


        public async Task<AuthResult> LoginAsync(string email, string password)
        {
            var content = JsonContent.Create(new { email, password });
            try
            {
                var response = await _httpClient.PostAsync("http://185.184.122.25:5000/auth_auth", content);
                var json = await response.Content.ReadAsStringAsync();

                if (json.Contains("\"success\":true"))
                    return new AuthResult { Success = true };
                if (json.Contains("\"error\":\"Email not confirmed\""))
                    return new AuthResult { Success = false, ErrorMessage = "Please confirm your email first" };
                if (json.Contains("Invalid credentials"))
                    return new AuthResult { Success = false, ErrorMessage = "Invalid email or password" };

                return new AuthResult { Success = false, ErrorMessage = "Unknown login error" };
            }
            catch (Exception ex)
            {
                return new AuthResult { Success = false, ErrorMessage = $"Network error: {ex.Message}" };
            }
        }


        private bool ValidateEmail(string email)
        {
            var regex = new Regex(@"^[a-zA-Z0-9_.+-]+@[a-zA-Z0-9-]+\.[a-zA-Z0-9-.]+$");
            return regex.IsMatch(email);
        }

        private bool ValidatePassword(string password)
        {
            if (password.Length < 8) return false;
            return Regex.IsMatch(password, "[A-Z]") &&
                   Regex.IsMatch(password, "[a-z]") &&
                   Regex.IsMatch(password, "[0-9]");
        }
    }

    public sealed class NetworkSpeedService
    {
        private readonly Dictionary<string, (ulong rx, ulong tx)> _last = new();
        private DateTime _lastUpdate = DateTime.MinValue;

        // Ключевые слова для VPN-интерфейсов (sing-box/wintun, tun/tap, openvpn, wireguard)
        private static readonly string[] Keywords =
        {
        "wintun", "sing-box", "tun", "tap", "openvpn", "wireguard", "wg"
    };

        // Параметр сглаживания (0 — без сглаживания, 0.2…0.5 — умеренное)
        private readonly double _emaAlpha;

        // Последняя сглаженная скорость (для EMA)
        private (double inBps, double outBps)? _ema;

        public NetworkSpeedService(double emaAlpha = 0.0)
        {
            _emaAlpha = Math.Clamp(emaAlpha, 0.0, 1.0);
        }

        public string GetNetworkSpeed()
        {
            var now = DateTime.UtcNow;

            var ifaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(i =>
                    i.OperationalStatus == OperationalStatus.Up &&
                    (i.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                     ContainsAny(i.Name) ||
                     ContainsAny(i.Description)));

            ulong totalRx = 0, totalTx = 0;

            foreach (var ni in ifaces)
            {
                var stats = ni.GetIPv4Statistics(); // IPv4 достаточен для подсчёта байт на Win
                totalRx += (ulong)stats.BytesReceived;
                totalTx += (ulong)stats.BytesSent;
            }

            // Первый вызов — просто зафиксировали baseline
            if (_lastUpdate == DateTime.MinValue)
            {
                _last.Clear();
                _last["__total__"] = (totalRx, totalTx);
                _lastUpdate = now;
                return "Подождите…";
            }

            var seconds = (now - _lastUpdate).TotalSeconds;
            if (seconds < 0.5) return "Подождите…";

            var (lastRx, lastTx) = _last.TryGetValue("__total__", out var v) ? v : (0UL, 0UL);
            var dRx = totalRx >= lastRx ? totalRx - lastRx : 0UL;
            var dTx = totalTx >= lastTx ? totalTx - lastTx : 0UL;

            var inBps = dRx / seconds;   // Байты в секунду
            var outBps = dTx / seconds;  // Байты в секунду

            // EMA сглаживание по желанию
            if (_emaAlpha > 0 && _ema is { } prev)
            {
                inBps = prev.inBps + _emaAlpha * (inBps - prev.inBps);
                outBps = prev.outBps + _emaAlpha * (outBps - prev.outBps);
                _ema = (inBps, outBps);
            }
            else if (_emaAlpha > 0)
            {
                _ema = (inBps, outBps);
            }

            _last["__total__"] = (totalRx, totalTx);
            _lastUpdate = now;

            return $"Входящий (VPN): {FormatBytesPerSec(inBps)}\nИсходящий (VPN): {FormatBytesPerSec(outBps)}";
        }

        private static bool ContainsAny(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return Keywords.Any(k => s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string FormatBytesPerSec(double bytesPerSec)
        {
            // Никаких делений на 8 — это уже байты/с
            const double KB = 1024.0;
            const double MB = 1024.0 * 1024.0;
            const double GB = 1024.0 * 1024.0 * 1024.0;

            if (bytesPerSec >= GB) return $"{bytesPerSec / GB:F2} ГБ/с";
            if (bytesPerSec >= MB) return $"{bytesPerSec / MB:F2} МБ/с";
            if (bytesPerSec >= KB) return $"{bytesPerSec / KB:F2} КБ/с";
            return $"{bytesPerSec:F2} Б/с";
        }
    }

    public class VpnService
    {
        private readonly string tempPath = Path.GetTempPath();

        private string singBoxPath => Path.Combine(tempPath, "sing-box.exe");
        private string configPath => Path.Combine(tempPath, "config.json"); // твой sing-box конфиг

        private string[] dependencyFiles =>
        [];

        public Task<bool> ConnectAsync()
        {
            try
            {
                if (!EnsureFilesExist())
                {
                    Console.WriteLine("Один или несколько необходимых файлов отсутствуют.");
                    return Task.FromResult(false);
                }

                var psi = new ProcessStartInfo
                {
                    FileName = singBoxPath,
                    Arguments = $"run -c \"{configPath}\"",
                    UseShellExecute = true,      // нужно для Verb=runas
                    Verb = "runas",              // права администратора (для установки/использования TUN)
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };

                if (OperatingSystem.IsWindows())
                {
                    Process.Start(psi);
                }

                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка запуска sing-box: {ex.Message}");
                return Task.FromResult(false);
            }
        }

        public Task DisconnectAsync()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = "/F /IM sing-box.exe /T",
                        UseShellExecute = true,         // нужно для Verb=runas
                        Verb = "runas",                 // просим UAC
                        WindowStyle = ProcessWindowStyle.Hidden,
                        CreateNoWindow = true
                    };
                    Process.Start(psi);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка остановки sing-box: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        private bool EnsureFilesExist()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                // Вытаскиваем только то, что реально нужно: sing-box.exe, config.json и опциональные базы.
                string[] allFiles = ["sing-box.exe", "config.json", .. dependencyFiles];

                foreach (var fileName in allFiles)
                {
                    string fullPath = Path.Combine(tempPath, fileName);
                    if (!File.Exists(fullPath))
                    {
                        string resourceName = assembly.GetManifestResourceNames()
                            .FirstOrDefault(r => r.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

                        if (resourceName == null)
                        {
                            Console.WriteLine($"Ресурс не найден: {fileName}");
                            return false;
                        }

                        using var resourceStream = assembly.GetManifestResourceStream(resourceName);
                        if (resourceStream == null)
                        {
                            Console.WriteLine($"Не удалось открыть ресурсный поток для: {fileName}");
                            return false;
                        }

                        using var fileStream = File.Create(fullPath);
                        resourceStream.CopyTo(fileStream);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка извлечения файлов: {ex.Message}");
                return false;
            }
        }
    }



    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();
            builder.Services.AddSingleton<VpnService>();
            builder.Services.AddSingleton<AuthService>();
            builder.Services.AddSingleton<NetworkSpeedService>();
            builder.Services.AddSingleton<NetworkSpeedService>();


            return builder.Build();
        }
    }



}
