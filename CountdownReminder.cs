using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

[assembly: AssemblyTitle("提醒")]
[assembly: AssemblyDescription("支持多个任务和悬浮窗的 Windows 倒计时提醒工具")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("提醒")]
[assembly: AssemblyVersion("2.2.4.0")]
[assembly: AssemblyFileVersion("2.2.4.0")]

namespace CountdownReminder
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length == 1 &&
                string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                return RunSelfTest();
            }

            if (args.Length == 1 &&
                string.Equals(args[0], "--tray-test", StringComparison.OrdinalIgnoreCase))
            {
                return RunTrayBehaviorTest();
            }

            bool runUiTest = args.Length == 1 &&
                string.Equals(args[0], "--ui-test", StringComparison.OrdinalIgnoreCase);
            try
            {
                Application.Run(new DarkMainForm(runUiTest));
                return 0;
            }
            catch (Exception exception)
            {
                string logPath = Path.Combine(
                    Path.GetTempPath(),
                    "CountdownReminder-error.log");
                string details =
                    exception.GetType().FullName + Environment.NewLine +
                    exception.Message + Environment.NewLine +
                    (exception.StackTrace ?? string.Empty);
                if (exception.InnerException != null)
                {
                    details += Environment.NewLine + "INNER: " +
                        exception.InnerException.GetType().FullName +
                        Environment.NewLine +
                        exception.InnerException.Message +
                        Environment.NewLine +
                        (exception.InnerException.StackTrace ?? string.Empty);
                }
                File.WriteAllText(logPath, details);
                return 99;
            }
        }

        private static int RunSelfTest()
        {
            if (MainForm.FormatRemaining(TimeSpan.FromSeconds(3661)) != "01:01:01")
            {
                return 1;
            }

            if (MainForm.FormatRemaining(TimeSpan.FromHours(120)) != "120:00:00")
            {
                return 2;
            }

            if (MainForm.FormatRemaining(TimeSpan.FromSeconds(-1)) != "00:00:00")
            {
                return 3;
            }

            if (MainForm.NormalizeReminder("   ") != "时间到")
            {
                return 4;
            }

            using (FloatingCountdownForm floatingForm = new FloatingCountdownForm())
            {
                if (!floatingForm.ReminderTextVisible ||
                    floatingForm.ReminderMenuText != "隐藏提醒内容")
                {
                    return 5;
                }

                floatingForm.SetReminderVisible(false);
                if (floatingForm.ReminderTextVisible ||
                    floatingForm.ReminderMenuText != "显示提醒内容")
                {
                    return 6;
                }

                floatingForm.SetReminderVisible(true);
                if (!floatingForm.ReminderTextVisible ||
                    floatingForm.ReminderMenuText != "隐藏提醒内容")
                {
                    return 7;
                }
            }

            return 0;
        }

        private static int RunTrayBehaviorTest()
        {
            int result = 20;
            using (DarkMainForm form = new DarkMainForm(false))
            {
                form.Shown += delegate
                {
                    form.Close();
                    if (form.Visible ||
                        !form.TrayIconVisible ||
                        !form.TrayMenuReady)
                    {
                        result = 21;
                        form.RequestApplicationExit();
                        return;
                    }

                    form.RestoreFromTrayForTest();
                    if (!form.Visible || form.TrayIconVisible)
                    {
                        result = 22;
                        form.RequestApplicationExit();
                        return;
                    }

                    form.Close();
                    if (form.Visible || !form.TrayIconVisible)
                    {
                        result = 23;
                        form.RequestApplicationExit();
                        return;
                    }

                    result = 0;
                    form.SelectTrayExitForTest();
                };

                Application.Run(form);
            }

            return result;
        }
    }

    internal enum CountdownStatus
    {
        Running,
        Cancelled,
        Completed
    }

    internal sealed class CountdownItem
    {
        internal CountdownItem(decimal minutes, string reminderText)
        {
            Id = Guid.NewGuid();
            ReminderText = reminderText == null ? string.Empty : reminderText.Trim();
            EndTimeUtc = DateTime.UtcNow.AddMinutes((double)minutes);
            Status = CountdownStatus.Running;
        }

        internal Guid Id { get; private set; }
        internal string ReminderText { get; private set; }
        internal DateTime EndTimeUtc { get; private set; }
        internal CountdownStatus Status { get; set; }
        internal DataGridViewRow Row { get; set; }

        internal TimeSpan Remaining
        {
            get
            {
                TimeSpan value = EndTimeUtc - DateTime.UtcNow;
                return value > TimeSpan.Zero ? value : TimeSpan.Zero;
            }
        }
    }

    internal sealed class FloatingCountdownForm : Form
    {
        private const int WmNcLButtonDown = 0x00A1;
        private const int HtCaption = 0x0002;
        private const int CompactHeight = 132;
        private const int ExpandedHeight = 188;

        private readonly Label _hoursLabel;
        private readonly Label _minutesLabel;
        private readonly Label _secondsLabel;
        private readonly Label _reminderLabel;
        private readonly Panel _divider;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _reminderVisibilityItem;
        private CountdownItem _currentItem;
        private bool _allowClose;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(
            IntPtr windowHandle,
            int message,
            IntPtr wordParameter,
            IntPtr longParameter);

        internal FloatingCountdownForm()
        {
            Text = "悬浮倒计时";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(360, CompactHeight);
            BackColor = Color.FromArgb(10, 10, 12);
            Opacity = 0.84D;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.Dpi;

            _hoursLabel = CreateDigitLabel(16, "00");
            _minutesLabel = CreateDigitLabel(132, "00");
            _secondsLabel = CreateDigitLabel(248, "00");

            Controls.Add(_hoursLabel);
            Controls.Add(_minutesLabel);
            Controls.Add(_secondsLabel);
            Controls.Add(CreateSeparatorLabel(111));
            Controls.Add(CreateSeparatorLabel(227));
            Controls.Add(CreateUnitLabel(16, "小时"));
            Controls.Add(CreateUnitLabel(132, "分钟"));
            Controls.Add(CreateUnitLabel(248, "秒钟"));

            _divider = new Panel();
            _divider.BackColor = Color.FromArgb(60, 255, 255, 255);
            _divider.Location = new Point(22, 116);
            _divider.Size = new Size(316, 1);
            _divider.Visible = false;
            Controls.Add(_divider);

            _reminderLabel = new Label();
            _reminderLabel.Text = "时间到";
            _reminderLabel.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular);
            _reminderLabel.ForeColor = Color.FromArgb(225, 255, 255, 255);
            _reminderLabel.BackColor = Color.Transparent;
            _reminderLabel.TextAlign = ContentAlignment.MiddleCenter;
            _reminderLabel.AutoEllipsis = true;
            _reminderLabel.Location = new Point(20, 124);
            _reminderLabel.Size = new Size(320, 48);
            _reminderLabel.Visible = false;
            Controls.Add(_reminderLabel);

            _menu = new ContextMenuStrip();
            _reminderVisibilityItem = new ToolStripMenuItem("显示提醒内容");
            _reminderVisibilityItem.CheckOnClick = true;
            _reminderVisibilityItem.CheckedChanged += delegate
            {
                ApplyReminderVisibility();
            };
            _menu.Items.Add(_reminderVisibilityItem);
            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem hideItem = new ToolStripMenuItem("隐藏悬浮窗");
            hideItem.Click += delegate { Hide(); };
            _menu.Items.Add(hideItem);
            ContextMenuStrip = _menu;

            MakeDraggable(this);
            foreach (Control control in Controls)
            {
                control.ContextMenuStrip = _menu;
                MakeDraggable(control);
            }

            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    Hide();
                }
            };

            SetReminderVisible(true);
        }

        internal CountdownItem CurrentItem
        {
            get { return _currentItem; }
        }

        internal bool ReminderTextVisible
        {
            get { return _reminderVisibilityItem.Checked; }
        }

        internal string ReminderMenuText
        {
            get { return _reminderVisibilityItem.Text; }
        }

        internal void SetReminderVisible(bool visible)
        {
            if (_reminderVisibilityItem.Checked == visible)
            {
                ApplyReminderVisibility();
                return;
            }

            _reminderVisibilityItem.Checked = visible;
        }

        internal void ShowFor(CountdownItem item)
        {
            _currentItem = item;
            UpdateCountdown();

            if (!Visible)
            {
                Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(
                    workArea.Right - Width - 24,
                    workArea.Bottom - Height - 24);
                Show();
            }

            TopMost = true;
        }

        internal void UpdateCountdown()
        {
            TimeSpan remaining = _currentItem == null
                ? TimeSpan.Zero
                : _currentItem.Remaining;

            long totalSeconds = (long)Math.Max(0D, Math.Ceiling(remaining.TotalSeconds));
            long hours = totalSeconds / 3600L;
            long minutes = (totalSeconds % 3600L) / 60L;
            long seconds = totalSeconds % 60L;

            _hoursLabel.Text = hours < 100L
                ? hours.ToString("00")
                : hours.ToString();
            _minutesLabel.Text = minutes.ToString("00");
            _secondsLabel.Text = seconds.ToString("00");
            _reminderLabel.Text = _currentItem == null
                ? "时间到"
                : MainForm.NormalizeReminder(_currentItem.ReminderText);
        }

        internal void ClearItem()
        {
            _currentItem = null;
            Hide();
            UpdateCountdown();
        }

        internal void CloseForApplicationExit()
        {
            _allowClose = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            base.OnFormClosing(e);
        }

        private void ApplyReminderVisibility()
        {
            bool showReminder = _reminderVisibilityItem.Checked;
            int previousBottom = Bottom;

            _reminderVisibilityItem.Text = showReminder
                ? "隐藏提醒内容"
                : "显示提醒内容";
            _divider.Visible = showReminder;
            _reminderLabel.Visible = showReminder;
            ClientSize = new Size(360, showReminder ? ExpandedHeight : CompactHeight);

            if (Visible)
            {
                Top = previousBottom - Height;
                Rectangle workArea = Screen.FromControl(this).WorkingArea;
                if (Top < workArea.Top)
                {
                    Top = workArea.Top;
                }
            }

            UpdateCountdown();
        }

        private static Label CreateDigitLabel(int left, string text)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = new Font("Segoe UI Light", 31F, FontStyle.Regular);
            label.ForeColor = Color.White;
            label.BackColor = Color.Transparent;
            label.TextAlign = ContentAlignment.MiddleCenter;
            label.Location = new Point(left, 18);
            label.Size = new Size(96, 62);
            return label;
        }

        private static Label CreateSeparatorLabel(int left)
        {
            Label label = new Label();
            label.Text = ":";
            label.Font = new Font("Segoe UI Light", 27F, FontStyle.Regular);
            label.ForeColor = Color.FromArgb(150, 255, 255, 255);
            label.BackColor = Color.Transparent;
            label.TextAlign = ContentAlignment.MiddleCenter;
            label.Location = new Point(left, 22);
            label.Size = new Size(20, 52);
            return label;
        }

        private static Label CreateUnitLabel(int left, string text)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Regular);
            label.ForeColor = Color.FromArgb(165, 255, 255, 255);
            label.BackColor = Color.Transparent;
            label.TextAlign = ContentAlignment.MiddleCenter;
            label.Location = new Point(left, 81);
            label.Size = new Size(96, 24);
            return label;
        }

        private void MakeDraggable(Control control)
        {
            control.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left)
                {
                    return;
                }

                ReleaseCapture();
                SendMessage(Handle, WmNcLButtonDown, new IntPtr(HtCaption), IntPtr.Zero);
            };
        }
    }

    internal sealed class ReminderDialog : Form
    {
        internal ReminderDialog(string reminderText)
        {
            string content = MainForm.NormalizeReminder(reminderText);
            Text = "提醒";
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            TopMost = true;
            ClientSize = new Size(400, 230);
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular);
            AutoScaleMode = AutoScaleMode.Dpi;

            Label titleLabel = new Label();
            titleLabel.Text = string.Equals(content, "时间到", StringComparison.Ordinal)
                ? "时间到！"
                : "提醒";
            titleLabel.Font = new Font(Font.FontFamily, 22F, FontStyle.Bold);
            titleLabel.ForeColor = Color.FromArgb(22, 119, 255);
            titleLabel.TextAlign = ContentAlignment.MiddleCenter;
            titleLabel.Location = new Point(25, 24);
            titleLabel.Size = new Size(350, 48);
            Controls.Add(titleLabel);

            Label contentLabel = new Label();
            contentLabel.Text = content;
            contentLabel.ForeColor = Color.FromArgb(55, 65, 81);
            contentLabel.TextAlign = ContentAlignment.MiddleCenter;
            contentLabel.AutoEllipsis = true;
            contentLabel.Location = new Point(30, 77);
            contentLabel.Size = new Size(340, 58);
            Controls.Add(contentLabel);

            Button confirmButton = new Button();
            confirmButton.Text = "知道了";
            confirmButton.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            confirmButton.ForeColor = Color.White;
            confirmButton.BackColor = Color.FromArgb(22, 119, 255);
            confirmButton.FlatStyle = FlatStyle.Flat;
            confirmButton.FlatAppearance.BorderSize = 0;
            confirmButton.Location = new Point(100, 158);
            confirmButton.Size = new Size(200, 44);
            confirmButton.Click += delegate { Close(); };
            Controls.Add(confirmButton);

            AcceptButton = confirmButton;
            Shown += delegate { Activate(); };
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly NumericUpDown _minutesInput;
        private readonly TextBox _reminderInput;
        private readonly Button _addButton;
        private readonly DataGridView _grid;
        private readonly Button _floatButton;
        private readonly Button _cancelButton;
        private readonly Button _deleteButton;
        private readonly Label _countLabel;
        private readonly Timer _uiTimer;
        private readonly List<CountdownItem> _items;
        private readonly List<ReminderDialog> _reminderDialogs;
        private readonly FloatingCountdownForm _floatingForm;

        private bool _closingConfirmed;
        private int _reminderOffset;

        internal MainForm(bool runUiTest)
        {
            _items = new List<CountdownItem>();
            _reminderDialogs = new List<ReminderDialog>();
            _floatingForm = new FloatingCountdownForm();

            Text = "提醒";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(790, 610);
            ClientSize = new Size(790, 610);
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular);
            AutoScaleMode = AutoScaleMode.Dpi;

            Label titleLabel = new Label();
            titleLabel.Text = "提醒";
            titleLabel.Font = new Font(Font.FontFamily, 21F, FontStyle.Bold);
            titleLabel.ForeColor = Color.FromArgb(31, 41, 55);
            titleLabel.AutoSize = true;
            titleLabel.Location = new Point(28, 20);
            Controls.Add(titleLabel);

            Label subtitleLabel = new Label();
            subtitleLabel.Text = "可同时创建多个提醒，并选择一个在桌面悬浮显示";
            subtitleLabel.ForeColor = Color.FromArgb(107, 114, 128);
            subtitleLabel.AutoSize = true;
            subtitleLabel.Location = new Point(30, 63);
            Controls.Add(subtitleLabel);

            Panel inputPanel = new Panel();
            inputPanel.BackColor = Color.FromArgb(247, 249, 252);
            inputPanel.Location = new Point(28, 96);
            inputPanel.Size = new Size(734, 112);
            inputPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(inputPanel);

            Label minutesLabel = new Label();
            minutesLabel.Text = "分钟";
            minutesLabel.ForeColor = Color.FromArgb(55, 65, 81);
            minutesLabel.AutoSize = true;
            minutesLabel.Location = new Point(18, 14);
            inputPanel.Controls.Add(minutesLabel);

            _minutesInput = new NumericUpDown();
            _minutesInput.DecimalPlaces = 1;
            _minutesInput.Increment = 0.5M;
            _minutesInput.Minimum = 0.1M;
            _minutesInput.Maximum = 43200M;
            _minutesInput.Value = 5M;
            _minutesInput.TextAlign = HorizontalAlignment.Right;
            _minutesInput.Font = new Font(Font.FontFamily, 12F, FontStyle.Regular);
            _minutesInput.Location = new Point(18, 46);
            _minutesInput.Size = new Size(145, 34);
            inputPanel.Controls.Add(_minutesInput);

            Label reminderLabel = new Label();
            reminderLabel.Text = "提醒内容（可选）";
            reminderLabel.ForeColor = Color.FromArgb(55, 65, 81);
            reminderLabel.AutoSize = true;
            reminderLabel.Location = new Point(188, 14);
            inputPanel.Controls.Add(reminderLabel);

            _reminderInput = new TextBox();
            _reminderInput.Font = new Font(Font.FontFamily, 11F, FontStyle.Regular);
            _reminderInput.MaxLength = 200;
            _reminderInput.Location = new Point(188, 46);
            _reminderInput.Size = new Size(335, 32);
            _reminderInput.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _reminderInput.KeyDown += Input_KeyDown;
            inputPanel.Controls.Add(_reminderInput);

            _addButton = CreatePrimaryButton("添加倒计时");
            _addButton.Location = new Point(548, 42);
            _addButton.Size = new Size(164, 42);
            _addButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _addButton.Click += delegate { AddFromInputs(); };
            inputPanel.Controls.Add(_addButton);

            Label listTitleLabel = new Label();
            listTitleLabel.Text = "倒计时列表";
            listTitleLabel.Font = new Font(Font.FontFamily, 12F, FontStyle.Bold);
            listTitleLabel.ForeColor = Color.FromArgb(31, 41, 55);
            listTitleLabel.AutoSize = true;
            listTitleLabel.Location = new Point(28, 226);
            Controls.Add(listTitleLabel);

            _countLabel = new Label();
            _countLabel.Text = "0 个进行中";
            _countLabel.ForeColor = Color.FromArgb(107, 114, 128);
            _countLabel.TextAlign = ContentAlignment.MiddleRight;
            _countLabel.Location = new Point(610, 226);
            _countLabel.Size = new Size(152, 25);
            _countLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(_countLabel);

            _grid = new DataGridView();
            _grid.Location = new Point(28, 258);
            _grid.Size = new Size(734, 250);
            _grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _grid.BackgroundColor = Color.White;
            _grid.BorderStyle = BorderStyle.FixedSingle;
            _grid.ReadOnly = true;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.RowHeadersVisible = false;
            _grid.MultiSelect = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.AutoGenerateColumns = false;
            _grid.ColumnHeadersHeight = 38;
            _grid.RowTemplate.Height = 42;
            _grid.EnableHeadersVisualStyles = false;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(247, 249, 252);
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(55, 65, 81);
            _grid.ColumnHeadersDefaultCellStyle.Font =
                new Font(Font.FontFamily, 9.5F, FontStyle.Bold);
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(230, 242, 255);
            _grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(31, 41, 55);
            _grid.DefaultCellStyle.ForeColor = Color.FromArgb(55, 65, 81);
            _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            _grid.GridColor = Color.FromArgb(235, 238, 242);

            DataGridViewTextBoxColumn contentColumn = new DataGridViewTextBoxColumn();
            contentColumn.HeaderText = "提醒内容";
            contentColumn.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            contentColumn.MinimumWidth = 260;
            _grid.Columns.Add(contentColumn);

            DataGridViewTextBoxColumn remainingColumn = new DataGridViewTextBoxColumn();
            remainingColumn.HeaderText = "剩余时间";
            remainingColumn.Width = 150;
            remainingColumn.DefaultCellStyle.Font =
                new Font("Consolas", 11F, FontStyle.Bold);
            remainingColumn.DefaultCellStyle.Alignment =
                DataGridViewContentAlignment.MiddleCenter;
            _grid.Columns.Add(remainingColumn);

            DataGridViewTextBoxColumn statusColumn = new DataGridViewTextBoxColumn();
            statusColumn.HeaderText = "状态";
            statusColumn.Width = 135;
            statusColumn.DefaultCellStyle.Alignment =
                DataGridViewContentAlignment.MiddleCenter;
            _grid.Columns.Add(statusColumn);

            _grid.SelectionChanged += delegate { UpdateActionButtons(); };
            _grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex >= 0)
                {
                    ToggleFloatingForSelected();
                }
            };
            Controls.Add(_grid);

            _floatButton = CreateSecondaryButton("悬浮显示");
            _floatButton.Location = new Point(28, 526);
            _floatButton.Size = new Size(140, 42);
            _floatButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _floatButton.Click += delegate { ToggleFloatingForSelected(); };
            Controls.Add(_floatButton);

            _cancelButton = CreateSecondaryButton("取消倒计时");
            _cancelButton.Location = new Point(180, 526);
            _cancelButton.Size = new Size(140, 42);
            _cancelButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _cancelButton.Click += delegate { CancelSelected(); };
            Controls.Add(_cancelButton);

            _deleteButton = CreateSecondaryButton("删除");
            _deleteButton.Location = new Point(332, 526);
            _deleteButton.Size = new Size(100, 42);
            _deleteButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _deleteButton.Click += delegate { DeleteSelected(); };
            Controls.Add(_deleteButton);

            Label hintLabel = new Label();
            hintLabel.Text = "双击可悬浮；悬浮窗可拖动，右键或按 Esc 隐藏";
            hintLabel.ForeColor = Color.FromArgb(107, 114, 128);
            hintLabel.TextAlign = ContentAlignment.MiddleRight;
            hintLabel.Location = new Point(447, 530);
            hintLabel.Size = new Size(315, 34);
            hintLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            Controls.Add(hintLabel);

            _floatingForm.VisibleChanged += delegate
            {
                RefreshAllRows();
                UpdateActionButtons();
            };

            _uiTimer = new Timer();
            _uiTimer.Interval = 250;
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();

            FormClosing += MainForm_FormClosing;
            FormClosed += delegate
            {
                _uiTimer.Stop();
                if (!_floatingForm.IsDisposed)
                {
                    _floatingForm.CloseForApplicationExit();
                }
            };

            UpdateActionButtons();

            Shown += delegate
            {
                if (runUiTest)
                {
                    CreateCountdown(0.05M, "喝水提醒");
                    CountdownItem floatingItem = CreateCountdown(0.1M, "提交日报");
                    SelectItem(floatingItem);
                    _floatingForm.ShowFor(floatingItem);
                    RefreshAllRows();
                }
                else
                {
                    _minutesInput.Focus();
                }
            };
        }

        internal static string NormalizeReminder(string reminderText)
        {
            string value = reminderText == null ? string.Empty : reminderText.Trim();
            return value.Length == 0 ? "时间到" : value;
        }

        internal static string FormatRemaining(TimeSpan remaining)
        {
            long totalSeconds = (long)Math.Max(0D, Math.Ceiling(remaining.TotalSeconds));
            long hours = totalSeconds / 3600L;
            long minutes = (totalSeconds % 3600L) / 60L;
            long seconds = totalSeconds % 60L;
            string hoursText = hours < 100L ? hours.ToString("00") : hours.ToString();
            return string.Format("{0}:{1:00}:{2:00}", hoursText, minutes, seconds);
        }

        private static Button CreatePrimaryButton(string text)
        {
            Button button = new Button();
            button.Text = text;
            button.Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
            button.ForeColor = Color.White;
            button.BackColor = Color.FromArgb(22, 119, 255);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.Cursor = Cursors.Hand;
            return button;
        }

        private static Button CreateSecondaryButton(string text)
        {
            Button button = new Button();
            button.Text = text;
            button.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular);
            button.ForeColor = Color.FromArgb(55, 65, 81);
            button.BackColor = Color.White;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(209, 213, 219);
            button.FlatAppearance.BorderSize = 1;
            button.Cursor = Cursors.Hand;
            return button;
        }

        private void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                AddFromInputs();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void AddFromInputs()
        {
            CountdownItem item = CreateCountdown(_minutesInput.Value, _reminderInput.Text);
            _reminderInput.Clear();
            SelectItem(item);
            _reminderInput.Focus();
        }

        private CountdownItem CreateCountdown(decimal minutes, string reminderText)
        {
            CountdownItem item = new CountdownItem(minutes, reminderText);
            DataGridViewRow row = new DataGridViewRow();
            row.CreateCells(
                _grid,
                NormalizeReminder(item.ReminderText),
                FormatRemaining(item.Remaining),
                "进行中");
            row.Tag = item;
            item.Row = row;
            _items.Add(item);
            _grid.Rows.Add(row);
            UpdateCountLabel();
            return item;
        }

        private void SelectItem(CountdownItem item)
        {
            _grid.ClearSelection();
            if (item != null && item.Row != null)
            {
                item.Row.Selected = true;
                _grid.CurrentCell = item.Row.Cells[0];
            }
        }

        private CountdownItem GetSelectedItem()
        {
            if (_grid.SelectedRows.Count == 0)
            {
                return null;
            }

            return _grid.SelectedRows[0].Tag as CountdownItem;
        }

        private void UiTimer_Tick(object sender, EventArgs e)
        {
            List<CountdownItem> completedItems = new List<CountdownItem>();
            foreach (CountdownItem item in _items)
            {
                if (item.Status == CountdownStatus.Running &&
                    item.Remaining <= TimeSpan.Zero)
                {
                    item.Status = CountdownStatus.Completed;
                    completedItems.Add(item);
                }

                UpdateRow(item);
            }

            if (_floatingForm.CurrentItem != null)
            {
                _floatingForm.UpdateCountdown();
            }

            foreach (CountdownItem item in completedItems)
            {
                ShowReminder(item);
            }

            if (completedItems.Count > 0)
            {
                UpdateCountLabel();
                UpdateActionButtons();
            }
        }

        private void ShowReminder(CountdownItem item)
        {
            PlayReminderSound();

            ReminderDialog dialog = new ReminderDialog(item.ReminderText);
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            int offset = (_reminderOffset % 5) * 24;
            dialog.Location = new Point(
                area.Left + ((area.Width - dialog.Width) / 2) + offset,
                area.Top + ((area.Height - dialog.Height) / 2) + offset);
            _reminderOffset++;
            _reminderDialogs.Add(dialog);
            dialog.FormClosed += delegate
            {
                _reminderDialogs.Remove(dialog);
                dialog.Dispose();
            };
            dialog.Show();
            dialog.Activate();
        }

        private static void PlayReminderSound()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    SystemSounds.Exclamation.Play();
                }
                catch
                {
                    // 音频设备异常时，视觉提醒仍然正常显示。
                }
            });
        }

        private void ToggleFloatingForSelected()
        {
            CountdownItem item = GetSelectedItem();
            if (item == null || item.Status != CountdownStatus.Running)
            {
                return;
            }

            if (ReferenceEquals(_floatingForm.CurrentItem, item) &&
                _floatingForm.Visible)
            {
                _floatingForm.Hide();
            }
            else
            {
                _floatingForm.ShowFor(item);
            }

            RefreshAllRows();
            UpdateActionButtons();
        }

        private void CancelSelected()
        {
            CountdownItem item = GetSelectedItem();
            if (item == null || item.Status != CountdownStatus.Running)
            {
                return;
            }

            item.Status = CountdownStatus.Cancelled;
            if (ReferenceEquals(_floatingForm.CurrentItem, item))
            {
                _floatingForm.ClearItem();
            }

            UpdateRow(item);
            UpdateCountLabel();
            UpdateActionButtons();
        }

        private void DeleteSelected()
        {
            CountdownItem item = GetSelectedItem();
            if (item == null)
            {
                return;
            }

            if (ReferenceEquals(_floatingForm.CurrentItem, item))
            {
                _floatingForm.ClearItem();
            }

            _items.Remove(item);
            _grid.Rows.Remove(item.Row);
            UpdateCountLabel();
            UpdateActionButtons();
        }

        private void UpdateRow(CountdownItem item)
        {
            if (item.Row == null || item.Row.DataGridView == null)
            {
                return;
            }

            item.Row.Cells[0].Value = NormalizeReminder(item.ReminderText);
            item.Row.Cells[1].Value = FormatRemaining(item.Remaining);

            if (item.Status == CountdownStatus.Completed)
            {
                item.Row.Cells[2].Value = "已结束";
                item.Row.DefaultCellStyle.ForeColor = Color.FromArgb(107, 114, 128);
            }
            else if (item.Status == CountdownStatus.Cancelled)
            {
                item.Row.Cells[2].Value = "已取消";
                item.Row.DefaultCellStyle.ForeColor = Color.FromArgb(156, 163, 175);
            }
            else if (ReferenceEquals(_floatingForm.CurrentItem, item) &&
                _floatingForm.Visible)
            {
                item.Row.Cells[2].Value = "进行中 · 悬浮";
                item.Row.DefaultCellStyle.ForeColor = Color.FromArgb(22, 119, 255);
            }
            else
            {
                item.Row.Cells[2].Value = "进行中";
                item.Row.DefaultCellStyle.ForeColor = Color.FromArgb(55, 65, 81);
            }
        }

        private void RefreshAllRows()
        {
            foreach (CountdownItem item in _items)
            {
                UpdateRow(item);
            }
        }

        private void UpdateCountLabel()
        {
            int runningCount = 0;
            foreach (CountdownItem item in _items)
            {
                if (item.Status == CountdownStatus.Running)
                {
                    runningCount++;
                }
            }

            _countLabel.Text = runningCount + " 个进行中";
        }

        private void UpdateActionButtons()
        {
            CountdownItem item = GetSelectedItem();
            bool hasSelection = item != null;
            bool isRunning = hasSelection && item.Status == CountdownStatus.Running;

            _floatButton.Enabled = isRunning;
            _cancelButton.Enabled = isRunning;
            _deleteButton.Enabled = hasSelection;

            _floatButton.Text = isRunning &&
                ReferenceEquals(_floatingForm.CurrentItem, item) &&
                _floatingForm.Visible
                ? "隐藏悬浮窗"
                : "悬浮显示";
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_closingConfirmed)
            {
                return;
            }

            int runningCount = 0;
            foreach (CountdownItem item in _items)
            {
                if (item.Status == CountdownStatus.Running)
                {
                    runningCount++;
                }
            }

            if (runningCount == 0)
            {
                _closingConfirmed = true;
                return;
            }

            DialogResult result = MessageBox.Show(
                this,
                "还有 " + runningCount + " 个倒计时正在进行，确定退出吗？",
                "退出确认",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);

            if (result == DialogResult.Yes)
            {
                _closingConfirmed = true;
            }
            else
            {
                e.Cancel = true;
            }
        }
    }

    internal static class DarkUi
    {
        internal static readonly Color Background = Color.FromArgb(9, 13, 18);
        internal static readonly Color TitleBar = Color.FromArgb(7, 10, 14);
        internal static readonly Color Card = Color.FromArgb(14, 19, 25);
        internal static readonly Color CardHover = Color.FromArgb(20, 27, 36);
        internal static readonly Color Input = Color.FromArgb(10, 15, 21);
        internal static readonly Color Border = Color.FromArgb(45, 53, 64);
        internal static readonly Color BorderSoft = Color.FromArgb(34, 41, 50);
        internal static readonly Color Text = Color.FromArgb(238, 241, 246);
        internal static readonly Color TextMuted = Color.FromArgb(135, 143, 156);
        internal static readonly Color TextDim = Color.FromArgb(92, 101, 114);
        internal static readonly Color Blue = Color.FromArgb(42, 116, 255);
        internal static readonly Color BlueHover = Color.FromArgb(63, 133, 255);
        internal static readonly Color Red = Color.FromArgb(239, 82, 82);

        internal static GraphicsPath RoundedPath(Rectangle rectangle, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = Math.Max(2, radius * 2);
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
    }

    internal static class DarkUiNative
    {
        internal const int WmNcLButtonDown = 0x00A1;
        internal const int HtCaption = 0x0002;
        internal const int EmSetCueBanner = 0x1501;

        [DllImport("user32.dll")]
        internal static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(
            IntPtr windowHandle,
            int message,
            IntPtr wordParameter,
            IntPtr longParameter);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr SendMessage(
            IntPtr windowHandle,
            int message,
            IntPtr wordParameter,
            string longParameter);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr windowHandle,
            int attribute,
            ref int value,
            int valueSize);

        internal static void EnableRoundedCorners(Form form)
        {
            try
            {
                int preference = 2;
                DwmSetWindowAttribute(
                    form.Handle,
                    33,
                    ref preference,
                    Marshal.SizeOf(typeof(int)));
            }
            catch
            {
                // 旧版 Windows 不支持 DWM 圆角时，窗口仍可正常使用。
            }
        }

        internal static void BeginWindowDrag(Form form)
        {
            ReleaseCapture();
            SendMessage(
                form.Handle,
                WmNcLButtonDown,
                new IntPtr(HtCaption),
                IntPtr.Zero);
        }
    }

    internal sealed class RoundedPanel : Panel
    {
        internal RoundedPanel()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint |
                ControlStyles.SupportsTransparentBackColor,
                true);
            BackColor = Color.Transparent;
            FillColor = DarkUi.Card;
            BorderColor = DarkUi.Border;
            BorderWidth = 1F;
            CornerRadius = 14;
        }

        internal Color FillColor { get; set; }
        internal Color BorderColor { get; set; }
        internal float BorderWidth { get; set; }
        internal int CornerRadius { get; set; }

        protected override void OnResize(EventArgs eventArgs)
        {
            base.OnResize(eventArgs);
            if (Width <= 0 || Height <= 0)
            {
                return;
            }

            using (GraphicsPath path = DarkUi.RoundedPath(
                new Rectangle(0, 0, Width, Height),
                CornerRadius))
            {
                Region = new Region(path);
            }
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rectangle = new Rectangle(
                1,
                1,
                Math.Max(1, Width - 3),
                Math.Max(1, Height - 3));

            using (GraphicsPath path = DarkUi.RoundedPath(rectangle, CornerRadius))
            using (SolidBrush brush = new SolidBrush(FillColor))
            using (Pen pen = new Pen(BorderColor, BorderWidth))
            {
                eventArgs.Graphics.FillPath(brush, path);
                eventArgs.Graphics.DrawPath(pen, path);
            }

            base.OnPaint(eventArgs);
        }
    }

    internal sealed class RoundedButton : Button
    {
        private bool _hovered;
        private bool _pressed;

        internal RoundedButton()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint |
                ControlStyles.SupportsTransparentBackColor,
                true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            CornerRadius = 10;
            FillColor = DarkUi.Card;
            HoverColor = DarkUi.CardHover;
            PressedColor = Color.FromArgb(27, 35, 46);
            BorderColor = DarkUi.Border;
            TextColor = DarkUi.Text;
            DisabledFillColor = Color.FromArgb(12, 16, 21);
            DisabledTextColor = DarkUi.TextDim;
            BorderWidth = 1F;
        }

        internal Color FillColor { get; set; }
        internal Color HoverColor { get; set; }
        internal Color PressedColor { get; set; }
        internal Color BorderColor { get; set; }
        internal Color TextColor { get; set; }
        internal Color DisabledFillColor { get; set; }
        internal Color DisabledTextColor { get; set; }
        internal float BorderWidth { get; set; }
        internal int CornerRadius { get; set; }

        protected override void OnMouseEnter(EventArgs eventArgs)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(eventArgs);
        }

        protected override void OnMouseLeave(EventArgs eventArgs)
        {
            _hovered = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(eventArgs);
        }

        protected override void OnMouseDown(MouseEventArgs eventArgs)
        {
            _pressed = true;
            Invalidate();
            base.OnMouseDown(eventArgs);
        }

        protected override void OnMouseUp(MouseEventArgs eventArgs)
        {
            _pressed = false;
            Invalidate();
            base.OnMouseUp(eventArgs);
        }

        protected override void OnEnabledChanged(EventArgs eventArgs)
        {
            Invalidate();
            base.OnEnabledChanged(eventArgs);
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            RoundedPanel roundedParent = Parent as RoundedPanel;
            eventArgs.Graphics.Clear(
                roundedParent == null
                    ? DarkUi.Background
                    : roundedParent.FillColor);
            Rectangle rectangle = new Rectangle(
                1,
                1,
                Math.Max(1, Width - 3),
                Math.Max(1, Height - 3));

            Color fill = !Enabled
                ? DisabledFillColor
                : _pressed
                    ? PressedColor
                    : _hovered
                        ? HoverColor
                        : FillColor;
            Color textColor = Enabled ? TextColor : DisabledTextColor;

            using (GraphicsPath path = DarkUi.RoundedPath(rectangle, CornerRadius))
            using (SolidBrush fillBrush = new SolidBrush(fill))
            using (Pen pen = new Pen(
                Enabled ? BorderColor : DarkUi.BorderSoft,
                BorderWidth))
            using (SolidBrush textBrush = new SolidBrush(textColor))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                eventArgs.Graphics.FillPath(fillBrush, path);
                if (BorderWidth > 0F)
                {
                    eventArgs.Graphics.DrawPath(pen, path);
                }
                eventArgs.Graphics.DrawString(Text, Font, textBrush, rectangle, format);
            }
        }
    }

    internal sealed class TitleBarButton : Control
    {
        private bool _hovered;
        private bool _pressed;

        internal TitleBarButton(string glyph, bool isClose)
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint |
                ControlStyles.SupportsTransparentBackColor,
                true);
            Glyph = glyph;
            IsClose = isClose;
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI", 14F, FontStyle.Regular);
        }

        internal string Glyph { get; set; }
        internal bool IsClose { get; private set; }

        protected override void OnMouseEnter(EventArgs eventArgs)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(eventArgs);
        }

        protected override void OnMouseLeave(EventArgs eventArgs)
        {
            _hovered = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(eventArgs);
        }

        protected override void OnMouseDown(MouseEventArgs eventArgs)
        {
            _pressed = true;
            Invalidate();
            base.OnMouseDown(eventArgs);
        }

        protected override void OnMouseUp(MouseEventArgs eventArgs)
        {
            _pressed = false;
            Invalidate();
            base.OnMouseUp(eventArgs);
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            Color background = Color.Transparent;
            if (_pressed)
            {
                background = IsClose
                    ? Color.FromArgb(182, 42, 42)
                    : Color.FromArgb(37, 44, 53);
            }
            else if (_hovered)
            {
                background = IsClose
                    ? Color.FromArgb(200, 53, 53)
                    : Color.FromArgb(29, 35, 43);
            }

            eventArgs.Graphics.Clear(background == Color.Transparent
                ? DarkUi.TitleBar
                : background);

            TextRenderer.DrawText(
                eventArgs.Graphics,
                Glyph,
                Font,
                ClientRectangle,
                DarkUi.Text,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding);
        }
    }

    internal sealed class AppIconControl : Control
    {
        internal AppIconControl()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint,
                true);
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rectangle = new Rectangle(1, 1, Width - 3, Height - 3);
            using (GraphicsPath background = DarkUi.RoundedPath(rectangle, 6))
            using (SolidBrush blueBrush = new SolidBrush(DarkUi.Blue))
            using (Pen whitePen = new Pen(Color.White, 1.8F))
            {
                eventArgs.Graphics.FillPath(blueBrush, background);
                int left = 8;
                int right = Width - 8;
                int top = 7;
                int bottom = Height - 7;
                eventArgs.Graphics.DrawLine(whitePen, left, top, right, top);
                eventArgs.Graphics.DrawLine(whitePen, left, bottom, right, bottom);
                eventArgs.Graphics.DrawLine(whitePen, left + 2, top + 1, right - 2, bottom - 1);
                eventArgs.Graphics.DrawLine(whitePen, right - 2, top + 1, left + 2, bottom - 1);
            }
        }
    }

    internal sealed class ArrowButton : Control
    {
        private readonly bool _pointsUp;
        private bool _hovered;

        internal ArrowButton(bool pointsUp)
        {
            _pointsUp = pointsUp;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint,
                true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs eventArgs)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(eventArgs);
        }

        protected override void OnMouseLeave(EventArgs eventArgs)
        {
            _hovered = false;
            Invalidate();
            base.OnMouseLeave(eventArgs);
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.Clear(_hovered ? DarkUi.CardHover : DarkUi.Input);
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int centerX = Width / 2;
            int centerY = Height / 2;
            Point[] points = _pointsUp
                ? new[]
                {
                    new Point(centerX - 5, centerY + 3),
                    new Point(centerX, centerY - 3),
                    new Point(centerX + 5, centerY + 3)
                }
                : new[]
                {
                    new Point(centerX - 5, centerY - 3),
                    new Point(centerX, centerY + 3),
                    new Point(centerX + 5, centerY - 3)
                };
            using (Pen pen = new Pen(DarkUi.TextMuted, 1.8F))
            {
                eventArgs.Graphics.DrawLines(pen, points);
            }
        }
    }

    internal sealed class MinutesInputControl : Control
    {
        private readonly TextBox _textBox;
        private decimal _value;

        internal MinutesInputControl()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint |
                ControlStyles.SupportsTransparentBackColor,
                true);
            BackColor = Color.Transparent;
            Minimum = 0.1M;
            Maximum = 43200M;

            _textBox = new TextBox();
            _textBox.BorderStyle = BorderStyle.None;
            _textBox.BackColor = DarkUi.Input;
            _textBox.ForeColor = DarkUi.Text;
            _textBox.Font = new Font("Segoe UI", 14F, FontStyle.Regular);
            _textBox.Text = "5.0";
            _textBox.TextAlign = HorizontalAlignment.Left;
            _textBox.KeyDown += delegate(object sender, KeyEventArgs eventArgs)
            {
                if (eventArgs.KeyCode == Keys.Enter)
                {
                    CommitValue();
                    if (EnterPressed != null)
                    {
                        EnterPressed(this, EventArgs.Empty);
                    }
                    eventArgs.Handled = true;
                    eventArgs.SuppressKeyPress = true;
                }
            };
            _textBox.Leave += delegate { CommitValue(); };
            Controls.Add(_textBox);

            Value = 5M;
        }

        internal event EventHandler EnterPressed;

        internal decimal Minimum { get; set; }
        internal decimal Maximum { get; set; }

        internal decimal Value
        {
            get
            {
                CommitValue();
                return _value;
            }
            set
            {
                _value = Math.Max(Minimum, Math.Min(Maximum, value));
                _textBox.Text = _value.ToString("0.0");
            }
        }

        internal void FocusInput()
        {
            _textBox.Focus();
            _textBox.SelectionStart = _textBox.TextLength;
            _textBox.SelectionLength = 0;
        }

        protected override void OnResize(EventArgs eventArgs)
        {
            base.OnResize(eventArgs);
            _textBox.Location = new Point(16, Math.Max(8, (Height - 28) / 2));
            _textBox.Size = new Size(Math.Max(20, Width - 32), 30);

            using (GraphicsPath path = DarkUi.RoundedPath(
                new Rectangle(0, 0, Width, Height),
                10))
            {
                Region = new Region(path);
            }
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rectangle = new Rectangle(1, 1, Width - 3, Height - 3);
            using (GraphicsPath path = DarkUi.RoundedPath(rectangle, 10))
            using (SolidBrush brush = new SolidBrush(DarkUi.Input))
            using (Pen pen = new Pen(DarkUi.Border, 1F))
            {
                eventArgs.Graphics.FillPath(brush, path);
                eventArgs.Graphics.DrawPath(pen, path);
            }
        }

        private void CommitValue()
        {
            decimal parsed;
            if (!decimal.TryParse(_textBox.Text.Trim(), out parsed))
            {
                parsed = _value <= 0M ? 5M : _value;
            }

            _value = Math.Max(Minimum, Math.Min(Maximum, parsed));
            _textBox.Text = _value.ToString("0.0");
        }
    }

    internal sealed class DarkTextInput : Control
    {
        private readonly TextBox _textBox;
        private string _placeholder;

        internal DarkTextInput()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint |
                ControlStyles.SupportsTransparentBackColor,
                true);
            BackColor = Color.Transparent;

            _textBox = new TextBox();
            _textBox.BorderStyle = BorderStyle.None;
            _textBox.BackColor = DarkUi.Input;
            _textBox.ForeColor = DarkUi.Text;
            _textBox.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Regular);
            _textBox.MaxLength = 200;
            _textBox.HandleCreated += delegate { ApplyPlaceholder(); };
            _textBox.KeyDown += delegate(object sender, KeyEventArgs eventArgs)
            {
                if (eventArgs.KeyCode == Keys.Enter)
                {
                    if (EnterPressed != null)
                    {
                        EnterPressed(this, EventArgs.Empty);
                    }
                    eventArgs.Handled = true;
                    eventArgs.SuppressKeyPress = true;
                }
            };
            Controls.Add(_textBox);
        }

        internal event EventHandler EnterPressed;

        public override string Text
        {
            get { return _textBox == null ? base.Text : _textBox.Text; }
            set
            {
                if (_textBox == null)
                {
                    base.Text = value;
                }
                else
                {
                    _textBox.Text = value;
                }
            }
        }

        internal string Placeholder
        {
            get { return _placeholder; }
            set
            {
                _placeholder = value;
                ApplyPlaceholder();
            }
        }

        internal void ClearInput()
        {
            _textBox.Clear();
        }

        internal void FocusInput()
        {
            _textBox.Focus();
        }

        protected override void OnHandleCreated(EventArgs eventArgs)
        {
            base.OnHandleCreated(eventArgs);
            ApplyPlaceholder();
        }

        protected override void OnResize(EventArgs eventArgs)
        {
            base.OnResize(eventArgs);
            _textBox.Location = new Point(16, Math.Max(8, (Height - 27) / 2));
            _textBox.Size = new Size(Math.Max(20, Width - 32), 28);

            using (GraphicsPath path = DarkUi.RoundedPath(
                new Rectangle(0, 0, Width, Height),
                10))
            {
                Region = new Region(path);
            }
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rectangle = new Rectangle(1, 1, Width - 3, Height - 3);
            using (GraphicsPath path = DarkUi.RoundedPath(rectangle, 10))
            using (SolidBrush brush = new SolidBrush(DarkUi.Input))
            using (Pen pen = new Pen(DarkUi.Border, 1F))
            {
                eventArgs.Graphics.FillPath(brush, path);
                eventArgs.Graphics.DrawPath(pen, path);
            }
        }

        private void ApplyPlaceholder()
        {
            if (_textBox.IsHandleCreated && _placeholder != null)
            {
                DarkUiNative.SendMessage(
                    _textBox.Handle,
                    DarkUiNative.EmSetCueBanner,
                    new IntPtr(1),
                    _placeholder);
            }
        }
    }

    internal sealed class EmptyStateControl : Control
    {
        internal EmptyStateControl()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
            BackColor = DarkUi.Card;
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.Clear(DarkUi.Card);
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int centerX = Width / 2;
            int centerY = Math.Max(80, (Height / 2) - 15);

            using (Pen circlePen = new Pen(Color.FromArgb(82, 92, 105), 1.5F))
            using (Pen documentPen = new Pen(Color.FromArgb(105, 116, 130), 2F))
            using (SolidBrush titleBrush = new SolidBrush(Color.FromArgb(198, 204, 213)))
            using (SolidBrush subtitleBrush = new SolidBrush(DarkUi.TextMuted))
            using (Font titleFont = new Font("Microsoft YaHei UI", 14F, FontStyle.Regular))
            using (Font subtitleFont = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular))
            using (StringFormat centered = new StringFormat())
            {
                centered.Alignment = StringAlignment.Center;
                centered.LineAlignment = StringAlignment.Center;

                eventArgs.Graphics.DrawEllipse(
                    circlePen,
                    centerX - 38,
                    centerY - 58,
                    76,
                    76);

                Rectangle document = new Rectangle(
                    centerX - 14,
                    centerY - 38,
                    28,
                    36);
                eventArgs.Graphics.DrawRectangle(documentPen, document);
                eventArgs.Graphics.DrawLine(
                    documentPen,
                    centerX - 7,
                    centerY - 27,
                    centerX + 7,
                    centerY - 27);
                eventArgs.Graphics.DrawLine(
                    documentPen,
                    centerX - 7,
                    centerY - 19,
                    centerX + 6,
                    centerY - 19);

                eventArgs.Graphics.DrawString(
                    "暂无倒计时",
                    titleFont,
                    titleBrush,
                    new RectangleF(0, centerY + 30, Width, 32),
                    centered);
                eventArgs.Graphics.DrawString(
                    "添加一个倒计时开始吧",
                    subtitleFont,
                    subtitleBrush,
                    new RectangleF(0, centerY + 64, Width, 28),
                    centered);
            }
        }
    }

    internal sealed class DarkReminderDialog : Form
    {
        internal DarkReminderDialog(string reminderText)
        {
            string content = MainForm.NormalizeReminder(reminderText);
            Text = "提醒";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(420, 250);
            BackColor = DarkUi.Card;
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular);
            AutoScaleMode = AutoScaleMode.Dpi;

            Label title = new Label();
            title.Text = string.Equals(content, "时间到", StringComparison.Ordinal)
                ? "时间到！"
                : "提醒";
            title.Font = new Font(Font.FontFamily, 22F, FontStyle.Bold);
            title.ForeColor = DarkUi.BlueHover;
            title.TextAlign = ContentAlignment.MiddleCenter;
            title.Location = new Point(30, 38);
            title.Size = new Size(360, 48);
            Controls.Add(title);

            Label message = new Label();
            message.Text = content;
            message.Font = new Font(Font.FontFamily, 11F, FontStyle.Regular);
            message.ForeColor = DarkUi.Text;
            message.TextAlign = ContentAlignment.MiddleCenter;
            message.AutoEllipsis = true;
            message.Location = new Point(38, 92);
            message.Size = new Size(344, 56);
            Controls.Add(message);

            RoundedButton confirm = new RoundedButton();
            confirm.Text = "知道了";
            confirm.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            confirm.FillColor = DarkUi.Blue;
            confirm.HoverColor = DarkUi.BlueHover;
            confirm.PressedColor = Color.FromArgb(27, 91, 214);
            confirm.BorderColor = DarkUi.Blue;
            confirm.TextColor = Color.White;
            confirm.Location = new Point(105, 174);
            confirm.Size = new Size(210, 46);
            confirm.Click += delegate { Close(); };
            Controls.Add(confirm);
            AcceptButton = confirm;

            MouseDown += DragDialog;
            title.MouseDown += DragDialog;
            Shown += delegate
            {
                DarkUiNative.EnableRoundedCorners(this);
                Activate();
            };
        }

        private void DragDialog(object sender, MouseEventArgs eventArgs)
        {
            if (eventArgs.Button == MouseButtons.Left)
            {
                DarkUiNative.BeginWindowDrag(this);
            }
        }
    }

    internal sealed class DarkMainForm : Form
    {
        private readonly Panel _titleBar;
        private readonly Label _windowTitle;
        private readonly AppIconControl _appIcon;
        private readonly TitleBarButton _minimizeButton;
        private readonly TitleBarButton _maximizeButton;
        private readonly TitleBarButton _closeButton;
        private readonly Label _heroTitle;
        private readonly Label _heroSubtitle;
        private readonly RoundedPanel _inputCard;
        private readonly Label _minutesLabel;
        private readonly MinutesInputControl _minutesInput;
        private readonly Label _reminderLabel;
        private readonly DarkTextInput _reminderInput;
        private readonly RoundedButton _addButton;
        private readonly Label _listIcon;
        private readonly Label _listTitle;
        private readonly Label _countNumber;
        private readonly Label _countText;
        private readonly RoundedPanel _listCard;
        private readonly DataGridView _grid;
        private readonly EmptyStateControl _emptyState;
        private readonly RoundedPanel _actionCard;
        private readonly RoundedButton _floatButton;
        private readonly RoundedButton _cancelButton;
        private readonly RoundedButton _deleteButton;
        private readonly Label _hintLabel;
        private readonly Timer _uiTimer;
        private readonly List<CountdownItem> _items;
        private readonly List<DarkReminderDialog> _reminderDialogs;
        private readonly FloatingCountdownForm _floatingForm;
        private readonly Icon _applicationIcon;
        private readonly ContextMenuStrip _trayMenu;
        private readonly NotifyIcon _trayIcon;

        private bool _exitRequested;
        private bool _trayTipShown;
        private bool _maximized;
        private Rectangle _restoreBounds;
        private int _reminderOffset;

        internal DarkMainForm(bool runUiTest)
        {
            _items = new List<CountdownItem>();
            _reminderDialogs = new List<DarkReminderDialog>();
            _floatingForm = new FloatingCountdownForm();
            _applicationIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (_applicationIcon == null)
            {
                _applicationIcon = (Icon)SystemIcons.Application.Clone();
            }
            _trayMenu = new ContextMenuStrip();

            ToolStripMenuItem openTrayItem = new ToolStripMenuItem("打开提醒");
            openTrayItem.Click += delegate { RestoreFromTray(); };
            _trayMenu.Items.Add(openTrayItem);
            _trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exitTrayItem = new ToolStripMenuItem("退出");
            exitTrayItem.Click += delegate { RequestApplicationExit(); };
            _trayMenu.Items.Add(exitTrayItem);

            _trayIcon = new NotifyIcon();
            _trayIcon.Icon = _applicationIcon;
            _trayIcon.Text = "提醒";
            _trayIcon.ContextMenuStrip = _trayMenu;
            _trayIcon.Visible = false;
            _trayIcon.MouseClick += delegate(object sender, MouseEventArgs eventArgs)
            {
                if (eventArgs.Button == MouseButtons.Left)
                {
                    RestoreFromTray();
                }
            };
            _trayIcon.DoubleClick += delegate { RestoreFromTray(); };

            Text = "提醒";
            Icon = _applicationIcon;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(960, 760);
            ClientSize = new Size(1160, 840);
            BackColor = DarkUi.Background;
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular);
            AutoScaleMode = AutoScaleMode.Dpi;
            DoubleBuffered = true;

            _titleBar = new Panel();
            _titleBar.BackColor = DarkUi.TitleBar;
            Controls.Add(_titleBar);

            _appIcon = new AppIconControl();
            _titleBar.Controls.Add(_appIcon);

            _windowTitle = new Label();
            _windowTitle.Text = "提醒";
            _windowTitle.Font = new Font(Font.FontFamily, 11F, FontStyle.Regular);
            _windowTitle.ForeColor = DarkUi.Text;
            _windowTitle.TextAlign = ContentAlignment.MiddleLeft;
            _titleBar.Controls.Add(_windowTitle);

            _minimizeButton = new TitleBarButton("—", false);
            _minimizeButton.Click += delegate { WindowState = FormWindowState.Minimized; };
            _titleBar.Controls.Add(_minimizeButton);

            _maximizeButton = new TitleBarButton("□", false);
            _maximizeButton.Click += delegate { ToggleMaximize(); };
            _titleBar.Controls.Add(_maximizeButton);

            _closeButton = new TitleBarButton("×", true);
            _closeButton.Click += delegate { Close(); };
            _titleBar.Controls.Add(_closeButton);

            _heroTitle = new Label();
            _heroTitle.Text = "提醒";
            _heroTitle.Font = new Font(Font.FontFamily, 25F, FontStyle.Bold);
            _heroTitle.ForeColor = DarkUi.Text;
            _heroTitle.AutoSize = true;
            Controls.Add(_heroTitle);

            _heroSubtitle = new Label();
            _heroSubtitle.Text = "可同时创建多个提醒，并选择一个在桌面悬浮显示";
            _heroSubtitle.Font = new Font(Font.FontFamily, 11F, FontStyle.Regular);
            _heroSubtitle.ForeColor = DarkUi.TextMuted;
            _heroSubtitle.AutoSize = true;
            Controls.Add(_heroSubtitle);

            _inputCard = new RoundedPanel();
            _inputCard.FillColor = Color.FromArgb(13, 18, 24);
            _inputCard.BorderColor = DarkUi.Border;
            _inputCard.CornerRadius = 14;
            Controls.Add(_inputCard);

            _minutesLabel = new Label();
            _minutesLabel.Text = "分钟";
            _minutesLabel.Font = new Font(Font.FontFamily, 10.5F, FontStyle.Regular);
            _minutesLabel.ForeColor = DarkUi.Text;
            _minutesLabel.AutoSize = true;
            _inputCard.Controls.Add(_minutesLabel);

            _minutesInput = new MinutesInputControl();
            _minutesInput.EnterPressed += delegate { AddFromInputs(); };
            _inputCard.Controls.Add(_minutesInput);

            _reminderLabel = new Label();
            _reminderLabel.Text = "提醒内容";
            _reminderLabel.Font = new Font(Font.FontFamily, 10.5F, FontStyle.Regular);
            _reminderLabel.ForeColor = DarkUi.Text;
            _reminderLabel.AutoSize = true;
            _inputCard.Controls.Add(_reminderLabel);

            Label optionalLabel = new Label();
            optionalLabel.Name = "OptionalLabel";
            optionalLabel.Text = "（可选）";
            optionalLabel.Font = new Font(Font.FontFamily, 9.5F, FontStyle.Regular);
            optionalLabel.ForeColor = DarkUi.TextMuted;
            optionalLabel.AutoSize = true;
            _inputCard.Controls.Add(optionalLabel);

            _reminderInput = new DarkTextInput();
            _reminderInput.Placeholder = "例如：专注学习，休息一下...";
            _reminderInput.EnterPressed += delegate { AddFromInputs(); };
            _inputCard.Controls.Add(_reminderInput);

            _addButton = new RoundedButton();
            _addButton.Text = "＋  添加倒计时";
            _addButton.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            _addButton.FillColor = DarkUi.Blue;
            _addButton.HoverColor = DarkUi.BlueHover;
            _addButton.PressedColor = Color.FromArgb(27, 91, 214);
            _addButton.BorderColor = DarkUi.Blue;
            _addButton.TextColor = Color.White;
            _addButton.CornerRadius = 10;
            _addButton.Click += delegate { AddFromInputs(); };
            _inputCard.Controls.Add(_addButton);

            _listIcon = new Label();
            _listIcon.Text = "☷";
            _listIcon.Font = new Font("Segoe UI Symbol", 21F, FontStyle.Regular);
            _listIcon.ForeColor = DarkUi.BlueHover;
            _listIcon.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(_listIcon);

            _listTitle = new Label();
            _listTitle.Text = "倒计时列表";
            _listTitle.Font = new Font(Font.FontFamily, 14F, FontStyle.Bold);
            _listTitle.ForeColor = DarkUi.Text;
            _listTitle.AutoSize = true;
            Controls.Add(_listTitle);

            _countNumber = new Label();
            _countNumber.Text = "0";
            _countNumber.Font = new Font("Segoe UI", 11F, FontStyle.Regular);
            _countNumber.ForeColor = DarkUi.BlueHover;
            _countNumber.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(_countNumber);

            _countText = new Label();
            _countText.Text = "个进行中";
            _countText.Font = new Font(Font.FontFamily, 10.5F, FontStyle.Regular);
            _countText.ForeColor = DarkUi.TextMuted;
            _countText.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(_countText);

            _listCard = new RoundedPanel();
            _listCard.FillColor = DarkUi.Card;
            _listCard.BorderColor = DarkUi.Border;
            _listCard.CornerRadius = 14;
            Controls.Add(_listCard);

            _grid = CreateDarkGrid();
            _grid.SelectionChanged += delegate { UpdateActionButtons(); };
            _grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs eventArgs)
            {
                if (eventArgs.RowIndex >= 0)
                {
                    ToggleFloatingForSelected();
                }
            };
            _listCard.Controls.Add(_grid);

            _emptyState = new EmptyStateControl();
            _listCard.Controls.Add(_emptyState);
            _emptyState.BringToFront();

            _actionCard = new RoundedPanel();
            _actionCard.FillColor = Color.FromArgb(13, 18, 24);
            _actionCard.BorderColor = DarkUi.BorderSoft;
            _actionCard.CornerRadius = 14;
            Controls.Add(_actionCard);

            _floatButton = CreateActionButton("▣  悬浮显示", DarkUi.BlueHover);
            _floatButton.BorderColor = Color.FromArgb(49, 105, 184);
            _floatButton.Click += delegate { ToggleFloatingForSelected(); };
            _actionCard.Controls.Add(_floatButton);

            _cancelButton = CreateActionButton("▣  取消倒计时", DarkUi.TextMuted);
            _cancelButton.Click += delegate { CancelSelected(); };
            _actionCard.Controls.Add(_cancelButton);

            _deleteButton = CreateActionButton("♜  删除", DarkUi.Red);
            _deleteButton.BorderColor = Color.FromArgb(87, 51, 54);
            _deleteButton.Click += delegate { DeleteSelected(); };
            _actionCard.Controls.Add(_deleteButton);

            _hintLabel = new Label();
            _hintLabel.Text = "双击可悬浮；悬浮窗可拖动，右键或按 Esc 隐藏";
            _hintLabel.Font = new Font(Font.FontFamily, 9.5F, FontStyle.Regular);
            _hintLabel.ForeColor = DarkUi.TextMuted;
            _hintLabel.TextAlign = ContentAlignment.MiddleRight;
            _actionCard.Controls.Add(_hintLabel);

            _floatingForm.VisibleChanged += delegate
            {
                RefreshAllRows();
                UpdateActionButtons();
            };

            _uiTimer = new Timer();
            _uiTimer.Interval = 250;
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();

            _titleBar.MouseDown += DragMainWindow;
            _windowTitle.MouseDown += DragMainWindow;
            _appIcon.MouseDown += DragMainWindow;
            _titleBar.DoubleClick += delegate { ToggleMaximize(); };
            Resize += delegate
            {
                LayoutUi();
                Invalidate();
            };
            Shown += delegate
            {
                DarkUiNative.EnableRoundedCorners(this);
                LayoutUi();

                if (runUiTest)
                {
                    CreateCountdown(0.08M, "喝水提醒");
                    CountdownItem floatingItem = CreateCountdown(0.15M, "提交日报");
                    SelectItem(floatingItem);
                    _floatingForm.ShowFor(floatingItem);
                    RefreshAllRows();
                }
                else
                {
                    _minutesInput.FocusInput();
                }
            };
            FormClosing += DarkMainForm_FormClosing;
            FormClosed += delegate
            {
                _uiTimer.Stop();
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayMenu.Dispose();
                _applicationIcon.Dispose();
                if (!_floatingForm.IsDisposed)
                {
                    _floatingForm.CloseForApplicationExit();
                }
            };

            LayoutUi();
            UpdateCountAndEmptyState();
            UpdateActionButtons();
        }

        protected override void OnPaintBackground(PaintEventArgs eventArgs)
        {
            using (LinearGradientBrush brush = new LinearGradientBrush(
                ClientRectangle,
                Color.FromArgb(12, 17, 23),
                DarkUi.Background,
                LinearGradientMode.Vertical))
            {
                eventArgs.Graphics.FillRectangle(brush, ClientRectangle);
            }
        }

        private DataGridView CreateDarkGrid()
        {
            DataGridView grid = new DataGridView();
            grid.BackgroundColor = DarkUi.Card;
            grid.BorderStyle = BorderStyle.None;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AllowUserToResizeColumns = false;
            grid.RowHeadersVisible = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoGenerateColumns = false;
            grid.ColumnHeadersHeight = 50;
            grid.ColumnHeadersHeightSizeMode =
                DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.RowTemplate.Height = 46;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(17, 23, 30);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = DarkUi.TextMuted;
            grid.ColumnHeadersDefaultCellStyle.Font =
                new Font(Font.FontFamily, 10F, FontStyle.Regular);
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor =
                Color.FromArgb(17, 23, 30);
            grid.DefaultCellStyle.BackColor = DarkUi.Card;
            grid.DefaultCellStyle.ForeColor = Color.FromArgb(195, 202, 213);
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(23, 42, 68);
            grid.DefaultCellStyle.SelectionForeColor = DarkUi.Text;
            grid.DefaultCellStyle.Font =
                new Font(Font.FontFamily, 10F, FontStyle.Regular);
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.GridColor = DarkUi.BorderSoft;

            DataGridViewTextBoxColumn contentColumn = new DataGridViewTextBoxColumn();
            contentColumn.HeaderText = "提醒内容";
            contentColumn.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            contentColumn.MinimumWidth = 320;
            contentColumn.DefaultCellStyle.Padding = new Padding(16, 0, 0, 0);
            grid.Columns.Add(contentColumn);

            DataGridViewTextBoxColumn remainingColumn = new DataGridViewTextBoxColumn();
            remainingColumn.HeaderText = "剩余时间";
            remainingColumn.Width = 250;
            remainingColumn.DefaultCellStyle.Font =
                new Font("Consolas", 11F, FontStyle.Bold);
            remainingColumn.DefaultCellStyle.Alignment =
                DataGridViewContentAlignment.MiddleCenter;
            remainingColumn.HeaderCell.Style.Alignment =
                DataGridViewContentAlignment.MiddleCenter;
            grid.Columns.Add(remainingColumn);

            DataGridViewTextBoxColumn statusColumn = new DataGridViewTextBoxColumn();
            statusColumn.HeaderText = "状态";
            statusColumn.Width = 190;
            statusColumn.DefaultCellStyle.Alignment =
                DataGridViewContentAlignment.MiddleCenter;
            statusColumn.HeaderCell.Style.Alignment =
                DataGridViewContentAlignment.MiddleCenter;
            grid.Columns.Add(statusColumn);

            return grid;
        }

        private static RoundedButton CreateActionButton(string text, Color textColor)
        {
            RoundedButton button = new RoundedButton();
            button.Text = text;
            button.Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Regular);
            button.FillColor = Color.FromArgb(12, 17, 23);
            button.HoverColor = Color.FromArgb(21, 28, 37);
            button.PressedColor = Color.FromArgb(26, 34, 45);
            button.BorderColor = DarkUi.Border;
            button.TextColor = textColor;
            button.CornerRadius = 10;
            return button;
        }

        private void LayoutUi()
        {
            int width = ClientSize.Width;
            int height = ClientSize.Height;
            int margin = 42;

            _titleBar.SetBounds(0, 0, width, 56);
            _appIcon.SetBounds(22, 15, 26, 26);
            _windowTitle.SetBounds(60, 0, 220, 56);
            _closeButton.SetBounds(width - 48, 0, 48, 56);
            _maximizeButton.SetBounds(width - 96, 0, 48, 56);
            _minimizeButton.SetBounds(width - 144, 0, 48, 56);

            _heroTitle.Location = new Point(margin, 92);
            _heroSubtitle.Location = new Point(margin + 1, 145);

            _inputCard.SetBounds(margin, 190, width - (margin * 2), 138);
            int inputWidth = _inputCard.ClientSize.Width;
            _minutesLabel.Location = new Point(26, 23);
            _minutesInput.SetBounds(26, 55, 250, 54);
            _reminderLabel.Location = new Point(310, 23);
            Control optional = _inputCard.Controls["OptionalLabel"];
            if (optional != null)
            {
                optional.Location = new Point(377, 24);
            }

            int addWidth = 205;
            int addLeft = inputWidth - addWidth - 26;
            _addButton.SetBounds(addLeft, 53, addWidth, 58);
            _reminderInput.SetBounds(
                310,
                55,
                Math.Max(240, addLeft - 310 - 24),
                54);

            int listHeaderTop = 355;
            _listIcon.SetBounds(margin, listHeaderTop - 4, 34, 38);
            _listTitle.Location = new Point(margin + 42, listHeaderTop + 2);
            _countNumber.SetBounds(width - margin - 118, listHeaderTop, 32, 34);
            _countText.SetBounds(width - margin - 82, listHeaderTop, 82, 34);

            int actionTop = height - 108;
            int listTop = 402;
            int listHeight = Math.Max(190, actionTop - listTop - 20);
            _listCard.SetBounds(margin, listTop, width - (margin * 2), listHeight);
            _grid.SetBounds(
                2,
                2,
                _listCard.ClientSize.Width - 4,
                _listCard.ClientSize.Height - 4);
            _emptyState.SetBounds(
                2,
                52,
                _listCard.ClientSize.Width - 4,
                Math.Max(80, _listCard.ClientSize.Height - 54));

            _actionCard.SetBounds(margin, actionTop, width - (margin * 2), 78);
            _floatButton.SetBounds(18, 13, 165, 50);
            _cancelButton.SetBounds(198, 13, 175, 50);
            _deleteButton.SetBounds(388, 13, 135, 50);
            _hintLabel.SetBounds(
                540,
                13,
                Math.Max(200, _actionCard.ClientSize.Width - 562),
                50);
        }

        private void DragMainWindow(object sender, MouseEventArgs eventArgs)
        {
            if (eventArgs.Button == MouseButtons.Left && !_maximized)
            {
                DarkUiNative.BeginWindowDrag(this);
            }
        }

        private void ToggleMaximize()
        {
            if (!_maximized)
            {
                _restoreBounds = Bounds;
                Bounds = Screen.FromControl(this).WorkingArea;
                _maximized = true;
                _maximizeButton.Glyph = "❐";
            }
            else
            {
                Bounds = _restoreBounds;
                _maximized = false;
                _maximizeButton.Glyph = "□";
            }

            LayoutUi();
        }

        private void AddFromInputs()
        {
            CountdownItem item = CreateCountdown(
                _minutesInput.Value,
                _reminderInput.Text);
            _reminderInput.ClearInput();
            SelectItem(item);
            _reminderInput.FocusInput();
        }

        private CountdownItem CreateCountdown(decimal minutes, string reminderText)
        {
            CountdownItem item = new CountdownItem(minutes, reminderText);
            DataGridViewRow row = new DataGridViewRow();
            row.Height = 46;
            row.CreateCells(
                _grid,
                MainForm.NormalizeReminder(item.ReminderText),
                MainForm.FormatRemaining(item.Remaining),
                "进行中");
            row.Tag = item;
            item.Row = row;
            _items.Add(item);
            _grid.Rows.Add(row);
            UpdateCountAndEmptyState();
            return item;
        }

        private void SelectItem(CountdownItem item)
        {
            _grid.ClearSelection();
            if (item != null && item.Row != null)
            {
                item.Row.Selected = true;
                _grid.CurrentCell = item.Row.Cells[0];
            }
        }

        private CountdownItem GetSelectedItem()
        {
            if (_grid.SelectedRows.Count == 0)
            {
                return null;
            }

            return _grid.SelectedRows[0].Tag as CountdownItem;
        }

        private void UiTimer_Tick(object sender, EventArgs eventArgs)
        {
            List<CountdownItem> completedItems = new List<CountdownItem>();
            foreach (CountdownItem item in _items)
            {
                if (item.Status == CountdownStatus.Running &&
                    item.Remaining <= TimeSpan.Zero)
                {
                    item.Status = CountdownStatus.Completed;
                    completedItems.Add(item);
                }

                UpdateRow(item);
            }

            if (_floatingForm.CurrentItem != null)
            {
                _floatingForm.UpdateCountdown();
            }

            foreach (CountdownItem item in completedItems)
            {
                ShowReminder(item);
            }

            if (completedItems.Count > 0)
            {
                UpdateCountAndEmptyState();
                UpdateActionButtons();
            }
        }

        private void ShowReminder(CountdownItem item)
        {
            PlayReminderSound();

            DarkReminderDialog dialog = new DarkReminderDialog(item.ReminderText);
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            int offset = (_reminderOffset % 5) * 24;
            dialog.Location = new Point(
                area.Left + ((area.Width - dialog.Width) / 2) + offset,
                area.Top + ((area.Height - dialog.Height) / 2) + offset);
            _reminderOffset++;
            _reminderDialogs.Add(dialog);
            dialog.FormClosed += delegate
            {
                _reminderDialogs.Remove(dialog);
                dialog.Dispose();
            };
            dialog.Show();
            dialog.Activate();
        }

        private static void PlayReminderSound()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    SystemSounds.Exclamation.Play();
                }
                catch
                {
                    // 音频设备异常时，视觉提醒仍然正常显示。
                }
            });
        }

        private void ToggleFloatingForSelected()
        {
            CountdownItem item = GetSelectedItem();
            if (item == null || item.Status != CountdownStatus.Running)
            {
                return;
            }

            if (ReferenceEquals(_floatingForm.CurrentItem, item) &&
                _floatingForm.Visible)
            {
                _floatingForm.Hide();
            }
            else
            {
                _floatingForm.ShowFor(item);
            }

            RefreshAllRows();
            UpdateActionButtons();
        }

        private void CancelSelected()
        {
            CountdownItem item = GetSelectedItem();
            if (item == null || item.Status != CountdownStatus.Running)
            {
                return;
            }

            item.Status = CountdownStatus.Cancelled;
            if (ReferenceEquals(_floatingForm.CurrentItem, item))
            {
                _floatingForm.ClearItem();
            }

            UpdateRow(item);
            UpdateCountAndEmptyState();
            UpdateActionButtons();
        }

        private void DeleteSelected()
        {
            CountdownItem item = GetSelectedItem();
            if (item == null)
            {
                return;
            }

            if (ReferenceEquals(_floatingForm.CurrentItem, item))
            {
                _floatingForm.ClearItem();
            }

            _items.Remove(item);
            _grid.Rows.Remove(item.Row);
            UpdateCountAndEmptyState();
            UpdateActionButtons();
        }

        private void UpdateRow(CountdownItem item)
        {
            if (item.Row == null || item.Row.DataGridView == null)
            {
                return;
            }

            item.Row.Cells[0].Value = MainForm.NormalizeReminder(item.ReminderText);
            item.Row.Cells[1].Value = MainForm.FormatRemaining(item.Remaining);

            if (item.Status == CountdownStatus.Completed)
            {
                item.Row.Cells[2].Value = "已结束";
                item.Row.DefaultCellStyle.ForeColor = DarkUi.TextMuted;
            }
            else if (item.Status == CountdownStatus.Cancelled)
            {
                item.Row.Cells[2].Value = "已取消";
                item.Row.DefaultCellStyle.ForeColor = DarkUi.TextDim;
            }
            else if (ReferenceEquals(_floatingForm.CurrentItem, item) &&
                _floatingForm.Visible)
            {
                item.Row.Cells[2].Value = "进行中 · 悬浮";
                item.Row.DefaultCellStyle.ForeColor = DarkUi.BlueHover;
            }
            else
            {
                item.Row.Cells[2].Value = "进行中";
                item.Row.DefaultCellStyle.ForeColor =
                    Color.FromArgb(195, 202, 213);
            }
        }

        private void RefreshAllRows()
        {
            foreach (CountdownItem item in _items)
            {
                UpdateRow(item);
            }
        }

        private void UpdateCountAndEmptyState()
        {
            int runningCount = 0;
            foreach (CountdownItem item in _items)
            {
                if (item.Status == CountdownStatus.Running)
                {
                    runningCount++;
                }
            }

            _countNumber.Text = runningCount.ToString();
            _emptyState.Visible = _items.Count == 0;
            if (_emptyState.Visible)
            {
                _emptyState.BringToFront();
            }
        }

        private void UpdateActionButtons()
        {
            CountdownItem item = GetSelectedItem();
            bool hasSelection = item != null;
            bool isRunning = hasSelection && item.Status == CountdownStatus.Running;

            _floatButton.Enabled = isRunning;
            _cancelButton.Enabled = isRunning;
            _deleteButton.Enabled = hasSelection;
            _floatButton.Text = isRunning &&
                ReferenceEquals(_floatingForm.CurrentItem, item) &&
                _floatingForm.Visible
                ? "▣  隐藏悬浮窗"
                : "▣  悬浮显示";
        }

        private void DarkMainForm_FormClosing(object sender, FormClosingEventArgs eventArgs)
        {
            if (_exitRequested)
            {
                return;
            }

            if (eventArgs.CloseReason == CloseReason.UserClosing)
            {
                eventArgs.Cancel = true;
                HideToTray();
                return;
            }

            _exitRequested = true;
        }

        internal bool TrayIconVisible
        {
            get { return _trayIcon.Visible; }
        }

        internal bool TrayMenuReady
        {
            get
            {
                return _trayMenu.Items.Count == 3 &&
                    _trayMenu.Items[0].Text == "打开提醒" &&
                    _trayMenu.Items[2].Text == "退出";
            }
        }

        internal void RestoreFromTrayForTest()
        {
            RestoreFromTray();
        }

        internal void SelectTrayExitForTest()
        {
            ToolStripMenuItem exitItem = _trayMenu.Items[2] as ToolStripMenuItem;
            if (exitItem != null)
            {
                exitItem.PerformClick();
            }
        }

        internal void RequestApplicationExit()
        {
            if (_exitRequested)
            {
                return;
            }

            _exitRequested = true;
            _trayIcon.Visible = false;
            Close();
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
            _trayIcon.Visible = true;

            if (!_trayTipShown)
            {
                _trayTipShown = true;
                _trayIcon.ShowBalloonTip(
                    2500,
                    "提醒",
                    "程序仍在后台运行，右键托盘图标可退出。",
                    ToolTipIcon.Info);
            }
        }

        private void RestoreFromTray()
        {
            if (_exitRequested)
            {
                return;
            }

            _trayIcon.Visible = false;
            ShowInTaskbar = true;
            Show();

            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }

            Activate();
            BringToFront();
        }
    }
}
