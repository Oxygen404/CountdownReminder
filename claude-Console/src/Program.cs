using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Claude Console")]
[assembly: AssemblyDescription("Claude Code 额度托盘悬浮卡片")]
[assembly: AssemblyCompany("Claude Console")]
[assembly: AssemblyProduct("Claude Console")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace ClaudeConsole
{
    internal static class Program
    {
        private const string MutexName = "Local\\ClaudeConsole.2A5F11CB-82EB-47CB-987A-3CB6A7D2A461";

        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            using (Mutex mutex = new Mutex(true, MutexName, out created))
            {
                if (!created) return;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                bool previewMode = Array.Exists(args, delegate(string arg)
                {
                    return string.Equals(arg, "--preview", StringComparison.OrdinalIgnoreCase);
                });
                bool showAtStartup = previewMode || Array.Exists(args, delegate(string arg)
                {
                    return string.Equals(arg, "--show", StringComparison.OrdinalIgnoreCase);
                });
                Application.Run(new ClaudeConsoleContext(showAtStartup, previewMode));
            }
        }
    }

    internal sealed class ClaudeConsoleContext : ApplicationContext
    {
        private readonly NotifyIcon trayIcon;
        private readonly UsagePopup popup;
        private readonly UsageService usageService;
        private readonly System.Windows.Forms.Timer refreshTimer;
        private readonly ToolStripMenuItem showItem;
        private readonly ToolStripMenuItem startupItem;
        private bool refreshing;
        private bool exiting;

        public ClaudeConsoleContext(bool showAtStartup, bool previewMode)
        {
            usageService = new UsageService();
            popup = new UsagePopup();
            popup.AutoHideEnabled = !previewMode;
            popup.RefreshRequested += delegate { RefreshUsageAsync(true); };
            popup.ExitRequested += delegate { ExitApplication(); };
            popup.VisibleChanged += delegate
            {
                if (showItem != null) showItem.Text = popup.Visible ? "收起额度卡片" : "显示额度卡片";
            };

            showItem = new ToolStripMenuItem("显示额度卡片", null, delegate { TogglePopup(); });
            ToolStripMenuItem refreshItem = new ToolStripMenuItem("立即刷新", null, delegate { RefreshUsageAsync(true); });
            startupItem = new ToolStripMenuItem("开机自动启动", null, delegate { ToggleStartup(); });
            startupItem.Checked = StartupManager.IsEnabled();
            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出", null, delegate { ExitApplication(); });

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = new Font("Microsoft YaHei UI", 9f);
            menu.Items.Add(showItem);
            menu.Items.Add(refreshItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            trayIcon = new NotifyIcon();
            trayIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            trayIcon.Text = "Claude Console · 正在读取额度";
            trayIcon.ContextMenuStrip = menu;
            trayIcon.Visible = true;
            trayIcon.MouseClick += TrayIconMouseClick;
            trayIcon.DoubleClick += delegate { ShowPopup(); };

            refreshTimer = new System.Windows.Forms.Timer();
            refreshTimer.Interval = 5 * 60 * 1000;
            refreshTimer.Tick += delegate { RefreshUsageAsync(false); };
            if (previewMode)
            {
                DateTimeOffset now = DateTimeOffset.Now;
                UsageSnapshot preview = new UsageSnapshot(
                    new QuotaInfo(20, now.AddDays(2)),
                    new QuotaInfo(73, now.AddHours(2)),
                    now);
                popup.SetSnapshot(preview);
                trayIcon.Text = BuildTrayText(preview);
                ShowPopup();
            }
            else
            {
                refreshTimer.Start();
                RefreshUsageAsync(false);
                if (showAtStartup)
                {
                    popup.Shown += PopupShownOnce;
                    ShowPopup();
                }
            }
        }

        private void PopupShownOnce(object sender, EventArgs e)
        {
            popup.Shown -= PopupShownOnce;
        }

        private void TrayIconMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) TogglePopup();
        }

        private void TogglePopup()
        {
            if (popup.Visible) popup.Hide();
            else ShowPopup();
        }

        private void ShowPopup()
        {
            popup.PresentNearTray();
        }

        private async void RefreshUsageAsync(bool showAfterRefresh)
        {
            if (refreshing || exiting) return;
            refreshing = true;
            popup.SetLoading();
            try
            {
                UsageSnapshot snapshot = await usageService.FetchAsync();
                if (exiting) return;
                popup.SetSnapshot(snapshot);
                trayIcon.Text = BuildTrayText(snapshot);
                if (showAfterRefresh && !popup.Visible) ShowPopup();
            }
            catch (UsageException ex)
            {
                if (exiting) return;
                popup.SetError(ex.Message);
                trayIcon.Text = "Claude Console · " + TrimForTray(ex.Message);
                if (showAfterRefresh && !popup.Visible) ShowPopup();
            }
            catch
            {
                if (exiting) return;
                const string message = "暂时无法读取额度";
                popup.SetError(message);
                trayIcon.Text = "Claude Console · " + message;
                if (showAfterRefresh && !popup.Visible) ShowPopup();
            }
            finally
            {
                refreshing = false;
            }
        }

        internal static string BuildTrayText(UsageSnapshot snapshot)
        {
            return string.Format(CultureInfo.CurrentCulture,
                "Claude Console · 5 小时剩余 {0}% · 本周 {1}%",
                snapshot.Session.RemainingPercent,
                snapshot.Weekly.RemainingPercent);
        }

        private static string TrimForTray(string value)
        {
            if (value.Length <= 45) return value;
            return value.Substring(0, 44) + "…";
        }

        private void ToggleStartup()
        {
            try
            {
                bool enabled = !StartupManager.IsEnabled();
                StartupManager.SetEnabled(enabled);
                startupItem.Checked = enabled;
            }
            catch
            {
                popup.SetError("无法修改开机启动设置");
                ShowPopup();
            }
        }

        private void ExitApplication()
        {
            if (exiting) return;
            exiting = true;
            refreshTimer.Stop();
            trayIcon.Visible = false;
            popup.Close();
            trayIcon.Dispose();
            refreshTimer.Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !exiting)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                popup.Dispose();
                refreshTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class UsagePopup : Form
    {
        private const int PopupWidth = 368;
        private const int PopupHeight = 454;
        private readonly Font titleFont = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
        private readonly Font bodyFont = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular);
        private readonly Font bodyBoldFont = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
        private readonly Font numberFont = new Font("Bahnschrift", 34f, FontStyle.Regular);
        private readonly Font secondaryNumberFont = new Font("Bahnschrift", 22f, FontStyle.Regular);
        private readonly Font errorTitleFont = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
        private readonly Font tinyFont = new Font("Microsoft YaHei UI", 7.5f, FontStyle.Regular);
        private readonly System.Windows.Forms.Timer interactionTimer;
        private readonly Rectangle refreshBounds = new Rectangle(320, 22, 30, 30);
        private readonly Rectangle exitBounds = new Rectangle(321, 423, 32, 20);
        private UsageSnapshot snapshot;
        private string error;
        private bool loading = true;
        private bool refreshHover;
        private bool exitHover;
        private bool mouseWasDown;
        private DateTime ignoreDeactivateUntilUtc;

        public bool AutoHideEnabled { get; set; }

        public event EventHandler RefreshRequested;
        public event EventHandler ExitRequested;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        public UsagePopup()
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(PopupWidth, PopupHeight);
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Text = "Claude Console";
            BackColor = Color.FromArgb(247, 247, 246);
            DoubleBuffered = true;
            AutoHideEnabled = true;
            Padding = new Padding(0);
            Deactivate += HandleDeactivate;
            Resize += delegate { UpdateRoundedRegion(); };
            MouseMove += HandleMouseMove;
            MouseLeave += delegate
            {
                refreshHover = false;
                exitHover = false;
                Cursor = Cursors.Default;
                Invalidate();
            };
            MouseUp += HandleMouseUp;
            interactionTimer = new System.Windows.Forms.Timer();
            interactionTimer.Interval = 80;
            interactionTimer.Tick += HandleInteractionTimerTick;
            VisibleChanged += delegate
            {
                mouseWasDown = false;
                if (Visible) interactionTimer.Start();
                else interactionTimer.Stop();
            };
            UpdateRoundedRegion();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int CsDropShadow = 0x00020000;
                CreateParams parameters = base.CreateParams;
                parameters.ClassStyle |= CsDropShadow;
                return parameters;
            }
        }

        public void PositionNearTray()
        {
            Screen screen = Screen.FromPoint(Cursor.Position);
            Rectangle area = screen.WorkingArea;
            Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
        }

        public void PresentNearTray()
        {
            ignoreDeactivateUntilUtc = DateTime.UtcNow.AddMilliseconds(650);
            PositionNearTray();
            Show();
            BeginInvoke((MethodInvoker)delegate
            {
                if (!IsDisposed && Visible) Activate();
            });
        }

        private void HandleDeactivate(object sender, EventArgs e)
        {
            if (!AutoHideEnabled) return;
            if (DateTime.UtcNow < ignoreDeactivateUntilUtc)
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (!IsDisposed && Visible) Activate();
                });
                return;
            }
            Hide();
        }

        private void HandleInteractionTimerTick(object sender, EventArgs e)
        {
            bool mouseDown = (GetAsyncKeyState(0x01) & 0x8000) != 0;
            bool newMouseDown = mouseDown && !mouseWasDown;
            mouseWasDown = mouseDown;
            if (!newMouseDown || !AutoHideEnabled || !Visible) return;
            if (DateTime.UtcNow < ignoreDeactivateUntilUtc) return;
            if (!Bounds.Contains(Cursor.Position)) Hide();
        }

        public void SetLoading()
        {
            loading = true;
            error = null;
            Invalidate();
        }

        public void SetSnapshot(UsageSnapshot value)
        {
            snapshot = value;
            loading = false;
            error = null;
            Invalidate();
        }

        public void SetError(string message)
        {
            loading = false;
            error = message;
            Invalidate();
        }

        private void UpdateRoundedRegion()
        {
            using (GraphicsPath path = RoundedRectangle(new Rectangle(0, 0, Width, Height), 18))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            DrawBackground(g);
            DrawHeader(g);

            if (loading && snapshot == null) DrawLoading(g);
            else if (!string.IsNullOrEmpty(error)) DrawError(g);
            else if (snapshot != null) DrawUsage(g);

            DrawFooter(g);
        }

        private void DrawBackground(Graphics g)
        {
            using (LinearGradientBrush background = new LinearGradientBrush(
                ClientRectangle,
                Color.FromArgb(252, 251, 249),
                Color.FromArgb(242, 244, 245),
                90f))
            {
                g.FillRectangle(background, ClientRectangle);
            }

            using (SolidBrush glow = new SolidBrush(Color.FromArgb(18, 221, 119, 84)))
            {
                g.FillEllipse(glow, -84, -92, 226, 206);
            }

            using (Pen border = new Pen(Color.FromArgb(35, 55, 57, 58), 1f))
            using (GraphicsPath outline = RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), 18))
            {
                g.DrawPath(border, outline);
            }
        }

        private void DrawHeader(Graphics g)
        {
            Rectangle iconRect = new Rectangle(18, 18, 38, 38);
            using (LinearGradientBrush iconBrush = new LinearGradientBrush(
                iconRect, Color.FromArgb(239, 130, 92), Color.FromArgb(202, 78, 59), 45f))
            {
                g.FillEllipse(iconBrush, iconRect);
            }
            using (Pen ring = new Pen(Color.FromArgb(245, 255, 255, 255), 2.5f))
            {
                ring.StartCap = LineCap.Round;
                ring.EndCap = LineCap.Round;
                g.DrawArc(ring, 27, 27, 20, 20, -90, 245);
            }
            using (SolidBrush dot = new SolidBrush(Color.White))
            {
                g.FillEllipse(dot, 43, 40, 5, 5);
            }

            DrawText(g, "Claude Console", titleFont, Color.FromArgb(41, 42, 43), new PointF(68, 21));
            DrawText(g, "PRO  ·  本地额度助手", tinyFont, Color.FromArgb(116, 116, 114), new PointF(68, 42));

            using (SolidBrush button = new SolidBrush(refreshHover ? Color.FromArgb(30, 41, 42, 43) : Color.FromArgb(16, 41, 42, 43)))
            using (GraphicsPath path = RoundedRectangle(refreshBounds, 9))
            {
                g.FillPath(button, path);
            }
            using (Pen pen = new Pen(Color.FromArgb(115, 55, 57, 58), 1.45f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, 329, 30, 12, 12, -55, 285);
                g.DrawLine(pen, 340, 29, 341, 34);
                g.DrawLine(pen, 340, 29, 336, 30);
            }

            using (Pen divider = new Pen(Color.FromArgb(25, 34, 35, 36), 1f))
            {
                g.DrawLine(divider, 18, 70, Width - 18, 70);
            }
        }

        private void DrawLoading(Graphics g)
        {
            DrawText(g, "正在读取 Claude Code 额度…", bodyBoldFont, Color.FromArgb(70, 71, 72), new PointF(24, 102));
            DrawSkeleton(g, new Rectangle(24, 137, 152, 45), 12);
            DrawSkeleton(g, new Rectangle(24, 203, 320, 10), 5);
            DrawSkeleton(g, new Rectangle(24, 276, 116, 28), 9);
            DrawSkeleton(g, new Rectangle(24, 331, 320, 10), 5);
        }

        private static void DrawSkeleton(Graphics g, Rectangle bounds, int radius)
        {
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(20, 74, 75, 77)))
            using (GraphicsPath path = RoundedRectangle(bounds, radius))
            {
                g.FillPath(brush, path);
            }
        }

        private void DrawError(Graphics g)
        {
            using (SolidBrush glow = new SolidBrush(Color.FromArgb(28, 224, 112, 78)))
            {
                g.FillEllipse(glow, 24, 102, 54, 54);
            }
            using (Pen pen = new Pen(Color.FromArgb(214, 95, 66), 2f))
            {
                g.DrawEllipse(pen, 39, 117, 24, 24);
                g.DrawLine(pen, 51, 123, 51, 132);
            }
            using (SolidBrush dot = new SolidBrush(Color.FromArgb(214, 95, 66)))
            {
                g.FillEllipse(dot, 49.5f, 135f, 3f, 3f);
            }

            DrawText(g, "额度读取失败", errorTitleFont, Color.FromArgb(45, 46, 47), new PointF(24, 177));
            RectangleF messageBounds = new RectangleF(24, 211, Width - 48, 52);
            TextRenderer.DrawText(g, error, bodyFont, Rectangle.Round(messageBounds), Color.FromArgb(98, 98, 96),
                TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Top);

            Rectangle retry = new Rectangle(24, 287, 126, 36);
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(220, 105, 76)))
            using (GraphicsPath path = RoundedRectangle(retry, 10))
            {
                g.FillPath(brush, path);
            }
            TextRenderer.DrawText(g, "重新读取", bodyBoldFont, retry, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            DrawText(g, "也可以先在终端运行 claude auth status", tinyFont, Color.FromArgb(126, 126, 124), new PointF(24, 338));
        }

        private void DrawUsage(Graphics g)
        {
            QuotaInfo weekly = snapshot.Weekly;
            QuotaInfo session = snapshot.Session;

            DrawSectionMarker(g, new Point(24, 91), Color.FromArgb(42, 151, 142));
            DrawText(g, "5 小时额度", bodyBoldFont, Color.FromArgb(62, 63, 64), new PointF(39, 83));
            DrawRightAligned(g, "当前窗口剩余", tinyFont, Color.FromArgb(96, 96, 94), 344, 84);

            DrawText(g, session.RemainingPercent.ToString(CultureInfo.CurrentCulture) + "%", numberFont,
                Color.FromArgb(54, 55, 56), new PointF(20, 108));
            DrawRightAligned(g, FormatReset(session.ResetsAt), tinyFont, Color.FromArgb(106, 106, 104), 344, 133);

            DrawProgress(g, new Rectangle(24, 174, 320, 9), session.RemainingPercent / 100f,
                Color.FromArgb(42, 151, 142));

            Color sessionStateColor = StatusColor(session.RemainingPercent);
            DrawSectionMarker(g, new Point(24, 208), sessionStateColor);
            DrawText(g, StatusText(session.RemainingPercent), bodyBoldFont, sessionStateColor, new PointF(39, 200));
            DrawRightAligned(g, "已使用 " + session.UsedPercent + "%", tinyFont, Color.FromArgb(109, 109, 107), 344, 202);

            using (Pen divider = new Pen(Color.FromArgb(28, 42, 43, 44), 1f))
            {
                g.DrawLine(divider, 24, 236, 344, 236);
            }

            DrawSectionMarker(g, new Point(24, 263), Color.FromArgb(218, 102, 73));
            DrawText(g, "本周额度", bodyBoldFont, Color.FromArgb(62, 63, 64), new PointF(39, 255));
            DrawRightAligned(g, "长期余量", tinyFont, Color.FromArgb(96, 96, 94), 344, 256);

            DrawText(g, weekly.RemainingPercent.ToString(CultureInfo.CurrentCulture) + "%", secondaryNumberFont,
                Color.FromArgb(54, 55, 56), new PointF(22, 287));
            DrawText(g, "剩余", tinyFont, Color.FromArgb(111, 111, 109), new PointF(92, 302));
            DrawRightAligned(g, FormatReset(weekly.ResetsAt), tinyFont, Color.FromArgb(106, 106, 104), 344, 302);

            DrawProgress(g, new Rectangle(24, 337, 320, 9), weekly.RemainingPercent / 100f,
                Color.FromArgb(218, 102, 73));

            Rectangle hint = new Rectangle(24, 367, 320, 35);
            using (SolidBrush hintBrush = new SolidBrush(Color.FromArgb(13, 42, 151, 142)))
            using (GraphicsPath hintPath = RoundedRectangle(hint, 10))
            {
                g.FillPath(hintBrush, hintPath);
            }
            DrawText(g, BuildSessionMessage(session), tinyFont, Color.FromArgb(70, 91, 88), new PointF(36, 377));
        }

        private void DrawFooter(Graphics g)
        {
            using (Pen divider = new Pen(Color.FromArgb(22, 42, 43, 44), 1f))
            {
                g.DrawLine(divider, 18, 414, Width - 18, 414);
            }

            string updated = snapshot == null
                ? "每 5 分钟自动刷新"
                : "更新于 " + snapshot.UpdatedAt.ToString("HH:mm") + "  ·  每 5 分钟自动刷新";
            DrawText(g, updated, tinyFont, Color.FromArgb(137, 137, 134), new PointF(20, 426));
            DrawText(g, "退出", tinyFont, exitHover ? Color.FromArgb(205, 80, 59) : Color.FromArgb(106, 106, 104), new PointF(326, 426));
        }

        private void DrawProgress(Graphics g, Rectangle bounds, float ratio, Color accent)
        {
            ratio = Math.Max(0f, Math.Min(1f, ratio));
            using (SolidBrush track = new SolidBrush(Color.FromArgb(23, 55, 56, 57)))
            using (GraphicsPath trackPath = RoundedRectangle(bounds, bounds.Height / 2))
            {
                g.FillPath(track, trackPath);
            }

            int fillWidth = Math.Max(bounds.Height, (int)Math.Round(bounds.Width * ratio));
            Rectangle fillBounds = new Rectangle(bounds.X, bounds.Y, fillWidth, bounds.Height);
            using (LinearGradientBrush fill = new LinearGradientBrush(fillBounds,
                ControlPaint.Light(accent, 0.18f), accent, 0f))
            using (GraphicsPath fillPath = RoundedRectangle(fillBounds, bounds.Height / 2))
            {
                g.FillPath(fill, fillPath);
            }
        }

        private static void DrawSectionMarker(Graphics g, Point center, Color color)
        {
            using (SolidBrush glow = new SolidBrush(Color.FromArgb(42, color)))
            using (SolidBrush core = new SolidBrush(color))
            {
                g.FillEllipse(glow, center.X - 6, center.Y - 6, 12, 12);
                g.FillEllipse(core, center.X - 3, center.Y - 3, 6, 6);
            }
        }

        private static string StatusText(int remaining)
        {
            if (remaining >= 60) return "额度充足";
            if (remaining >= 30) return "使用平稳";
            if (remaining >= 15) return "注意余量";
            return "额度偏低";
        }

        private static Color StatusColor(int remaining)
        {
            if (remaining >= 60) return Color.FromArgb(46, 150, 107);
            if (remaining >= 30) return Color.FromArgb(204, 139, 48);
            return Color.FromArgb(210, 83, 64);
        }

        private static string BuildSessionMessage(QuotaInfo session)
        {
            if (session.RemainingPercent >= 60) return "当前 5 小时余量充足，可以继续专注当前任务。";
            if (session.RemainingPercent >= 30) return "当前 5 小时余量适中，长任务前留意消耗。";
            return "当前 5 小时余量较低，建议优先处理关键任务。";
        }

        private static string FormatReset(DateTimeOffset value)
        {
            if (value == DateTimeOffset.MinValue) return "重置时间未知";
            DateTimeOffset local = value.ToLocalTime();
            return local.ToString("M 月 d 日 HH:mm", CultureInfo.CurrentCulture) + " 重置";
        }

        private void HandleMouseMove(object sender, MouseEventArgs e)
        {
            bool newRefresh = refreshBounds.Contains(e.Location);
            bool newExit = exitBounds.Contains(e.Location);
            if (newRefresh != refreshHover || newExit != exitHover)
            {
                refreshHover = newRefresh;
                exitHover = newExit;
                Cursor = (refreshHover || exitHover) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        private void HandleMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (refreshBounds.Contains(e.Location) || (!string.IsNullOrEmpty(error) && new Rectangle(24, 287, 126, 36).Contains(e.Location)))
            {
                EventHandler handler = RefreshRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            }
            else if (exitBounds.Contains(e.Location))
            {
                EventHandler handler = ExitRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        private static void DrawText(Graphics g, string text, Font font, Color color, PointF location)
        {
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.DrawString(text, font, brush, location, StringFormat.GenericTypographic);
            }
        }

        private static void DrawRightAligned(Graphics g, string text, Font font, Color color, float right, float y)
        {
            SizeF size = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
            DrawText(g, text, font, color, new PointF(right - size.Width, y));
        }

        internal static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            if (diameter <= 0)
            {
                path.AddRectangle(rectangle);
                path.CloseFigure();
                return path;
            }

            Rectangle arc = new Rectangle(rectangle.Location, new Size(diameter, diameter));
            path.AddArc(arc, 180, 90);
            arc.X = rectangle.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = rectangle.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = rectangle.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                titleFont.Dispose();
                bodyFont.Dispose();
                bodyBoldFont.Dispose();
                numberFont.Dispose();
                secondaryNumberFont.Dispose();
                errorTitleFont.Dispose();
                tinyFont.Dispose();
                interactionTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class UsageService
    {
        private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
        private static readonly HttpClient Client = CreateHttpClient();
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

        private static HttpClient CreateHttpClient()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            HttpClientHandler handler = new HttpClientHandler();
            handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            HttpClient client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(10);
            return client;
        }

        public async Task<UsageSnapshot> FetchAsync()
        {
            Credential credential = LoadCredential();
            if (credential.ExpiresAt <= DateTimeOffset.Now.AddMinutes(2))
            {
                await RefreshWithClaudeCliAsync();
                credential = LoadCredential();
            }

            HttpResponseMessage response = await SendAsync(credential.AccessToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                await RefreshWithClaudeCliAsync();
                credential = LoadCredential();
                response = await SendAsync(credential.AccessToken);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    if ((int)response.StatusCode == 429) throw new UsageException("Claude 服务暂时限制了查询，请稍后重试");
                    if ((int)response.StatusCode >= 500) throw new UsageException("Claude 服务暂时不可用，请稍后重试");
                    throw new UsageException("无法读取额度，请确认 Claude Code 已登录");
                }

                string json = await response.Content.ReadAsStringAsync();
                return ParseUsage(json, DateTimeOffset.Now);
            }
        }

        private async Task<HttpResponseMessage> SendAsync(string accessToken)
        {
            try
            {
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                request.Headers.TryAddWithoutValidation("User-Agent", "claude-console/1.0");
                HttpResponseMessage response = await Client.SendAsync(request);
                request.Dispose();
                return response;
            }
            catch (TaskCanceledException)
            {
                throw new UsageException("连接 Claude 服务超时，请检查网络");
            }
            catch (HttpRequestException)
            {
                throw new UsageException("无法连接 Claude 服务，请检查网络");
            }
        }

        private Credential LoadCredential()
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string path = Path.Combine(profile, ".claude", ".credentials.json");
            if (!File.Exists(path)) throw new UsageException("未找到 Claude Code 登录信息，请先运行 claude auth login");

            try
            {
                string json = File.ReadAllText(path);
                Dictionary<string, object> root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                Dictionary<string, object> oauth = GetDictionary(root, "claudeAiOauth");
                string token = GetString(oauth, "accessToken");
                long expiresAt = GetLong(oauth, "expiresAt");
                if (string.IsNullOrWhiteSpace(token)) throw new UsageException("Claude Code 尚未登录，请先运行 claude auth login");

                DateTimeOffset expiry = expiresAt > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(expiresAt)
                    : DateTimeOffset.MaxValue;
                return new Credential(token, expiry);
            }
            catch (UsageException)
            {
                throw;
            }
            catch
            {
                throw new UsageException("Claude Code 登录信息无法读取，请运行 claude auth status 检查");
            }
        }

        private async Task RefreshWithClaudeCliAsync()
        {
            string executable = FindClaudeExecutable();
            if (executable == null) throw new UsageException("Claude 登录已过期，且未找到 claude.exe");

            bool succeeded = await Task.Run(delegate
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = executable;
                info.Arguments = "auth status --json";
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WindowStyle = ProcessWindowStyle.Hidden;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;

                try
                {
                    using (Process process = Process.Start(info))
                    {
                        if (process == null) return false;
                        if (!process.WaitForExit(15000))
                        {
                            try { process.Kill(); } catch { }
                            return false;
                        }
                        return process.ExitCode == 0;
                    }
                }
                catch
                {
                    return false;
                }
            });

            if (!succeeded) throw new UsageException("Claude 登录状态需要更新，请运行 claude auth status");
        }

        private static string FindClaudeExecutable()
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string local = Path.Combine(profile, ".local", "bin", "claude.exe");
            if (File.Exists(local)) return local;

            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string raw in path.Split(Path.PathSeparator))
            {
                string directory = raw.Trim().Trim('"');
                if (directory.Length == 0) continue;
                try
                {
                    string candidate = Path.Combine(directory, "claude.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }

        internal UsageSnapshot ParseUsage(string json, DateTimeOffset updatedAt)
        {
            try
            {
                Dictionary<string, object> root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                Dictionary<string, object> weeklyData = GetDictionary(root, "seven_day");
                Dictionary<string, object> sessionData = GetDictionary(root, "five_hour");

                if (weeklyData == null || sessionData == null)
                {
                    QuotaInfo fromLimitsWeekly = null;
                    QuotaInfo fromLimitsSession = null;
                    object limitsObject;
                    if (root != null && root.TryGetValue("limits", out limitsObject))
                    {
                        object[] limits = limitsObject as object[];
                        if (limits != null)
                        {
                            foreach (object item in limits)
                            {
                                Dictionary<string, object> limit = item as Dictionary<string, object>;
                                string kind = GetString(limit, "kind");
                                if (kind == "weekly_all") fromLimitsWeekly = ParseLimit(limit);
                                if (kind == "session") fromLimitsSession = ParseLimit(limit);
                            }
                        }
                    }

                    if (weeklyData == null && fromLimitsWeekly == null) throw new UsageException("当前账号没有可显示的周额度信息");
                    if (sessionData == null && fromLimitsSession == null) throw new UsageException("当前账号没有可显示的 5 小时额度信息");
                    return new UsageSnapshot(fromLimitsWeekly ?? ParseQuota(weeklyData), fromLimitsSession ?? ParseQuota(sessionData), updatedAt);
                }

                return new UsageSnapshot(ParseQuota(weeklyData), ParseQuota(sessionData), updatedAt);
            }
            catch (UsageException)
            {
                throw;
            }
            catch
            {
                throw new UsageException("Claude 返回了无法识别的额度数据");
            }
        }

        private static QuotaInfo ParseQuota(Dictionary<string, object> data)
        {
            int used = ClampPercent((int)Math.Round(GetDouble(data, "utilization"), MidpointRounding.AwayFromZero));
            DateTimeOffset reset = ParseDate(GetString(data, "resets_at"));
            return new QuotaInfo(used, reset);
        }

        private static QuotaInfo ParseLimit(Dictionary<string, object> data)
        {
            int used = ClampPercent((int)Math.Round(GetDouble(data, "percent"), MidpointRounding.AwayFromZero));
            DateTimeOffset reset = ParseDate(GetString(data, "resets_at"));
            return new QuotaInfo(used, reset);
        }

        private static DateTimeOffset ParseDate(string value)
        {
            DateTimeOffset result;
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out result)
                ? result
                : DateTimeOffset.MinValue;
        }

        private static int ClampPercent(int value)
        {
            return Math.Max(0, Math.Min(100, value));
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> data, string key)
        {
            if (data == null) return null;
            object value;
            if (!data.TryGetValue(key, out value)) return null;
            return value as Dictionary<string, object>;
        }

        private static string GetString(Dictionary<string, object> data, string key)
        {
            if (data == null) return null;
            object value;
            if (!data.TryGetValue(key, out value) || value == null) return null;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static double GetDouble(Dictionary<string, object> data, string key)
        {
            if (data == null) return 0;
            object value;
            if (!data.TryGetValue(key, out value) || value == null) return 0;
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        private static long GetLong(Dictionary<string, object> data, string key)
        {
            if (data == null) return 0;
            object value;
            if (!data.TryGetValue(key, out value) || value == null) return 0;
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
    }

    internal sealed class Credential
    {
        public Credential(string accessToken, DateTimeOffset expiresAt)
        {
            AccessToken = accessToken;
            ExpiresAt = expiresAt;
        }

        public string AccessToken { get; private set; }
        public DateTimeOffset ExpiresAt { get; private set; }
    }

    internal sealed class QuotaInfo
    {
        public QuotaInfo(int usedPercent, DateTimeOffset resetsAt)
        {
            UsedPercent = Math.Max(0, Math.Min(100, usedPercent));
            ResetsAt = resetsAt;
        }

        public int UsedPercent { get; private set; }
        public int RemainingPercent { get { return 100 - UsedPercent; } }
        public DateTimeOffset ResetsAt { get; private set; }
    }

    internal sealed class UsageSnapshot
    {
        public UsageSnapshot(QuotaInfo weekly, QuotaInfo session, DateTimeOffset updatedAt)
        {
            Weekly = weekly;
            Session = session;
            UpdatedAt = updatedAt;
        }

        public QuotaInfo Weekly { get; private set; }
        public QuotaInfo Session { get; private set; }
        public DateTimeOffset UpdatedAt { get; private set; }
    }

    internal sealed class UsageException : Exception
    {
        public UsageException(string message) : base(message) { }
    }

    internal static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ClaudeConsole";

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
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key == null) throw new InvalidOperationException("Run registry key is unavailable.");
                if (enabled)
                {
                    string command = "\"" + Application.ExecutablePath + "\" --startup";
                    key.SetValue(ValueName, command, RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }
    }
}
