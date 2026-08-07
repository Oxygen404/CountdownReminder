using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClashTrafficSentinel
{
    internal sealed class TrafficPopup : Form
    {
        private const int PopupWidth = 430;
        private const int PopupHeight = 680;
        private readonly TrafficDatabase database;
        private readonly TrafficMonitor monitor;
        private readonly Font brandFont = new Font("Bahnschrift SemiBold", 14f, FontStyle.Regular);
        private readonly Font displayFont = new Font("Bahnschrift SemiBold", 35f, FontStyle.Regular);
        private readonly Font displayUnitFont = new Font("Bahnschrift", 12f, FontStyle.Regular);
        private readonly Font sectionFont = new Font("Microsoft YaHei UI", 9.2f, FontStyle.Bold);
        private readonly Font bodyFont = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular);
        private readonly Font bodyBoldFont = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
        private readonly Font tinyFont = new Font("Microsoft YaHei UI", 7.4f, FontStyle.Regular);
        private readonly Timer interactionTimer;
        private readonly Timer alertTimer;
        private readonly List<RowHit> rowHits = new List<RowHit>();
        private readonly Rectangle closeBounds = new Rectangle(389, 22, 22, 22);
        private readonly Rectangle settingsBounds = new Rectangle(370, 638, 36, 28);
        private readonly Rectangle todayBounds = new Rectangle(22, 248, 188, 34);
        private readonly Rectangle weekBounds = new Rectangle(220, 248, 188, 34);
        private readonly Rectangle appsBounds = new Rectangle(22, 291, 188, 34);
        private readonly Rectangle domainsBounds = new Rectangle(220, 291, 188, 34);
        private MonitorState state;
        private TrafficSummary summary;
        private string period = "today";
        private string dimension = "app";
        private string selectedAppKey;
        private string selectedAppLabel;
        private AlertInfo alert;
        private bool settingsMode;
        private bool mouseWasDown;
        private DateTime ignoreDeactivateUntilUtc;
        private bool previewMode;

        public event EventHandler OpenDataRequested;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        public TrafficPopup(TrafficDatabase database, TrafficMonitor monitor)
        {
            this.database = database;
            this.monitor = monitor;
            state = monitor.State;
            ClientSize = new Size(PopupWidth, PopupHeight);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ShowIcon = false;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Text = "Clash 流量哨兵";
            BackColor = Color.FromArgb(6, 18, 23);
            DoubleBuffered = true;
            AccessibleName = "Clash 流量哨兵流量卡片";
            Resize += delegate { UpdateRoundedRegion(); };
            Deactivate += HandleDeactivate;
            MouseUp += HandleMouseUp;
            MouseMove += HandleMouseMove;
            MouseLeave += delegate { Cursor = Cursors.Default; };
            VisibleChanged += delegate
            {
                mouseWasDown = false;
                if (Visible)
                {
                    interactionTimer.Start();
                    RefreshSummary();
                }
                else interactionTimer.Stop();
            };

            interactionTimer = new Timer();
            interactionTimer.Interval = 90;
            interactionTimer.Tick += HandleInteractionTimerTick;
            alertTimer = new Timer();
            alertTimer.Interval = 12000;
            alertTimer.Tick += delegate
            {
                alertTimer.Stop();
                alert = null;
                Invalidate();
                if (Visible && !ContainsFocus && !previewMode) Hide();
            };
            UpdateRoundedRegion();
            RefreshSummary();
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

        public void SetPreviewState()
        {
            previewMode = true;
            state = new MonitorState
            {
                Connected = true,
                StatusText = "正在监控代理流量",
                DownloadSpeed = 384 * 1024,
                UploadSpeed = 42 * 1024,
                ActiveProxyConnections = 27,
                UpdatedAt = DateTime.Now
            };
            alert = new AlertInfo
            {
                Title = "检测到高流量应用",
                Message = "Google Chrome · 3 分钟内 238 MB"
            };
            RefreshSummary();
        }

        public void UpdateMonitorState(MonitorState value)
        {
            if (value == null) return;
            state = value;
            if (Visible) RefreshSummary();
            Invalidate();
        }

        public void ShowAlert(AlertInfo value)
        {
            if (value == null) return;
            alert = value;
            alertTimer.Stop();
            alertTimer.Start();
            if (!Visible) PresentNearTray(false);
            else Invalidate();
        }

        public void ShowStatusAlert(string title, string message)
        {
            ShowAlert(new AlertInfo { Title = title, Message = message });
        }

        public void PresentNearTray(bool manual)
        {
            Screen screen = Screen.FromPoint(Cursor.Position);
            Rectangle area = screen.WorkingArea;
            Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
            ignoreDeactivateUntilUtc = DateTime.UtcNow.AddMilliseconds(manual ? 650 : 200);
            RefreshSummary();
            Show();
            if (manual)
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (!IsDisposed && Visible) Activate();
                });
            }
        }

        private void HandleDeactivate(object sender, EventArgs e)
        {
            if (previewMode || alert != null || DateTime.UtcNow < ignoreDeactivateUntilUtc) return;
            Hide();
        }

        private void HandleInteractionTimerTick(object sender, EventArgs e)
        {
            bool mouseDown = (GetAsyncKeyState(0x01) & 0x8000) != 0;
            bool newMouseDown = mouseDown && !mouseWasDown;
            mouseWasDown = mouseDown;
            if (!newMouseDown || !Visible || previewMode || alert != null) return;
            if (DateTime.UtcNow < ignoreDeactivateUntilUtc) return;
            if (!Bounds.Contains(Cursor.Position)) Hide();
        }

        private void RefreshSummary()
        {
            DateTime now = DateTime.Now;
            DateTime from;
            DateTime to;
            if (period == "week")
            {
                int offset = ((int)now.DayOfWeek + 6) % 7;
                from = now.Date.AddDays(-offset);
                to = from.AddDays(7);
            }
            else
            {
                from = now.Date;
                to = from.AddDays(1);
            }
            try
            {
                summary = database.GetSummary(from, to, dimension, selectedAppKey);
            }
            catch
            {
                summary = new TrafficSummary();
            }
            Invalidate();
        }

        private void UpdateRoundedRegion()
        {
            using (GraphicsPath path = RoundedRectangle(new Rectangle(0, 0, Width, Height), 20))
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
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            DrawAtmosphere(g);
            DrawHeader(g);
            if (settingsMode) DrawSettings(g);
            else DrawDashboard(g);
            DrawFooter(g);
        }

        private void DrawAtmosphere(Graphics g)
        {
            using (LinearGradientBrush background = new LinearGradientBrush(
                ClientRectangle, Color.FromArgb(7, 20, 26), Color.FromArgb(9, 33, 36), 72f))
            {
                g.FillRectangle(background, ClientRectangle);
            }
            using (SolidBrush glow = new SolidBrush(Color.FromArgb(24, 36, 221, 172)))
            {
                g.FillEllipse(glow, 250, -150, 340, 340);
            }
            using (Pen grid = new Pen(Color.FromArgb(10, 128, 207, 180), 1f))
            {
                for (int y = 100; y < Height; y += 44) g.DrawLine(grid, 0, y, Width, y);
                for (int x = 20; x < Width; x += 64) g.DrawLine(grid, x, 76, x, Height);
            }
            using (Pen border = new Pen(Color.FromArgb(58, 90, 174, 157), 1f))
            using (GraphicsPath path = RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), 20))
            {
                g.DrawPath(border, path);
            }
        }

        private void DrawHeader(Graphics g)
        {
            DrawRadarMark(g, new Point(34, 34));
            DrawText(g, "CLASH", brandFont, Color.FromArgb(227, 246, 240), new PointF(62, 17));
            DrawText(g, "流量哨兵", bodyFont, Color.FromArgb(129, 166, 159), new PointF(63, 43));

            Color dot = state != null && state.Connected ? Color.FromArgb(64, 229, 174) : Color.FromArgb(243, 165, 73);
            using (SolidBrush halo = new SolidBrush(Color.FromArgb(45, dot)))
            using (SolidBrush core = new SolidBrush(dot))
            {
                g.FillEllipse(halo, 288, 25, 16, 16);
                g.FillEllipse(core, 293, 30, 6, 6);
            }
            string live = state != null && state.Connected ? "LIVE" : state != null && state.Paused ? "PAUSED" : "WAIT";
            DrawText(g, live, tinyFont, dot, new PointF(308, 27));
            DrawClose(g);
        }

        private void DrawDashboard(Graphics g)
        {
            DrawSignalStrip(g);
            DrawHero(g);
            DrawSegment(g, todayBounds, "今天", period == "today");
            DrawSegment(g, weekBounds, "本周", period == "week");

            if (selectedAppKey == null)
            {
                DrawSegment(g, appsBounds, "应用", dimension == "app");
                DrawSegment(g, domainsBounds, "域名", dimension == "domain");
            }
            else
            {
                using (GraphicsPath path = RoundedRectangle(new Rectangle(22, 291, 386, 34), 10))
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(104, 15, 49, 51)))
                {
                    g.FillPath(fill, path);
                }
                DrawText(g, "‹  " + Ellipsize(g, selectedAppLabel, bodyBoldFont, 285), bodyBoldFont,
                    Color.FromArgb(130, 236, 202), new PointF(37, 299));
                DrawRight(g, "域名明细", tinyFont, Color.FromArgb(106, 148, 142), 391, 301);
            }
            DrawRanking(g);
        }

        private void DrawSignalStrip(Graphics g)
        {
            Rectangle bounds = new Rectangle(22, 78, 386, 48);
            bool hasAlert = alert != null;
            Color accent = hasAlert ? Color.FromArgb(255, 174, 76) :
                state != null && state.Connected ? Color.FromArgb(57, 220, 168) : Color.FromArgb(242, 154, 68);
            using (GraphicsPath path = RoundedRectangle(bounds, 12))
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(hasAlert ? 37 : 24, accent)))
            using (Pen border = new Pen(Color.FromArgb(hasAlert ? 118 : 64, accent), 1f))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            if (hasAlert)
            {
                DrawAlertBolt(g, new Point(42, 102), accent);
                DrawText(g, alert.Title, bodyBoldFont, Color.FromArgb(255, 221, 166), new PointF(63, 85));
                DrawText(g, Ellipsize(g, alert.Message, tinyFont, 325), tinyFont, Color.FromArgb(214, 190, 150), new PointF(63, 106));
            }
            else
            {
                DrawPulse(g, new Rectangle(34, 91, 22, 22), accent);
                string title = state == null ? "正在启动" : state.StatusText;
                DrawText(g, Ellipsize(g, title, bodyBoldFont, 215), bodyBoldFont, Color.FromArgb(198, 231, 221), new PointF(68, 85));
                string detail = state != null && state.Connected
                    ? state.ActiveProxyConnections + " 条代理连接 · 不含 DIRECT"
                    : "恢复后会自动继续累计";
                DrawText(g, detail, tinyFont, Color.FromArgb(110, 153, 146), new PointF(68, 106));
                DrawRight(g, state != null && state.UpdatedAt != DateTime.MinValue ? state.UpdatedAt.ToString("HH:mm:ss") : "--:--:--",
                    tinyFont, Color.FromArgb(102, 144, 137), 392, 95);
            }
        }

        private void DrawHero(Graphics g)
        {
            long total = summary == null ? 0 : summary.Total;
            string formatted = TrafficIdentity.FormatBytes(total);
            int split = formatted.LastIndexOf(' ');
            string number = split > 0 ? formatted.Substring(0, split) : formatted;
            string unit = split > 0 ? formatted.Substring(split + 1) : string.Empty;
            DrawText(g, period == "week" ? "本周代理消耗" : "今日代理消耗", tinyFont,
                Color.FromArgb(104, 153, 144), new PointF(24, 142));
            DrawText(g, number, displayFont, Color.FromArgb(234, 249, 243), new PointF(20, 156));
            SizeF numberSize = g.MeasureString(number, displayFont, PointF.Empty, StringFormat.GenericTypographic);
            DrawText(g, unit, displayUnitFont, Color.FromArgb(82, 219, 177), new PointF(25 + numberSize.Width, 190));

            long upload = summary == null ? 0 : summary.Upload;
            long download = summary == null ? 0 : summary.Download;
            DrawMetric(g, new Rectangle(269, 145, 139, 36), "↓ 下载", TrafficIdentity.FormatBytes(download), Color.FromArgb(66, 196, 255));
            DrawMetric(g, new Rectangle(269, 188, 139, 36), "↑ 上传", TrafficIdentity.FormatBytes(upload), Color.FromArgb(255, 180, 78));
        }

        private void DrawMetric(Graphics g, Rectangle bounds, string label, string value, Color accent)
        {
            using (GraphicsPath path = RoundedRectangle(bounds, 9))
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(55, 10, 37, 40)))
            using (Pen border = new Pen(Color.FromArgb(42, accent)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            DrawText(g, label, tinyFont, accent, new PointF(bounds.X + 10, bounds.Y + 5));
            DrawRight(g, value, bodyBoldFont, Color.FromArgb(211, 231, 225), bounds.Right - 9, bounds.Y + 15);
        }

        private void DrawSegment(Graphics g, Rectangle bounds, string label, bool active)
        {
            using (GraphicsPath path = RoundedRectangle(bounds, 10))
            using (SolidBrush fill = new SolidBrush(active ? Color.FromArgb(170, 23, 89, 78) : Color.FromArgb(95, 9, 31, 35)))
            using (Pen border = new Pen(active ? Color.FromArgb(125, 61, 226, 172) : Color.FromArgb(31, 99, 137, 128)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            SizeF size = g.MeasureString(label, bodyBoldFont, PointF.Empty, StringFormat.GenericTypographic);
            DrawText(g, label, bodyBoldFont, active ? Color.FromArgb(164, 248, 215) : Color.FromArgb(112, 149, 143),
                new PointF(bounds.X + (bounds.Width - size.Width) / 2f, bounds.Y + 8));
        }

        private void DrawRanking(Graphics g)
        {
            rowHits.Clear();
            string title = selectedAppKey != null ? "域名去向" : dimension == "domain" ? "域名排行" : "应用排行";
            DrawText(g, title, sectionFont, Color.FromArgb(205, 231, 223), new PointF(24, 338));
            DrawRight(g, "上传 + 下载", tinyFont, Color.FromArgb(91, 137, 130), 405, 341);
            if (summary == null || summary.Rows.Count == 0)
            {
                DrawEmpty(g);
                return;
            }

            long maximum = Math.Max(1, summary.Rows[0].Total);
            int count = Math.Min(6, summary.Rows.Count);
            for (int i = 0; i < count; i++)
            {
                TrafficRow row = summary.Rows[i];
                Rectangle bounds = new Rectangle(22, 364 + i * 43, 386, 38);
                bool clickable = selectedAppKey == null && dimension == "app";
                rowHits.Add(new RowHit(bounds, row.Key, row.Label, clickable));
                using (GraphicsPath path = RoundedRectangle(bounds, 9))
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(i == 0 ? 82 : 48, 12, 43, 45)))
                {
                    g.FillPath(fill, path);
                }
                int barWidth = (int)Math.Round((bounds.Width - 2) * Math.Min(1.0, (double)row.Total / maximum));
                if (barWidth > 4)
                {
                    Rectangle bar = new Rectangle(bounds.X + 1, bounds.Bottom - 3, barWidth, 2);
                    using (LinearGradientBrush brush = new LinearGradientBrush(bar,
                        Color.FromArgb(44, 211, 169), Color.FromArgb(63, 153, 236), 0f))
                        g.FillRectangle(brush, bar);
                }
                DrawRankNumber(g, i + 1, new Point(bounds.X + 18, bounds.Y + 19));
                DrawText(g, Ellipsize(g, row.Label, bodyBoldFont, 220), bodyBoldFont,
                    Color.FromArgb(211, 235, 227), new PointF(bounds.X + 39, bounds.Y + 9));
                DrawRight(g, TrafficIdentity.FormatBytes(row.Total), bodyBoldFont,
                    Color.FromArgb(132, 226, 198), bounds.Right - (clickable ? 24 : 11), bounds.Y + 9);
                if (clickable) DrawChevron(g, new Point(bounds.Right - 12, bounds.Y + 19));
            }
        }

        private void DrawEmpty(Graphics g)
        {
            using (Pen pen = new Pen(Color.FromArgb(67, 81, 145, 134), 1.4f))
            {
                pen.DashStyle = DashStyle.Dash;
                g.DrawEllipse(pen, 182, 420, 66, 66);
                g.DrawArc(pen, 196, 434, 38, 38, 205, 290);
            }
            DrawText(g, "从现在开始记录", sectionFont, Color.FromArgb(154, 190, 182), new PointF(153, 505));
            DrawText(g, "产生代理流量后，排行会自动出现", tinyFont, Color.FromArgb(89, 132, 125), new PointF(128, 531));
        }

        private void DrawSettings(Graphics g)
        {
            DrawText(g, "提醒设置", sectionFont, Color.FromArgb(215, 239, 231), new PointF(24, 91));
            DrawText(g, "所有设置只保存在本机数据库", tinyFont, Color.FromArgb(97, 143, 135), new PointF(24, 116));
            DrawSettingCard(g, new Rectangle(22, 151, 386, 100), "应用高流量", "滚动 3 分钟达到", monitor.Settings.BurstMegabytes + " MB", "burst");
            DrawSettingCard(g, new Rectangle(22, 263, 386, 100), "每日累计", "达到后每增加同等流量提醒", monitor.Settings.DailyGigabytes.ToString("0.#", CultureInfo.InvariantCulture) + " GB", "daily");
            DrawSettingCard(g, new Rectangle(22, 375, 386, 100), "应用冷却", "同一应用两次提醒至少间隔", monitor.Settings.AppCooldownMinutes + " 分钟", "cooldown");

            Rectangle data = new Rectangle(22, 494, 386, 72);
            using (GraphicsPath path = RoundedRectangle(data, 12))
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(75, 11, 42, 44)))
            using (Pen border = new Pen(Color.FromArgb(43, 78, 161, 145)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            DrawDatabaseIcon(g, new Point(47, 530));
            DrawText(g, "本地数据库", bodyBoldFont, Color.FromArgb(203, 231, 222), new PointF(72, 508));
            DrawText(g, "traffic-data.db · 永久保留 · Git 已忽略", tinyFont, Color.FromArgb(102, 151, 142), new PointF(72, 533));
            DrawRight(g, "打开  ›", bodyBoldFont, Color.FromArgb(108, 234, 193), 391, 519);

            DrawText(g, "‹  返回流量排行", bodyBoldFont, Color.FromArgb(123, 231, 198), new PointF(24, 596));
        }

        private void DrawSettingCard(Graphics g, Rectangle bounds, string title, string description, string value, string kind)
        {
            using (GraphicsPath path = RoundedRectangle(bounds, 13))
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(78, 9, 39, 42)))
            using (Pen border = new Pen(Color.FromArgb(44, 80, 162, 147)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            DrawText(g, title, bodyBoldFont, Color.FromArgb(206, 234, 225), new PointF(bounds.X + 15, bounds.Y + 13));
            DrawText(g, description, tinyFont, Color.FromArgb(93, 139, 132), new PointF(bounds.X + 15, bounds.Y + 39));
            DrawText(g, value, sectionFont, Color.FromArgb(116, 239, 198), new PointF(bounds.X + 15, bounds.Y + 66));
            DrawStepButton(g, new Rectangle(bounds.Right - 82, bounds.Y + 57, 30, 30), "−");
            DrawStepButton(g, new Rectangle(bounds.Right - 40, bounds.Y + 57, 30, 30), "+");
        }

        private void DrawStepButton(Graphics g, Rectangle bounds, string text)
        {
            using (GraphicsPath path = RoundedRectangle(bounds, 9))
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(105, 18, 67, 61)))
            using (Pen border = new Pen(Color.FromArgb(82, 71, 211, 171)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            Font font = new Font("Bahnschrift", 14f);
            SizeF size = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
            DrawText(g, text, font, Color.FromArgb(173, 248, 220),
                new PointF(bounds.X + (bounds.Width - size.Width) / 2f, bounds.Y + 4));
            font.Dispose();
        }

        private void DrawFooter(Graphics g)
        {
            using (Pen line = new Pen(Color.FromArgb(31, 91, 139, 130))) g.DrawLine(line, 22, 628, 408, 628);
            if (settingsMode)
            {
                DrawText(g, "设置自动保存", tinyFont, Color.FromArgb(90, 139, 131), new PointF(24, 646));
            }
            else
            {
                string down = state == null ? "--" : TrafficIdentity.FormatSpeed(state.DownloadSpeed);
                string up = state == null ? "--" : TrafficIdentity.FormatSpeed(state.UploadSpeed);
                DrawText(g, "↓ " + down, tinyFont, Color.FromArgb(81, 193, 239), new PointF(24, 646));
                DrawText(g, "↑ " + up, tinyFont, Color.FromArgb(241, 177, 83), new PointF(137, 646));
                DrawText(g, "仅代理", tinyFont, Color.FromArgb(88, 143, 133), new PointF(254, 646));
            }
            DrawGear(g, new Point(388, 652));
        }

        private void HandleMouseMove(object sender, MouseEventArgs e)
        {
            bool hand = closeBounds.Contains(e.Location) || settingsBounds.Contains(e.Location);
            if (!settingsMode)
            {
                hand = hand || todayBounds.Contains(e.Location) || weekBounds.Contains(e.Location) ||
                    appsBounds.Contains(e.Location) || domainsBounds.Contains(e.Location);
                foreach (RowHit hit in rowHits) if (hit.Clickable && hit.Bounds.Contains(e.Location)) hand = true;
            }
            else
            {
                hand = hand || new Rectangle(22, 494, 386, 72).Contains(e.Location) ||
                    new Rectangle(22, 582, 180, 34).Contains(e.Location) || IsAnySettingButton(e.Location);
            }
            Cursor = hand ? Cursors.Hand : Cursors.Default;
        }

        private void HandleMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (closeBounds.Contains(e.Location)) { Hide(); return; }
            if (settingsBounds.Contains(e.Location))
            {
                settingsMode = !settingsMode;
                Invalidate();
                return;
            }
            if (settingsMode)
            {
                HandleSettingsClick(e.Location);
                return;
            }
            if (todayBounds.Contains(e.Location)) { period = "today"; RefreshSummary(); return; }
            if (weekBounds.Contains(e.Location)) { period = "week"; RefreshSummary(); return; }
            if (selectedAppKey != null && new Rectangle(22, 291, 386, 34).Contains(e.Location))
            {
                selectedAppKey = null;
                selectedAppLabel = null;
                RefreshSummary();
                return;
            }
            if (selectedAppKey == null && appsBounds.Contains(e.Location)) { dimension = "app"; RefreshSummary(); return; }
            if (selectedAppKey == null && domainsBounds.Contains(e.Location)) { dimension = "domain"; RefreshSummary(); return; }
            foreach (RowHit hit in rowHits)
            {
                if (hit.Clickable && hit.Bounds.Contains(e.Location))
                {
                    selectedAppKey = hit.Key;
                    selectedAppLabel = hit.Label;
                    RefreshSummary();
                    return;
                }
            }
        }

        private bool IsAnySettingButton(Point point)
        {
            foreach (int top in new int[] { 151, 263, 375 })
            {
                if (new Rectangle(326, top + 57, 30, 30).Contains(point) ||
                    new Rectangle(368, top + 57, 30, 30).Contains(point)) return true;
            }
            return false;
        }

        private void HandleSettingsClick(Point point)
        {
            if (new Rectangle(22, 494, 386, 72).Contains(point))
            {
                EventHandler handler = OpenDataRequested;
                if (handler != null) handler(this, EventArgs.Empty);
                return;
            }
            if (new Rectangle(22, 582, 180, 34).Contains(point))
            {
                settingsMode = false;
                Invalidate();
                return;
            }

            MonitorSettings current = monitor.Settings;
            MonitorSettings changed = new MonitorSettings
            {
                BurstMegabytes = current.BurstMegabytes,
                BurstWindowMinutes = current.BurstWindowMinutes,
                DailyGigabytes = current.DailyGigabytes,
                AppCooldownMinutes = current.AppCooldownMinutes
            };
            bool didChange = false;
            didChange |= AdjustInt(point, 151, delegate(int direction) { changed.BurstMegabytes += direction * 50; });
            didChange |= AdjustInt(point, 263, delegate(int direction) { changed.DailyGigabytes += direction * 0.5; });
            didChange |= AdjustInt(point, 375, delegate(int direction) { changed.AppCooldownMinutes += direction * 5; });
            if (didChange)
            {
                changed.Clamp();
                monitor.SaveSettings(changed);
                Invalidate();
            }
        }

        private static bool AdjustInt(Point point, int top, Action<int> adjust)
        {
            if (new Rectangle(326, top + 57, 30, 30).Contains(point)) { adjust(-1); return true; }
            if (new Rectangle(368, top + 57, 30, 30).Contains(point)) { adjust(1); return true; }
            return false;
        }

        private static void DrawRadarMark(Graphics g, Point center)
        {
            Color accent = Color.FromArgb(65, 226, 174);
            using (SolidBrush glow = new SolidBrush(Color.FromArgb(28, accent))) g.FillEllipse(glow, center.X - 25, center.Y - 25, 50, 50);
            using (Pen pen = new Pen(Color.FromArgb(150, accent), 1.4f))
            using (Pen sweep = new Pen(Color.FromArgb(230, accent), 2f))
            {
                g.DrawEllipse(pen, center.X - 15, center.Y - 15, 30, 30);
                g.DrawEllipse(pen, center.X - 8, center.Y - 8, 16, 16);
                g.DrawLine(sweep, center, new Point(center.X + 12, center.Y - 9));
                g.DrawArc(sweep, center.X - 19, center.Y - 19, 38, 38, 292, 48);
            }
            using (SolidBrush dot = new SolidBrush(accent)) g.FillEllipse(dot, center.X + 9, center.Y - 12, 5, 5);
        }

        private static void DrawClose(Graphics g)
        {
            using (Pen pen = new Pen(Color.FromArgb(120, 166, 159), 1.2f))
            {
                g.DrawLine(pen, 395, 28, 405, 38);
                g.DrawLine(pen, 405, 28, 395, 38);
            }
        }

        private static void DrawPulse(Graphics g, Rectangle bounds, Color accent)
        {
            using (Pen pen = new Pen(accent, 1.7f))
            {
                Point[] points = {
                    new Point(bounds.Left, bounds.Top + 12), new Point(bounds.Left + 5, bounds.Top + 12),
                    new Point(bounds.Left + 8, bounds.Top + 5), new Point(bounds.Left + 12, bounds.Bottom - 4),
                    new Point(bounds.Left + 16, bounds.Top + 11), new Point(bounds.Right, bounds.Top + 11)
                };
                g.DrawLines(pen, points);
            }
        }

        private static void DrawAlertBolt(Graphics g, Point center, Color accent)
        {
            Point[] points = {
                new Point(center.X + 1, center.Y - 13), new Point(center.X - 8, center.Y + 1),
                new Point(center.X - 1, center.Y + 1), new Point(center.X - 4, center.Y + 13),
                new Point(center.X + 9, center.Y - 3), new Point(center.X + 2, center.Y - 3)
            };
            using (SolidBrush brush = new SolidBrush(accent)) g.FillPolygon(brush, points);
        }

        private static void DrawRankNumber(Graphics g, int value, Point center)
        {
            using (SolidBrush fill = new SolidBrush(value == 1 ? Color.FromArgb(46, 212, 166) : Color.FromArgb(39, 91, 84)))
                g.FillEllipse(fill, center.X - 11, center.Y - 11, 22, 22);
            Font font = new Font("Bahnschrift SemiBold", 8f);
            string text = value.ToString(CultureInfo.InvariantCulture);
            SizeF size = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
            DrawText(g, text, font, Color.FromArgb(226, 246, 239), new PointF(center.X - size.Width / 2f, center.Y - size.Height / 2f));
            font.Dispose();
        }

        private static void DrawChevron(Graphics g, Point center)
        {
            using (Pen pen = new Pen(Color.FromArgb(93, 183, 159), 1.2f))
            {
                g.DrawLine(pen, center.X - 2, center.Y - 4, center.X + 2, center.Y);
                g.DrawLine(pen, center.X + 2, center.Y, center.X - 2, center.Y + 4);
            }
        }

        private static void DrawDatabaseIcon(Graphics g, Point center)
        {
            using (Pen pen = new Pen(Color.FromArgb(104, 228, 190), 1.5f))
            {
                g.DrawEllipse(pen, center.X - 11, center.Y - 12, 22, 8);
                g.DrawArc(pen, center.X - 11, center.Y - 5, 22, 8, 0, 180);
                g.DrawArc(pen, center.X - 11, center.Y + 2, 22, 8, 0, 180);
                g.DrawLine(pen, center.X - 11, center.Y - 8, center.X - 11, center.Y + 6);
                g.DrawLine(pen, center.X + 11, center.Y - 8, center.X + 11, center.Y + 6);
            }
        }

        private static void DrawGear(Graphics g, Point center)
        {
            using (Pen pen = new Pen(Color.FromArgb(111, 193, 174), 1.6f))
            {
                g.DrawEllipse(pen, center.X - 7, center.Y - 7, 14, 14);
                g.DrawEllipse(pen, center.X - 2, center.Y - 2, 4, 4);
                for (int i = 0; i < 8; i++)
                {
                    double angle = Math.PI * i / 4.0;
                    g.DrawLine(pen,
                        center.X + (int)(8 * Math.Cos(angle)), center.Y + (int)(8 * Math.Sin(angle)),
                        center.X + (int)(11 * Math.Cos(angle)), center.Y + (int)(11 * Math.Sin(angle)));
                }
            }
        }

        private static void DrawText(Graphics g, string text, Font font, Color color, PointF location)
        {
            using (SolidBrush brush = new SolidBrush(color))
                g.DrawString(text ?? string.Empty, font, brush, location, StringFormat.GenericTypographic);
        }

        private static void DrawRight(Graphics g, string text, Font font, Color color, float right, float y)
        {
            SizeF size = g.MeasureString(text ?? string.Empty, font, PointF.Empty, StringFormat.GenericTypographic);
            DrawText(g, text, font, color, new PointF(right - size.Width, y));
        }

        private static string Ellipsize(Graphics g, string text, Font font, float maximumWidth)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width <= maximumWidth) return text;
            string value = text;
            while (value.Length > 1 && g.MeasureString(value + "…", font, PointF.Empty, StringFormat.GenericTypographic).Width > maximumWidth)
                value = value.Substring(0, value.Length - 1);
            return value + "…";
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
                brandFont.Dispose();
                displayFont.Dispose();
                displayUnitFont.Dispose();
                sectionFont.Dispose();
                bodyFont.Dispose();
                bodyBoldFont.Dispose();
                tinyFont.Dispose();
                interactionTimer.Dispose();
                alertTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class RowHit
        {
            public RowHit(Rectangle bounds, string key, string label, bool clickable)
            {
                Bounds = bounds;
                Key = key;
                Label = label;
                Clickable = clickable;
            }
            public Rectangle Bounds { get; private set; }
            public string Key { get; private set; }
            public string Label { get; private set; }
            public bool Clickable { get; private set; }
        }
    }
}
