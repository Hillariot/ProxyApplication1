using System.Net.NetworkInformation;
namespace ProxyApplication1.Services;

public sealed class NetworkSpeedService : INetworkSpeedService
{
    private long _prevRx, _prevTx;
    private DateTime _prevTs = DateTime.UtcNow;

    public string GetNetworkSpeed()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _prevTs).TotalSeconds;
        if (elapsed <= 0) elapsed = 1;

        long rx = 0, tx = 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            // исключим Loopback/Туннели при желании:
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var stats = ni.GetIPv4Statistics();
            rx += stats.BytesReceived;
            tx += stats.BytesSent;
        }

        var drx = Math.Max(0, rx - _prevRx);
        var dtx = Math.Max(0, tx - _prevTx);

        _prevRx = rx; _prevTx = tx; _prevTs = now;

        return $"↑ {Format(dtx / elapsed)}  ↓ {Format(drx / elapsed)}";
    }

    private static string Format(double bytesPerSec)
    {
        const double KB = 1024, MB = 1024 * 1024, GB = 1024 * 1024 * 1024;
        if (bytesPerSec >= GB) return $"{bytesPerSec / GB:0.0} GB/s";
        if (bytesPerSec >= MB) return $"{bytesPerSec / MB:0.0} MB/s";
        if (bytesPerSec >= KB) return $"{bytesPerSec / KB:0.0} KB/s";
        return $"{bytesPerSec:0} B/s";
    }
}
