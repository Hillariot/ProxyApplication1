using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Reflection;

namespace vpnApplication1
{

    public class AuthService
    {
        private readonly HttpClient _httpClient = new();

        public async Task<bool> LoginAsync(string email, string password)
        {
            var content = JsonContent.Create(new { email, password });
            try
            {
                var response = await _httpClient.PostAsync("http://185.184.122.74:5000/auth_auth", content);
                var json = await response.Content.ReadAsStringAsync();
                return json.Contains("\"success\":true");
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> RegisterAsync(string email, string password, string repeatPassword)
        {
            if (password != repeatPassword)
                return false;

            if (!ValidateEmail(email) || !ValidatePassword(password))
                return false;

            var content = JsonContent.Create(new { email, password });
            try
            {
                var response = await _httpClient.PostAsync("http://185.184.122.74:5001/auth_reg", content);
                var json = await response.Content.ReadAsStringAsync();
                return json.Contains("\"success\":true");
            }
            catch
            {
                return false;
            }
        }

        private bool ValidateEmail(string email)
        {
            var allowedDomains = new[] { "@gmail.com", "@yahoo.com", "@mail.ru", "@yandex.ru", "@outlook.com" };
            return allowedDomains.Any(d => email.EndsWith(d, StringComparison.OrdinalIgnoreCase));
        }

        private bool ValidatePassword(string password)
        {
            if (password.Length < 8) return false;
            return Regex.IsMatch(password, "[A-Z]") &&
                   Regex.IsMatch(password, "[a-z]") &&
                   Regex.IsMatch(password, "[0-9]");
        }
    }
    public class NetworkSpeedService
    {
        private ulong _lastInBytes = 0, _lastOutBytes = 0;
        private DateTime _lastUpdate = DateTime.UtcNow;

        public string GetNetworkSpeed()
        {
            var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up);

            ulong totalIn = 0, totalOut = 0;
            foreach (var ni in interfaces)
            {
                if (OperatingSystem.IsWindows())
                {
                    var stats = ni.GetIPv4Statistics();
                    totalIn += (ulong)stats.BytesReceived;
                    totalOut += (ulong)stats.BytesSent;
                }
            }

            var now = DateTime.UtcNow;
            var seconds = (now - _lastUpdate).TotalSeconds;
            if (seconds < 0.5) return "Подождите...";

            var inSpeed = (totalIn - _lastInBytes) / seconds;
            var outSpeed = (totalOut - _lastOutBytes) / seconds;
            _lastInBytes = totalIn;
            _lastOutBytes = totalOut;
            _lastUpdate = now;

            static string Format(double bytesPerSec)
            {
                bytesPerSec /= 8.0;
                if (bytesPerSec >= 1024 * 1024)
                    return $"{bytesPerSec / (1024 * 1024):F2} МБ/с";
                if (bytesPerSec >= 1024)
                    return $"{bytesPerSec / 1024:F2} КБ/с";
                return $"{bytesPerSec:F2} Б/с";
            }

            return $"Входящий трафик: {Format(inSpeed)}\nИсходящий трафик: {Format(outSpeed)}";
        }
    }


    public class HelpService
    {
        public string GetHelpMessage()
        {
            return """
        Справка по приложению VPN-клиент

        Это приложение позволяет установить защищённое соединение с удалённым VPN-сервером.

        Основные функции:
        - Профиль — просмотр и настройка учетной записи
        - Подключиться — установить VPN-соединение
        - Скорость — показать текущую скорость
        - Справка — открыть эту справку
        - Выход — завершить приложение

        FAQ:
        1. Как подключиться? — Нажмите "Подключиться".
        2. Где скорость? — Нажмите "Скорость".
        3. Проблемы? — Пишите: hillariot2070@gmail.com
        """;
        }
    }


    public class VpnService
    {
        private readonly string tempPath = Path.GetTempPath();
        private string openVpnPath => Path.Combine(tempPath, "openvpn.exe");
        private string configPath => Path.Combine(tempPath, "OpenVPN_7.ovpn");
        private string[] dependencyFiles =>
        [
            "libcrypto-3-x64.dll",
            "libpkcs11-helper-1.dll",
            "libssl-3-x64.dll",
            "wintun.dll"
        ];

        public Task<bool> ConnectAsync()
        {
            try
            {
                if (!EnsureFilesExist())
                {
                    Console.WriteLine("Один или несколько необходимых файлов отсутствуют.");
                    return Task.FromResult(false);
                }

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = openVpnPath,
                    Arguments = $"--config \"{configPath}\" --log C:\\log.txt",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };

                if (OperatingSystem.IsWindows())
                {
                    Process.Start(processStartInfo);
                }

                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка запуска OpenVPN: {ex.Message}");
                return Task.FromResult(false);
            }
        }

        public Task DisconnectAsync()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = "/F /IM openvpn.exe /T",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка остановки OpenVPN: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        private bool EnsureFilesExist()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                string[] allFiles = ["openvpn.exe", "OpenVPN_7.ovpn", .. dependencyFiles];

                foreach (var fileName in allFiles)
                {
                    string fullPath = Path.Combine(tempPath, fileName);
                    if (!File.Exists(fullPath))
                    {

                        string resourceName = assembly.GetManifestResourceNames()
                              .FirstOrDefault(r => r.EndsWith(fileName))!;

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
            builder.Services.AddSingleton<HelpService>();
            builder.Services.AddSingleton<NetworkSpeedService>();


            return builder.Build();
        }
    }



}
