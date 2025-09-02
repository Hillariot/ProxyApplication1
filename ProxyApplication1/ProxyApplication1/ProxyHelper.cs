using System.Diagnostics;
using System.Reflection;

#if ANDROID
using Android.App;
using Android.Content.Res;
using Android.Net;
using Android.OS;
using Java.Lang;
// Алиасы, чтобы избежать конфликтов имён
using JFile = Java.IO.File;               // Java-файл для ProcessBuilder.Directory
using SFile = System.IO.File;             // System.IO.File
using SDirectory = System.IO.Directory;   // System.IO.Directory
using SPath = System.IO.Path;             // System.IO.Path
#endif


namespace ProxyApplication1
{
    public class ProxyHelper
    {
#if ANDROID
        private global::Java.Lang.Process? _androidProc;
#endif

#if WINDOWS
    private static string GetSingBoxFileName() => "sing-box.exe";
    private readonly string basePath = System.IO.Path.GetTempPath();
    private string singBoxPath => System.IO.Path.Combine(basePath, GetSingBoxFileName());
    private string configPath => System.IO.Path.Combine(basePath, "config.json");
#else
        private static string GetSingBoxFileName() => "sing-box";
        private string basePath => Android.App.Application.Context.FilesDir!.AbsolutePath;
        private string singBoxPath => SPath.Combine(basePath, GetSingBoxFileName());
        private string configPath => SPath.Combine(basePath, "config.json");
#endif

        public async Task<bool> ConnectAsync()
        {
            try
            {
                var ok = await EnsureFilesAsync();
                if (!ok)
                {
                    Console.WriteLine("Один или несколько необходимых файлов отсутствуют.");
                    return false;
                }

#if WINDOWS
            var psi = new ProcessStartInfo
            {
                FileName = singBoxPath,
                Arguments = $"run -c \"{configPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };
            Process.Start(psi);
            return true;
#elif ANDROID
                var context = Android.App.Application.Context; // без двусмысленности

                // Сделать бинарник исполняемым
                Runtime.GetRuntime().Exec(new[] { "/system/bin/chmod", "755", singBoxPath })?.WaitFor();

                var cmd = $"{singBoxPath} run -c \"{configPath}\"";
                var pb = new ProcessBuilder("/system/bin/sh", "-c", cmd);
                pb.RedirectErrorStream(true);
                pb.Directory(new JFile(context.FilesDir!.AbsolutePath)); // ИМЕННО Java.IO.File
                _androidProc = pb.Start();

                _ = Task.Run(() =>
                {
                    try
                    {
                        using var reader = new System.IO.StreamReader(_androidProc!.InputStream);
                        string? line;
                        while ((line = reader.ReadLine()) != null)
                            Console.WriteLine("[sing-box] " + line);
                    }
                    catch { /* ignore */ }
                });

                return true;
#else
                Console.WriteLine("Эта платформа пока не поддерживается.");
                return false;
#endif
            }
            catch (System.Exception ex)
            {
                Console.WriteLine($"Ошибка запуска sing-box: {ex.Message}");
                return false;
            }
        }

        public Task DisconnectAsync()
        {
            try
            {
#if WINDOWS
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = "/F /IM sing-box.exe /T",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };
            Process.Start(psi);
#elif ANDROID
                try { _androidProc?.Destroy(); } catch { }
#endif
            }
            catch (System.Exception ex)
            {
                Console.WriteLine($"Ошибка остановки sing-box: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        private async Task<bool> EnsureFilesAsync()
        {
            try
            {
#if WINDOWS
            string[] required = ["sing-box.exe", "config.json"];
            var asm = Assembly.GetExecutingAssembly();

            foreach (var fileName in required)
            {
                var fullPath = System.IO.Path.Combine(basePath, fileName);
                if (System.IO.File.Exists(fullPath)) continue;

                var resourceName = asm.GetManifestResourceNames()
                    .FirstOrDefault(r => r.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
                if (resourceName == null) { Console.WriteLine($"Ресурс не найден: {fileName}"); return false; }

                using var res = asm.GetManifestResourceStream(resourceName)!;
                using var fs = System.IO.File.Create(fullPath);
                await res.CopyToAsync(fs);
            }
            return true;
#elif ANDROID
                var assets = Android.App.Application.Context.Assets;
                var filesDir = basePath;

                await CopySingBoxFromAssetsAsync(assets, filesDir);
                await CopyAssetIfMissingAsync(assets, "config.json", SPath.Combine(filesDir, "config.json"));

                return SFile.Exists(singBoxPath) && SFile.Exists(configPath);
#else
                return false;
#endif
            }
            catch (System.Exception ex)
            {
                Console.WriteLine($"Ошибка подготовки файлов: {ex.Message}");
                return false;
            }
        }

#if ANDROID
        private static async Task CopySingBoxFromAssetsAsync(AssetManager assets, string destDir)
        {
            var abi = (Build.SupportedAbis?.FirstOrDefault() ?? "arm64-v8a").ToLowerInvariant();
            var folder = abi.Contains("x86") ? (abi.Contains("64") ? "x86_64" : "x86") : (abi.Contains("64") ? "arm64-v8a" : "armeabi-v7a");
            var assetPath = $"sing-box/{folder}/sing-box";
            var dest = SPath.Combine(destDir, "sing-box");
            await CopyAssetIfMissingAsync(assets, assetPath, dest);
        }

        private static async Task CopyAssetIfMissingAsync(AssetManager assets, string assetPath, string destFullPath)
        {
            if (SFile.Exists(destFullPath)) return;
            SDirectory.CreateDirectory(SPath.GetDirectoryName(destFullPath)!);
            using var input = assets.Open(assetPath);
            using var output = SFile.Create(destFullPath);
            await input.CopyToAsync(output);
        }
#endif
    }

}

