using System.Diagnostics;

namespace ProxyApplication1;
public static class ProxyCloser
{
    public static async Task StopSingBoxAsync()
    {
        try
        {
            // Пытаемся найти все процессы sing-box
            foreach (var proc in Process.GetProcessesByName("sing-box"))
            {
                try
                {
                    if (proc.HasExited) continue;

                    // Мягко закрыть
                    proc.CloseMainWindow();
                    if (!proc.WaitForExit(1000))
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(3000);
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Elevated процесс, придётся через taskkill
                    await TaskkillElevatedAsync("/IM sing-box.exe /T /F");
                }
                catch
                {
                    // игнор
                }
            }
        }
        catch
        {
            // если не нашли процессов — ок
        }
    }

    private static async Task TaskkillElevatedAsync(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "taskkill.exe",
            Arguments = args,
            UseShellExecute = true,
            Verb = "runas", // спросит UAC при необходимости
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true
        };
        try
        {
            using var p = Process.Start(psi);
            if (p != null) await p.WaitForExitAsync();
        }
        catch
        {
            // пользователь мог нажать "Нет" в UAC
        }
    }
}
