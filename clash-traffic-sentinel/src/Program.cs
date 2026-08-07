using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Clash 流量哨兵")]
[assembly: AssemblyDescription("Clash Verge 代理流量托盘监控")]
[assembly: AssemblyCompany("Clash Traffic Sentinel")]
[assembly: AssemblyProduct("Clash 流量哨兵")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace ClashTrafficSentinel
{
    internal static class Program
    {
        private const string MutexName = "Local\\ClashTrafficSentinel.67C8344D-72C9-4514-A5E3-EA47CC2B2359";

        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            using (Mutex mutex = new Mutex(true, MutexName, out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                bool preview = HasArgument(args, "--preview");
                bool show = preview || HasArgument(args, "--show");
                try
                {
                    Application.Run(new SentinelContext(show, preview));
                }
                catch
                {
                    MessageBox.Show("Clash 流量哨兵无法启动，请确认程序所在目录可写。",
                        "Clash 流量哨兵", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static bool HasArgument(string[] args, string expected)
        {
            return Array.Exists(args, delegate(string value)
            {
                return string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
            });
        }
    }

    internal sealed class SentinelContext : ApplicationContext
    {
        private readonly TrafficDatabase database;
        private readonly TrafficMonitor monitor;
        private readonly TrafficPopup popup;
        private readonly NotifyIcon trayIcon;
        private readonly ToolStripMenuItem showItem;
        private readonly ToolStripMenuItem pauseItem;
        private readonly ToolStripMenuItem startupItem;
        private readonly string dataPath;
        private readonly bool preview;
        private bool exiting;

        public SentinelContext(bool showAtStartup, bool previewMode)
        {
            preview = previewMode;
            dataPath = previewMode
                ? Path.Combine(Path.GetTempPath(), "clash-sentinel-preview-" + Guid.NewGuid().ToString("N") + ".db")
                : Path.Combine(AppContext.BaseDirectory, "traffic-data.db");
            database = new TrafficDatabase(dataPath);
            if (previewMode) SeedPreviewData();

            monitor = new TrafficMonitor(database);
            popup = new TrafficPopup(database, monitor);
            popup.OpenDataRequested += delegate { OpenDataFolder(); };
            popup.VisibleChanged += delegate
            {
                if (showItem != null) showItem.Text = popup.Visible ? "收起流量卡片" : "显示流量卡片";
            };

            showItem = new ToolStripMenuItem("显示流量卡片", null, delegate { TogglePopup(); });
            pauseItem = new ToolStripMenuItem("暂停统计", null, delegate { TogglePause(); });
            startupItem = new ToolStripMenuItem("开机自动启动", null, delegate { ToggleStartup(); });
            startupItem.Checked = !previewMode && StartupManager.IsEnabled();
            ToolStripMenuItem openDataItem = new ToolStripMenuItem("打开数据目录", null, delegate { OpenDataFolder(); });
            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出", null, delegate { ExitApplication(); });

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = new Font("Microsoft YaHei UI", 9f);
            menu.Items.Add(showItem);
            menu.Items.Add(pauseItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(startupItem);
            menu.Items.Add(openDataItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            trayIcon = new NotifyIcon();
            trayIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            trayIcon.Text = "Clash 流量哨兵 · 正在启动";
            trayIcon.ContextMenuStrip = menu;
            trayIcon.Visible = true;
            trayIcon.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) TogglePopup();
            };
            trayIcon.DoubleClick += delegate { ShowPopup(true); };

            monitor.StateChanged += HandleStateChanged;
            monitor.AlertRaised += HandleAlert;
            if (previewMode)
            {
                popup.SetPreviewState();
                ShowPopup(true);
            }
            else
            {
                monitor.Start();
                if (showAtStartup) ShowPopup(true);
            }
        }

        private void SeedPreviewData()
        {
            DateTime now = DateTime.Now;
            List<TrafficDelta> rows = new List<TrafficDelta>
            {
                Preview(now, "chrome", "Google Chrome", @"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe", "youtube.com", 8294400, 934281216),
                Preview(now, "chrome", "Google Chrome", @"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe", "github.com", 27262976, 157286400),
                Preview(now, "code", "Code.exe", @"C:\\Users\\demo\\AppData\\Local\\Programs\\Microsoft VS Code\\Code.exe", "api.github.com", 5242880, 288358400),
                Preview(now, "wechat", "WeChat.exe", @"C:\\Program Files\\Tencent\\WeChat\\WeChat.exe", "res.wx.qq.com", 18874368, 136314880),
                Preview(now, "onedrive", "OneDrive.exe", @"C:\\Program Files\\Microsoft OneDrive\\OneDrive.exe", "storage.live.com", 178257920, 73400320),
                Preview(now, "claude", "claude.exe", @"C:\\Users\\demo\\.local\\bin\\claude.exe", "api.anthropic.com", 12582912, 84934656)
            };
            database.RecordBatch(rows);
        }

        private static TrafficDelta Preview(DateTime time, string key, string name, string path, string domain, long upload, long download)
        {
            return new TrafficDelta
            {
                LocalTime = time,
                AppKey = key,
                AppName = name,
                AppPath = path,
                Domain = domain,
                Upload = upload,
                Download = download
            };
        }

        private void HandleStateChanged(object sender, EventArgs e)
        {
            if (exiting) return;
            MonitorState state = monitor.State;
            popup.UpdateMonitorState(state);
            string text = state.Connected
                ? "Clash 流量哨兵 · ↓" + TrafficIdentity.FormatSpeed(state.DownloadSpeed) + " · ↑" + TrafficIdentity.FormatSpeed(state.UploadSpeed)
                : "Clash 流量哨兵 · " + state.StatusText;
            trayIcon.Text = TrimTrayText(text);
            pauseItem.Text = monitor.Paused ? "继续统计" : "暂停统计";
        }

        private void HandleAlert(object sender, AlertEventArgs e)
        {
            if (exiting || e == null || e.Alert == null) return;
            popup.ShowAlert(e.Alert);
        }

        private void TogglePopup()
        {
            if (popup.Visible) popup.Hide();
            else ShowPopup(true);
        }

        private void ShowPopup(bool manual)
        {
            popup.PresentNearTray(manual);
        }

        private void TogglePause()
        {
            if (preview) return;
            monitor.TogglePause();
        }

        private void ToggleStartup()
        {
            if (preview) return;
            try
            {
                bool enabled = !StartupManager.IsEnabled();
                StartupManager.SetEnabled(enabled);
                startupItem.Checked = enabled;
            }
            catch
            {
                popup.ShowStatusAlert("设置失败", "无法修改开机启动设置");
            }
        }

        private void OpenDataFolder()
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = "explorer.exe";
                info.Arguments = "/select,\"" + dataPath.Replace("\"", string.Empty) + "\"";
                info.UseShellExecute = true;
                Process.Start(info);
            }
            catch
            {
                popup.ShowStatusAlert("无法打开目录", Path.GetDirectoryName(dataPath));
            }
        }

        private void ExitApplication()
        {
            if (exiting) return;
            exiting = true;
            trayIcon.Visible = false;
            monitor.Dispose();
            popup.Close();
            trayIcon.Dispose();
            database.Dispose();
            if (preview)
            {
                TryDelete(dataPath);
                TryDelete(dataPath + "-wal");
                TryDelete(dataPath + "-shm");
            }
            ExitThread();
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path) && Path.GetFileName(path).StartsWith("clash-sentinel-preview-", StringComparison.OrdinalIgnoreCase))
                    File.Delete(path);
            }
            catch { }
        }

        private static string TrimTrayText(string value)
        {
            if (value.Length <= 62) return value;
            return value.Substring(0, 61) + "…";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !exiting)
            {
                trayIcon.Visible = false;
                monitor.Dispose();
                popup.Dispose();
                trayIcon.Dispose();
                database.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ClashTrafficSentinel";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    string value = key == null ? null : key.GetValue(ValueName) as string;
                    return !string.IsNullOrWhiteSpace(value);
                }
            }
            catch { return false; }
        }

        public static void SetEnabled(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key == null) throw new InvalidOperationException("Run registry key is unavailable.");
                if (enabled) key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\" --startup", RegistryValueKind.String);
                else key.DeleteValue(ValueName, false);
            }
        }
    }
}
