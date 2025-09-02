using System.Net.NetworkInformation;
namespace ProxyApplication1.Services
{
    public sealed class AndroidNetworkSpeedService : INetworkSpeedService
    {
        private readonly Dictionary<string, (ulong rx, ulong tx)> _last = new();
        private DateTime _lastUpdate = DateTime.MinValue;

        private static readonly string[] Keywords = { "wintun", "sing-box", "tun", "tap", "openvpn", "wireguard", "wg" };
        private readonly double _emaAlpha;
        private (double inBps, double outBps)? _ema;
        private string _lastText = "—"; // <- кэш последней строки
        private readonly object _lock = new();

        public AndroidNetworkSpeedService(double emaAlpha = 0.0)
        {
            _emaAlpha = System.Math.Clamp(emaAlpha, 0.0, 1.0);
        }


        public string GetNetworkSpeed()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;

                (ulong totalRx, ulong totalTx) = GetTotals();

                if (_lastUpdate == DateTime.MinValue)
                {
                    _last.Clear();
                    _last["__total__"] = (totalRx, totalTx);
                    _lastUpdate = now;
                    _lastText = _lastText is { Length: > 0 } ? _lastText : "—";
                    return _lastText; // раньше тут было "Подождите…"
                }

                var seconds = (now - _lastUpdate).TotalSeconds;

                // защита от прыжков времени и слишком частых вызовов
                if (seconds <= 0.0)
                    seconds = 1.0;
                else if (seconds < 0.5)
                    return _lastText; // вместо "Подождите…" отдаем предыдущее значение

                var (lastRx, lastTx) = _last.TryGetValue("__total__", out var v) ? v : (0UL, 0UL);
                var dRx = totalRx >= lastRx ? totalRx - lastRx : 0UL;
                var dTx = totalTx >= lastTx ? totalTx - lastTx : 0UL;

                var inBps = dRx / seconds;
                var outBps = dTx / seconds;

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

                _lastText = $"↑ {FormatBytesPerSec((int)outBps)}  ↓ {FormatBytesPerSec((int)inBps)}"; // единый вид
                return _lastText;
            }
        }

        private (ulong rx, ulong tx) GetTotals()
        {
            try
            {
                ulong rx = 0, tx = 0;

                // 1) читаем счётчики интерфейсов из sysfs
                var netDir = "/sys/class/net";
                if (System.IO.Directory.Exists(netDir))
                {
                    foreach (var path in System.IO.Directory.EnumerateDirectories(netDir))
                    {
                        var ifname = System.IO.Path.GetFileName(path);
                        if (string.IsNullOrEmpty(ifname) || !ContainsAny(ifname))
                            continue;

                        var rxPath = System.IO.Path.Combine(path, "statistics", "rx_bytes");
                        var txPath = System.IO.Path.Combine(path, "statistics", "tx_bytes");

                        if (System.IO.File.Exists(rxPath) && System.IO.File.Exists(txPath))
                        {
                            if (ulong.TryParse(System.IO.File.ReadAllText(rxPath).Trim(), out var r)) rx += r;
                            if (ulong.TryParse(System.IO.File.ReadAllText(txPath).Trim(), out var t)) tx += t;
                        }
                    }
                }

                // Если подходящих интерфейсов не нашли — попробуем общий трафик процесса (UID)
                if (rx == 0 && tx == 0)
                {
                    var uid = Android.OS.Process.MyUid();
                    long urx = Android.Net.TrafficStats.GetUidRxBytes(uid);
                    long utx = Android.Net.TrafficStats.GetUidTxBytes(uid);
                    rx = (ulong)System.Math.Max(urx, 0);
                    tx = (ulong)System.Math.Max(utx, 0);
                }

                return (rx, tx);
            }
            catch
            {
                // Последний фолбэк — общий трафик по системе (не идеально, но не падаем)
                var trxb = global::Android.Net.TrafficStats.TotalRxBytes; // long
                var ttxb = global::Android.Net.TrafficStats.TotalTxBytes; // long
                return ((ulong)System.Math.Max(trxb, 0), (ulong)System.Math.Max(ttxb, 0));
            }
        }

        private static bool ContainsAny(string s)
            => !string.IsNullOrEmpty(s) && Keywords.Any(k => s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

        private static string FormatBytesPerSec(int bytesPerSec)
        {
            const int KB = 1024, MB = 1024 * 1024, GB = 1024 * 1024 * 1024;
            if (bytesPerSec >= GB) return $"{bytesPerSec / GB} ГБ/с";
            if (bytesPerSec >= MB) return $"{bytesPerSec / MB} МБ/с";
            if (bytesPerSec >= KB) return $"{bytesPerSec / KB} КБ/с";
            return $"{bytesPerSec} Б/с";
        }
    }
}