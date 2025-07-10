using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Text.Json;

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
    public class NetworkSpeedService
    {
        private ulong _lastInBytes = 0, _lastOutBytes = 0;
        private DateTime _lastUpdate = DateTime.UtcNow;

        public string GetNetworkSpeed()
        {
            var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
     .Where(i =>
         i.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
         (i.Name.Contains("tap", StringComparison.OrdinalIgnoreCase) ||
          i.Name.Contains("tun", StringComparison.OrdinalIgnoreCase) ||
          i.Description.Contains("tap", StringComparison.OrdinalIgnoreCase) ||
          i.Description.Contains("tun", StringComparison.OrdinalIgnoreCase) ||
          i.Description.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase)));

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

            return $"Входящий трафик (VPN): {Format(inSpeed)}\nИсходящий трафик (VPN): {Format(outSpeed)}";

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
            builder.Services.AddSingleton<NetworkSpeedService>();


            return builder.Build();
        }
    }



}
