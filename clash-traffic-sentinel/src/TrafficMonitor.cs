using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ClashTrafficSentinel
{
    internal static class TrafficCalculator
    {
        public static List<TrafficDelta> Calculate(
            IDictionary<string, ConnectionSample> previous,
            MihomoSnapshot current,
            bool countNewConnections,
            DateTime localNow)
        {
            List<TrafficDelta> deltas = new List<TrafficDelta>();
            if (current == null) return deltas;
            foreach (ConnectionSample sample in current.Connections)
            {
                if (!sample.IsProxy) continue;
                ConnectionSample old;
                long upload;
                long download;
                if (previous != null && previous.TryGetValue(sample.Id, out old))
                {
                    upload = sample.Upload >= old.Upload ? sample.Upload - old.Upload : sample.Upload;
                    download = sample.Download >= old.Download ? sample.Download - old.Download : sample.Download;
                }
                else if (countNewConnections)
                {
                    upload = sample.Upload;
                    download = sample.Download;
                }
                else continue;

                if (upload <= 0 && download <= 0) continue;
                string appName = TrafficIdentity.NormalizeAppName(sample.AppName, sample.AppPath);
                string appPath = TrafficIdentity.NormalizeAppPath(sample.AppPath, appName);
                deltas.Add(new TrafficDelta
                {
                    LocalTime = localNow,
                    AppKey = TrafficIdentity.BuildAppKey(appPath, appName),
                    AppName = appName,
                    AppPath = appPath,
                    Domain = TrafficIdentity.NormalizeDomain(sample.Domain, null),
                    Upload = Math.Max(0, upload),
                    Download = Math.Max(0, download)
                });
            }
            return deltas;
        }

        public static Dictionary<string, ConnectionSample> BuildIndex(MihomoSnapshot snapshot)
        {
            Dictionary<string, ConnectionSample> result = new Dictionary<string, ConnectionSample>(StringComparer.Ordinal);
            if (snapshot == null) return result;
            foreach (ConnectionSample sample in snapshot.Connections)
            {
                if (!string.IsNullOrEmpty(sample.Id)) result[sample.Id] = sample;
            }
            return result;
        }
    }

    internal sealed class TrafficMonitor : IDisposable
    {
        private readonly NamedPipeMihomoClient client;
        private readonly TrafficDatabase database;
        private readonly Timer timer;
        private Dictionary<string, ConnectionSample> previous;
        private DateTime lastPollAt;
        private DateTime lastPruneDate;
        private bool initialized;
        private bool polling;
        private bool disposed;
        private bool paused;
        private MonitorSettings settings;

        public event EventHandler StateChanged;
        public event EventHandler<AlertEventArgs> AlertRaised;

        public TrafficMonitor(TrafficDatabase database)
        {
            if (database == null) throw new ArgumentNullException("database");
            this.database = database;
            client = new NamedPipeMihomoClient();
            previous = new Dictionary<string, ConnectionSample>(StringComparer.Ordinal);
            settings = database.LoadSettings();
            State = new MonitorState { Connected = false, StatusText = "正在连接 Clash", UpdatedAt = DateTime.Now };
            timer = new Timer();
            timer.Interval = 500;
            timer.Tick += delegate { PollAsync(); };
        }

        public MonitorState State { get; private set; }
        public MonitorSettings Settings { get { return settings; } }
        public bool Paused { get { return paused; } }

        public void Start()
        {
            database.PruneOperationalData(DateTime.Now);
            lastPruneDate = DateTime.Today;
            timer.Start();
            PollAsync();
        }

        public void TogglePause()
        {
            paused = !paused;
            State.Paused = paused;
            State.StatusText = paused ? "统计已暂停" : "正在重新连接";
            if (!paused)
            {
                initialized = false;
                previous.Clear();
                PollAsync();
            }
            RaiseStateChanged();
        }

        public void SaveSettings(MonitorSettings value)
        {
            if (value == null) return;
            value.Clamp();
            database.SaveSettings(value);
            settings = value;
            RaiseStateChanged();
        }

        private async void PollAsync()
        {
            if (disposed || polling || paused) return;
            polling = true;
            DateTime startedAt = DateTime.Now;
            try
            {
                MihomoSnapshot snapshot = await client.FetchAsync();
                if (disposed) return;
                DateTime localNow = DateTime.Now;
                double seconds = lastPollAt == DateTime.MinValue ? 1.0 : Math.Max(0.25, (localNow - lastPollAt).TotalSeconds);
                List<TrafficDelta> deltas = TrafficCalculator.Calculate(previous, snapshot, initialized, localNow);
                previous = TrafficCalculator.BuildIndex(snapshot);
                initialized = true;
                lastPollAt = localNow;

                long upload = 0;
                long download = 0;
                HashSet<string> touchedApps = new HashSet<string>(StringComparer.Ordinal);
                foreach (TrafficDelta delta in deltas)
                {
                    upload += delta.Upload;
                    download += delta.Download;
                    touchedApps.Add(delta.AppKey);
                }
                database.RecordBatch(deltas);

                int active = 0;
                foreach (ConnectionSample connection in snapshot.Connections)
                    if (connection.IsProxy) active++;

                State.Connected = true;
                State.Paused = false;
                State.StatusText = "正在监控代理流量";
                State.UploadSpeed = (long)Math.Round(upload / seconds);
                State.DownloadSpeed = (long)Math.Round(download / seconds);
                State.ActiveProxyConnections = active;
                State.UpdatedAt = localNow;

                CheckAlerts(touchedApps, localNow);
                if (lastPruneDate != localNow.Date)
                {
                    database.PruneOperationalData(localNow);
                    lastPruneDate = localNow.Date;
                }
                RaiseStateChanged();
            }
            catch (MihomoException ex)
            {
                HandleOffline(ex.Message);
            }
            catch
            {
                HandleOffline("本地统计暂时不可用");
            }
            finally
            {
                polling = false;
            }
        }

        private void HandleOffline(string message)
        {
            initialized = false;
            previous.Clear();
            lastPollAt = DateTime.MinValue;
            State.Connected = false;
            State.UploadSpeed = 0;
            State.DownloadSpeed = 0;
            State.ActiveProxyConnections = 0;
            State.StatusText = string.IsNullOrWhiteSpace(message) ? "正在等待 Clash Verge" : message;
            State.UpdatedAt = DateTime.Now;
            RaiseStateChanged();
        }

        private void CheckAlerts(IEnumerable<string> touchedApps, DateTime localNow)
        {
            long burstThreshold = (long)settings.BurstMegabytes * 1024L * 1024L;
            DateTimeOffset utcNow = DateTimeOffset.UtcNow;
            foreach (string appKey in touchedApps)
            {
                long recent = database.GetRecentAppBytes(appKey, localNow, settings.BurstWindowMinutes);
                if (recent < burstThreshold) continue;
                DateTimeOffset last = database.GetLastAppAlert(appKey);
                if (last != DateTimeOffset.MinValue && utcNow - last < TimeSpan.FromMinutes(settings.AppCooldownMinutes)) continue;
                database.SetLastAppAlert(appKey, utcNow);
                string appName = FindAppName(appKey);
                RaiseAlert(new AlertInfo
                {
                    Title = "检测到高流量应用",
                    Message = appName + " · " + settings.BurstWindowMinutes + " 分钟内 " + TrafficIdentity.FormatBytes(recent),
                    AppKey = appKey
                });
            }

            long dailyStep = (long)Math.Round(settings.DailyGigabytes * 1024.0 * 1024.0 * 1024.0);
            if (dailyStep <= 0) return;
            long today = database.GetTodayTotal(localNow);
            int milestone = (int)Math.Min(int.MaxValue, today / dailyStep);
            int notified = database.GetDailyMilestone(localNow);
            if (milestone > notified)
            {
                database.SetDailyMilestone(localNow, milestone);
                RaiseAlert(new AlertInfo
                {
                    Title = "今日代理流量提醒",
                    Message = "今天已使用 " + TrafficIdentity.FormatBytes(today) + " · 达到第 " + milestone + " 个阈值",
                    AppKey = null
                });
            }
        }

        private string FindAppName(string appKey)
        {
            foreach (ConnectionSample sample in previous.Values)
            {
                string key = TrafficIdentity.BuildAppKey(sample.AppPath, sample.AppName);
                if (string.Equals(key, appKey, StringComparison.Ordinal)) return sample.AppName;
            }
            try
            {
                string name = System.IO.Path.GetFileName(appKey);
                return string.IsNullOrWhiteSpace(name) ? "某个应用" : name;
            }
            catch { return "某个应用"; }
        }

        private void RaiseStateChanged()
        {
            EventHandler handler = StateChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaiseAlert(AlertInfo alert)
        {
            EventHandler<AlertEventArgs> handler = AlertRaised;
            if (handler != null) handler(this, new AlertEventArgs(alert));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            timer.Stop();
            timer.Dispose();
        }
    }

    internal sealed class AlertEventArgs : EventArgs
    {
        public AlertEventArgs(AlertInfo alert) { Alert = alert; }
        public AlertInfo Alert { get; private set; }
    }
}
