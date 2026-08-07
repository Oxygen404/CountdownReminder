using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ClashTrafficSentinel
{
    internal sealed class ConnectionSample
    {
        public string Id { get; set; }
        public string AppName { get; set; }
        public string AppPath { get; set; }
        public string Domain { get; set; }
        public long Upload { get; set; }
        public long Download { get; set; }
        public bool IsProxy { get; set; }
    }

    internal sealed class MihomoSnapshot
    {
        public MihomoSnapshot()
        {
            Connections = new List<ConnectionSample>();
        }

        public long UploadTotal { get; set; }
        public long DownloadTotal { get; set; }
        public List<ConnectionSample> Connections { get; private set; }
    }

    internal sealed class TrafficDelta
    {
        public DateTime LocalTime { get; set; }
        public string AppKey { get; set; }
        public string AppName { get; set; }
        public string AppPath { get; set; }
        public string Domain { get; set; }
        public long Upload { get; set; }
        public long Download { get; set; }

        public long Total { get { return Upload + Download; } }
    }

    internal sealed class TrafficRow
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public string Secondary { get; set; }
        public long Upload { get; set; }
        public long Download { get; set; }
        public long Total { get { return Upload + Download; } }
    }

    internal sealed class TrafficSummary
    {
        public TrafficSummary()
        {
            Rows = new List<TrafficRow>();
        }

        public long Upload { get; set; }
        public long Download { get; set; }
        public long Total { get { return Upload + Download; } }
        public List<TrafficRow> Rows { get; private set; }
    }

    internal sealed class MonitorState
    {
        public bool Connected { get; set; }
        public bool Paused { get; set; }
        public string StatusText { get; set; }
        public long UploadSpeed { get; set; }
        public long DownloadSpeed { get; set; }
        public int ActiveProxyConnections { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    internal sealed class AlertInfo
    {
        public string Title { get; set; }
        public string Message { get; set; }
        public string AppKey { get; set; }
    }

    internal sealed class MonitorSettings
    {
        public MonitorSettings()
        {
            BurstWindowMinutes = 3;
            BurstMegabytes = 200;
            DailyGigabytes = 1.0;
            AppCooldownMinutes = 30;
        }

        public int BurstWindowMinutes { get; set; }
        public int BurstMegabytes { get; set; }
        public double DailyGigabytes { get; set; }
        public int AppCooldownMinutes { get; set; }

        public void Clamp()
        {
            BurstWindowMinutes = Math.Max(1, Math.Min(30, BurstWindowMinutes));
            BurstMegabytes = Math.Max(50, Math.Min(10240, BurstMegabytes));
            DailyGigabytes = Math.Max(0.5, Math.Min(1000.0, DailyGigabytes));
            AppCooldownMinutes = Math.Max(5, Math.Min(1440, AppCooldownMinutes));
        }
    }

    internal static class TrafficIdentity
    {
        public static string NormalizeAppName(string process, string path)
        {
            if (!string.IsNullOrWhiteSpace(process)) return Clean(process, 260);
            if (!string.IsNullOrWhiteSpace(path))
            {
                try { return Clean(Path.GetFileName(path.Trim()), 260); }
                catch { }
            }
            return "未知应用";
        }

        public static string NormalizeAppPath(string path, string appName)
        {
            if (!string.IsNullOrWhiteSpace(path)) return Clean(path, 2048);
            return Clean(appName ?? "未知应用", 260);
        }

        public static string BuildAppKey(string path, string appName)
        {
            string value = !string.IsNullOrWhiteSpace(path) ? path : appName;
            return Clean(value ?? "未知应用", 2048).ToLowerInvariant();
        }

        public static string NormalizeDomain(string host, string destinationIp)
        {
            string value = !string.IsNullOrWhiteSpace(host) ? host : destinationIp;
            if (string.IsNullOrWhiteSpace(value)) return "未知域名";
            return Clean(value, 512).TrimEnd('.').ToLowerInvariant();
        }

        private static string Clean(string value, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string trimmed = value.Trim();
            StringBuilder builder = new StringBuilder(Math.Min(trimmed.Length, maximumLength));
            foreach (char character in trimmed)
            {
                if (builder.Length >= maximumLength) break;
                builder.Append(char.IsControl(character) ? ' ' : character);
            }
            return builder.ToString().Trim();
        }

        public static string FormatBytes(long value)
        {
            double number = Math.Max(0L, value);
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int index = 0;
            while (number >= 1024.0 && index < units.Length - 1)
            {
                number /= 1024.0;
                index++;
            }
            string format = number >= 100 || index == 0 ? "0" : number >= 10 ? "0.0" : "0.00";
            return number.ToString(format, CultureInfo.InvariantCulture) + " " + units[index];
        }

        public static string FormatSpeed(long value)
        {
            return FormatBytes(value) + "/s";
        }
    }
}
