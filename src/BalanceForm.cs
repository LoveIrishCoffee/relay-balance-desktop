using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace RelayBalanceDesktop
{
    public sealed class BalanceForm : Form
    {
        private static readonly Color Ink = Color.FromArgb(25, 48, 70);
        private static readonly Color Muted = Color.FromArgb(103, 119, 136);
        private static readonly Color Teal = Color.FromArgb(0, 131, 119);
        private static readonly Color Warning = Color.FromArgb(164, 94, 13);
        private readonly BackendClient _client;
        private readonly bool _preview;
        private readonly int _uiThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        private readonly Dictionary<string, double> _thresholds = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Dictionary<string, AdapterConfig> _adapters = new Dictionary<string, AdapterConfig>(StringComparer.Ordinal);
        private readonly Dictionary<string, ProviderSnapshot> _providers = new Dictionary<string, ProviderSnapshot>(StringComparer.Ordinal);
        private readonly HashSet<string> _editedThresholds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _editedAdapters = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _lastLow = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _pendingVisibility = new Dictionary<string, bool>(StringComparer.Ordinal);
        private List<HiddenProviderSnapshot> _hiddenProviders = new List<HiddenProviderSnapshot>();
        private HiddenProvidersDialog _hiddenDialog;
        private readonly ToolTip _tips = new ToolTip();
        private TableLayoutPanel _layout;
        private Size _contentMinimum;
        private bool _resizingCanvas;
        private DataGridView _grid;
        private Button _refreshButton, _saveButton, _adapterButton, _retryDetectionButton, _hideButton, _hiddenButton;
        private NumericUpDown _threshold;
        private ComboBox _interval;
        private CheckBox _notifications;
        private Label _summaryLabel, _selectedTitle, _thresholdUnit, _selectedAdapter, _details, _checkedLabel, _messageLabel, _emptyLabel;
        private NotifyIcon _tray;
        private ContextMenuStrip _trayMenu;
        private Snapshot _latestSnapshot;
        private string _selectedId;
        private string _detectingProviderId;
        private bool _retrievingBalance;
        private bool _exitRequested, _stopped, _updatingSettings, _updatingGrid, _intervalDirty, _waitingForSettings, _trayHintShown, _refreshing;

        public BalanceForm(BackendClient client, bool preview)
        {
            if (client == null) throw new ArgumentNullException("client");
            SuspendLayout(); _client = client; _preview = preview;
            Text = "中转站余额"; Icon = Program.AppIcon; StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(244, 247, 250); ForeColor = Ink;
            ClientSize = new Size(1120, 766); MinimumSize = new Size(1080, 746);
            BuildInterface(); AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96F, 96F);
            ResumeLayout(true); PerformAutoScale(); ConfigureDpiLayout();
            if (!_preview) BuildTray();
            _client.SnapshotReceived += OnSnapshotReceived; _client.ErrorReceived += OnErrorReceived; _client.SettingsSaved += OnSettingsSaved;
            Shown += delegate { if (!_preview) { try { _client.Start(); } catch { OnErrorReceived("后台服务启动失败，请重新打开应用。"); } } };
            Resize += delegate { UpdateScrollableLayout(); if (!_preview && WindowState == FormWindowState.Minimized) HideToTray(false); };
        }
        private void ConfigureDpiLayout()
        {
            float scale; using (Graphics graphics = CreateGraphics()) scale = graphics.DpiY / 96F;
            _grid.ColumnHeadersHeight = (int)Math.Ceiling(38F * scale);
            _grid.RowTemplate.MinimumHeight = (int)Math.Ceiling(68F * scale);
            _grid.RowTemplate.Height = _grid.RowTemplate.MinimumHeight;
            _grid.DefaultCellStyle.Padding = new Padding((int)Math.Round(10F * scale), (int)Math.Round(10F * scale), 0, (int)Math.Round(10F * scale));
            _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding((int)Math.Round(10F * scale), 0, 0, 0);
            foreach (DataGridViewColumn column in _grid.Columns) column.MinimumWidth = (int)Math.Ceiling(column.MinimumWidth * scale);
            _contentMinimum = new Size((int)Math.Ceiling(1048F * scale), (int)Math.Ceiling(580F * scale));
            Rectangle area = Screen.FromControl(this).WorkingArea;
            Size bounded = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            MinimumSize = new Size(Math.Min(MinimumSize.Width, bounded.Width), Math.Min(MinimumSize.Height, bounded.Height));
            Size = bounded; UpdateScrollableLayout();
        }
        private void UpdateScrollableLayout()
        {
            if (_resizingCanvas || _layout == null || _contentMinimum.IsEmpty || WindowState == FormWindowState.Minimized) return;
            _resizingCanvas = true;
            try
            {
                bool needsScroll = ClientSize.Width < _contentMinimum.Width || ClientSize.Height < _contentMinimum.Height;
                if (needsScroll)
                {
                    AutoScroll = true; _layout.Dock = DockStyle.None; _layout.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                    _layout.Size = new Size(Math.Max(ClientSize.Width - SystemInformation.VerticalScrollBarWidth, _contentMinimum.Width), Math.Max(ClientSize.Height - SystemInformation.HorizontalScrollBarHeight, _contentMinimum.Height));
                    AutoScrollMinSize = _layout.Size;
                }
                else
                {
                    AutoScrollMinSize = Size.Empty; AutoScroll = false; _layout.Location = Point.Empty; _layout.Dock = DockStyle.Fill;
                }
            }
            finally { _resizingCanvas = false; }
        }
        private void BuildInterface()
        {
            TableLayoutPanel layout = new TableLayoutPanel(); _layout = layout; layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(24, 17, 24, 10); layout.ColumnCount = 1; layout.RowCount = 6;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 199F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70F));
            Controls.Add(layout);
            TableLayoutPanel header = new TableLayoutPanel(); header.Dock = DockStyle.Fill; header.Margin = Padding.Empty; header.ColumnCount = 3; header.RowCount = 1;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 157F)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148F)); header.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Label intro = LabelAt("自动读取 CC Switch 配置 · 新增或切换后自动更新", 0, 0, 740, 34, 10F, FontStyle.Regular, Muted); intro.Dock = DockStyle.Fill; intro.Margin = Padding.Empty; header.Controls.Add(intro, 0, 0);
            _hiddenButton = SecondaryButton("已移出 (0)", 0, 0, 145, 38); _hiddenButton.Dock = DockStyle.Fill; _hiddenButton.Margin = new Padding(0, 4, 12, 8); _hiddenButton.Click += delegate { ShowHiddenProviders(); }; header.Controls.Add(_hiddenButton, 1, 0);
            _refreshButton = ActionButton("立即刷新", 0, 0, 136, 38); _refreshButton.Dock = DockStyle.Fill; _refreshButton.Margin = new Padding(12, 4, 0, 8);
            _refreshButton.Click += delegate { RefreshBalances(); }; header.Controls.Add(_refreshButton, 2, 0); layout.Controls.Add(header, 0, 0);
            _summaryLabel = LabelAt("正在读取本机配置…", 0, 0, 1000, 29, 9F, FontStyle.Regular, Muted);
            _summaryLabel.Dock = DockStyle.Fill; _summaryLabel.Margin = Padding.Empty; layout.Controls.Add(_summaryLabel, 0, 1);
            Panel tablePanel = new BorderPanel(); tablePanel.Dock = DockStyle.Fill; tablePanel.Padding = new Padding(1); tablePanel.Margin = new Padding(0, 0, 0, 12);
            _grid = new BufferedGrid(); _grid.Dock = DockStyle.Fill; _grid.BackgroundColor = Color.White;
            _grid.BorderStyle = BorderStyle.None; _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            _grid.GridColor = Color.FromArgb(235, 240, 243); _grid.RowHeadersVisible = false;
            _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false; _grid.AllowUserToResizeRows = false;
            _grid.ReadOnly = true; _grid.MultiSelect = false; _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; _grid.EnableHeadersVisualStyles = false;
            _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            _grid.ColumnHeadersHeight = 38; _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(234, 241, 245); _grid.ColumnHeadersDefaultCellStyle.ForeColor = Ink;
            _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(234, 241, 245);
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold); _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(10, 0, 0, 0);
            _grid.DefaultCellStyle.BackColor = Color.White; _grid.DefaultCellStyle.ForeColor = Ink;
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(226, 245, 240); _grid.DefaultCellStyle.SelectionForeColor = Ink;
            _grid.DefaultCellStyle.Padding = new Padding(10, 10, 0, 10); _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _grid.RowTemplate.MinimumHeight = 68; _grid.RowTemplate.Height = 68;
            _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCellsExceptHeaders;
            AddColumn("provider", "配置 / 应用", 177, 154); AddColumn("origin", "中转站地址", 191, 140);
            AddColumn("balance", "余额 / 单位", 132, 117); AddColumn("kind", "余额范围", 103, 88);
            AddColumn("adapter", "查询适配", 139, 110); AddColumn("status", "状态", 127, 110); AddColumn("updated", "最近更新", 117, 104);
            _grid.SelectionChanged += delegate { if (!_updatingGrid) SelectGridProvider(); };
            _grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0) ConfigureAdapter(); };
            tablePanel.Controls.Add(_grid);
            _emptyLabel = LabelAt("尚未发现可查询的中转站配置\r\n请先在 CC Switch 中添加 API 配置，再点击立即刷新。", 0, 0, 500, 100, 11F, FontStyle.Regular, Muted);
            _emptyLabel.TextAlign = ContentAlignment.MiddleCenter; _emptyLabel.Dock = DockStyle.Fill; _emptyLabel.Visible = false;
            tablePanel.Controls.Add(_emptyLabel); layout.Controls.Add(tablePanel, 0, 2);
            BorderPanel selection = new BorderPanel(); selection.Size = new Size(1072, 191); selection.Dock = DockStyle.Fill; selection.BackColor = Color.White; selection.Margin = new Padding(0, 0, 0, 8); selection.Padding = new Padding(18, 11, 18, 12);
            TableLayoutPanel selectionLayout = new TableLayoutPanel(); selectionLayout.Dock = DockStyle.Fill; selectionLayout.Margin = Padding.Empty;
            selectionLayout.ColumnCount = 1; selectionLayout.RowCount = 3; selectionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            selectionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F)); selectionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F)); selectionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); selection.Controls.Add(selectionLayout);
            _selectedTitle = LabelAt("选择配置，查看详情和设置", 0, 0, 1018, 29, 12F, FontStyle.Bold, Ink);
            _selectedTitle.Dock = DockStyle.Fill; _selectedTitle.Margin = Padding.Empty;
            TableLayoutPanel selectionHeading = new TableLayoutPanel(); selectionHeading.Dock = DockStyle.Fill; selectionHeading.Margin = Padding.Empty; selectionHeading.ColumnCount = 2; selectionHeading.RowCount = 1;
            selectionHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); selectionHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140F)); selectionHeading.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); selectionHeading.Controls.Add(_selectedTitle, 0, 0);
            _hideButton = SecondaryButton("移出列表", 0, 0, 134, 30); _hideButton.Dock = DockStyle.Fill; _hideButton.Margin = new Padding(6, 0, 0, 2); _hideButton.Enabled = false;
            _hideButton.Click += delegate { if (_selectedId != null) SetProviderVisibility(_selectedId, true); }; _tips.SetToolTip(_hideButton, "仅停止本软件监控，不修改 CC Switch。可从“已移出”随时恢复。"); selectionHeading.Controls.Add(_hideButton, 1, 0); selectionLayout.Controls.Add(selectionHeading, 0, 0);
            // Separate cells keep the descriptive label out of both button hit areas at every DPI.
            TableLayoutPanel settingsRow = new TableLayoutPanel(); settingsRow.Dock = DockStyle.Fill; settingsRow.Margin = Padding.Empty; settingsRow.ColumnCount = 4; settingsRow.RowCount = 1;
            settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 390F)); settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 153F)); settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 171F)); settingsRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); selectionLayout.Controls.Add(settingsRow, 0, 1);
            TableLayoutPanel thresholdRow = new TableLayoutPanel(); thresholdRow.Dock = DockStyle.Fill; thresholdRow.Margin = Padding.Empty; thresholdRow.ColumnCount = 3; thresholdRow.RowCount = 1;
            thresholdRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132F)); thresholdRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135F)); thresholdRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); thresholdRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); settingsRow.Controls.Add(thresholdRow, 0, 0);
            Label thresholdCaption = LabelAt("低余额提醒阈值", 0, 0, 132, 25, 9F, FontStyle.Regular, Muted); thresholdCaption.Dock = DockStyle.Fill; thresholdCaption.Margin = Padding.Empty; thresholdRow.Controls.Add(thresholdCaption, 0, 0);
            _threshold = new NumericUpDown(); _threshold.Size = new Size(135, 27); _threshold.Anchor = AnchorStyles.Left | AnchorStyles.Right; _threshold.Margin = Padding.Empty;
            _threshold.Minimum = 0; _threshold.Maximum = 1000000000M; _threshold.DecimalPlaces = 4; _threshold.Increment = 1M; _threshold.ThousandsSeparator = true; _threshold.Enabled = false;
            _threshold.ValueChanged += delegate { if (!_updatingSettings && !String.IsNullOrEmpty(_selectedId)) { _thresholds[_selectedId] = (double)_threshold.Value; _editedThresholds.Add(_selectedId); MarkSettingsDirty(); } };
            thresholdRow.Controls.Add(_threshold, 1, 0);
            _thresholdUnit = LabelAt("", 0, 0, 98, 25, 9F, FontStyle.Regular, Muted); _thresholdUnit.Dock = DockStyle.Fill; _thresholdUnit.Margin = new Padding(10, 0, 0, 0); thresholdRow.Controls.Add(_thresholdUnit, 2, 0);
            _selectedAdapter = LabelAt("查询适配：自动检测", 0, 0, 285, 25, 9F, FontStyle.Regular, Muted); _selectedAdapter.Dock = DockStyle.Fill; _selectedAdapter.Margin = new Padding(6, 0, 6, 0); settingsRow.Controls.Add(_selectedAdapter, 1, 0);
            _retryDetectionButton = ActionButton("适配", 0, 0, 147, 34); _retryDetectionButton.Dock = DockStyle.Fill; _retryDetectionButton.Margin = new Padding(6, 7, 0, 7); _retryDetectionButton.Visible = false;
            _retryDetectionButton.Click += delegate { RetryDetection(); }; settingsRow.Controls.Add(_retryDetectionButton, 2, 0);
            _adapterButton = ActionButton("手动配置…", 0, 0, 165, 34); _adapterButton.Dock = DockStyle.Fill; _adapterButton.Margin = new Padding(6, 7, 0, 7); _adapterButton.Enabled = false;
            _adapterButton.Click += delegate { ConfigureAdapter(); }; settingsRow.Controls.Add(_adapterButton, 3, 0);
            _details = LabelAt("每个配置单独显示余额和单位；余额范围以站点返回结果为准。", 0, 0, 1025, 80, 9F, FontStyle.Regular, Muted);
            _details.Dock = DockStyle.Fill; _details.Margin = new Padding(0, 7, 0, 0); _details.AutoEllipsis = false; selectionLayout.Controls.Add(_details, 0, 2); layout.Controls.Add(selection, 0, 3);
            Panel global = new Panel(); global.Size = new Size(1072, 67); global.Dock = DockStyle.Fill; global.Margin = Padding.Empty;
            global.Controls.Add(LabelAt("自动刷新", 0, 13, 81, 26, 9F, FontStyle.Regular, Muted));
            _interval = new ComboBox(); _interval.Location = new Point(83, 13); _interval.Size = new Size(138, 30); _interval.DropDownStyle = ComboBoxStyle.DropDownList;
            _interval.Items.AddRange(new object[] { new IntervalItem(60), new IntervalItem(300), new IntervalItem(600), new IntervalItem(1800), new IntervalItem(3600) }); _interval.SelectedIndex = 1;
            _interval.SelectedIndexChanged += delegate { if (!_updatingSettings) { _intervalDirty = true; MarkSettingsDirty(); } }; global.Controls.Add(_interval);
            _notifications = new CheckBox(); _notifications.Location = new Point(247, 13); _notifications.Size = new Size(236, 29);
            _notifications.Text = "启用桌面低余额提醒"; _notifications.Checked = _preview || LoadNotifications();
            _notifications.CheckedChanged += delegate { SaveNotifications(); }; global.Controls.Add(_notifications);
            _saveButton = ActionButton("保存设置", 919, 9, 153, 38); _saveButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _saveButton.Click += delegate { SaveSettings(); }; global.Controls.Add(_saveButton); layout.Controls.Add(global, 0, 4);
            TableLayoutPanel footer = new TableLayoutPanel(); footer.Dock = DockStyle.Fill; footer.Margin = Padding.Empty; footer.ColumnCount = 1; footer.RowCount = 3;
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100F));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute,21F)); footer.RowStyles.Add(new RowStyle(SizeType.Absolute,22F)); footer.RowStyles.Add(new RowStyle(SizeType.Percent,100F));
            _checkedLabel = LabelAt("最近查询：尚未查询", 0, 0, 1068, 21, 8.5F, FontStyle.Regular, Muted); _checkedLabel.Dock = DockStyle.Fill; _checkedLabel.Margin = Padding.Empty; footer.Controls.Add(_checkedLabel,0,0);
            Label backgroundHint = LabelAt("使用说明：关闭窗口后仍会在后台更新余额。双击电脑右下角的小图标可打开窗口；右键它，选择“退出”可彻底关闭程序。",0,0,1068,22,8.5F,FontStyle.Regular,Muted);
            backgroundHint.Dock = DockStyle.Fill; backgroundHint.Margin = Padding.Empty; footer.Controls.Add(backgroundHint,0,1); _tips.SetToolTip(backgroundHint,backgroundHint.Text);
            _messageLabel = LabelAt("已连接本机 CC Switch 配置。", 0, 0, 1068, 22, 8.5F, FontStyle.Regular, Muted); _messageLabel.Dock = DockStyle.Fill; _messageLabel.Margin = Padding.Empty;
            footer.Controls.Add(_messageLabel,0,2); layout.Controls.Add(footer, 0, 5);
        }
        private void AddColumn(string name, string heading, float weight, int minimum)
        {
            DataGridViewTextBoxColumn column = new DataGridViewTextBoxColumn(); column.Name = name; column.HeaderText = heading; column.FillWeight = weight; column.MinimumWidth = minimum;
            column.SortMode = DataGridViewColumnSortMode.NotSortable; _grid.Columns.Add(column);
        }
        private static Label LabelAt(string text, int x, int y, int width, int height, float size, FontStyle style, Color color)
        {
            Label label = new Label(); label.Text = text; label.Location = new Point(x, y); label.Size = new Size(width, height);
            label.Font = new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point); label.ForeColor = color;
            label.BackColor = Color.Transparent; label.TextAlign = ContentAlignment.MiddleLeft; label.AutoEllipsis = true; return label;
        }
        private static Button ActionButton(string text, int x, int y, int width, int height)
        {
            Button button = new Button(); button.Text = text; button.Location = new Point(x, y); button.Size = new Size(width, height);
            button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 0; button.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 111, 103);
            button.BackColor = Teal; button.ForeColor = Color.White; button.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold, GraphicsUnit.Point);
            button.Cursor = Cursors.Hand; button.UseVisualStyleBackColor = false; return button;
        }
        private static Button SecondaryButton(string text, int x, int y, int width, int height)
        {
            Button button = ActionButton(text, x, y, width, height); button.BackColor = Color.White; button.ForeColor = Teal;
            button.FlatAppearance.BorderSize = 1; button.FlatAppearance.BorderColor = Color.FromArgb(197, 219, 216); button.FlatAppearance.MouseOverBackColor = Color.FromArgb(235, 248, 245); return button;
        }
        private void BuildTray()
        {
            _trayMenu = new ContextMenuStrip(); _trayMenu.Items.Add("打开余额窗口", null, delegate { ShowFromTray(); });
            _trayMenu.Items.Add("立即刷新", null, delegate { RefreshBalances(); }); _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("退出", null, delegate { _exitRequested = true; Close(); });
            _tray = new NotifyIcon(); _tray.Icon = Program.AppIcon; _tray.Text = "中转站余额 · 后台运行"; _tray.ContextMenuStrip = _trayMenu;
            _tray.DoubleClick += delegate { ShowFromTray(); }; _tray.BalloonTipClicked += delegate { ShowFromTray(); }; _tray.Visible = true;
        }
        public void ShowFromTray() { RunOnUi(delegate { Show(); WindowState = FormWindowState.Normal; ShowInTaskbar = true; Activate(); BringToFront(); }); }
        private void HideToTray(bool explain)
        {
            Hide(); if (explain && !_trayHintShown && _tray != null) { _trayHintShown = true; _tray.ShowBalloonTip(3000, "中转站余额仍在后台运行", "双击电脑右下角的小图标可打开窗口；右键它，选择“退出”可彻底关闭程序。", ToolTipIcon.Info); }
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_preview && !_exitRequested && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideToTray(true); }
            else { _exitRequested = true; StopBackend(); } base.OnFormClosing(e);
        }
        private void StopBackend()
        {
            if (_stopped) return; _stopped = true;
            _client.SnapshotReceived -= OnSnapshotReceived; _client.ErrorReceived -= OnErrorReceived; _client.SettingsSaved -= OnSettingsSaved;
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } try { if (!_preview) _client.Stop(); } catch { }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { StopBackend(); if (_trayMenu != null) { _trayMenu.Dispose(); _trayMenu = null; } _tips.Dispose(); } base.Dispose(disposing);
        }
        private void RefreshBalances()
        {
            if (_preview || _refreshing || _stopped) return; _refreshing = true; _refreshButton.Enabled = false; _refreshButton.Text = "正在刷新…";
            SetMessage("正在重新读取 CC Switch 配置并查询余额…", Muted); try { _client.Refresh(); } catch { OnErrorReceived("余额查询失败，请稍后重试。"); }
        }
        private bool SettingsDirty { get { return _intervalDirty || _editedThresholds.Count > 0 || _editedAdapters.Count > 0; } }
        private void MarkSettingsDirty() { _saveButton.Text = "保存设置"; SetMessage("设置已修改，点击“保存设置”后生效。", Muted); }
        private int SelectedInterval() { IntervalItem item = _interval.SelectedItem as IntervalItem; return item == null ? 300 : item.Seconds; }
        private void SaveSettings()
        {
            if (_preview || _waitingForSettings || _stopped) return; _waitingForSettings = true; SetSettingsEnabled(false); _saveButton.Text = "正在保存…";
            try
            {
                Dictionary<string, double> thresholds = new Dictionary<string, double>(_thresholds, StringComparer.Ordinal);
                Dictionary<string, AdapterConfig> adapters = new Dictionary<string, AdapterConfig>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, AdapterConfig> pair in _adapters) adapters[pair.Key] = CopyAdapter(pair.Value);
                _client.SaveSettings(thresholds, adapters, SelectedInterval());
            }
            catch { OnErrorReceived("设置保存失败，请重试。"); }
        }
        private void SetSettingsEnabled(bool enabled) { _threshold.Enabled = enabled && _selectedId != null; _adapterButton.Enabled = enabled && _selectedId != null; _interval.Enabled = enabled; _saveButton.Enabled = enabled; _retryDetectionButton.Enabled = enabled && _selectedId != null && !_editedAdapters.Contains(_selectedId) && !_pendingVisibility.ContainsKey(_selectedId) && !_refreshing; }
        private void OnSettingsSaved()
        {
            RunOnUi(delegate { _waitingForSettings = false; _editedThresholds.Clear(); _editedAdapters.Clear(); _intervalDirty = false;
                if (_latestSnapshot != null) MergeSettings(_latestSnapshot); SetSettingsEnabled(true); _saveButton.Text = "保存设置"; ShowSelection(); SetMessage("设置已保存，查询适配和提醒设置已生效。", Teal); });
        }
        private void OnSnapshotReceived(Snapshot data) { ApplySnapshot(data); }
        public void ApplySnapshot(Snapshot data) { if (data != null) RunOnUi(delegate { ApplySnapshotOnUi(data); }); }

        private void ApplySnapshotOnUi(Snapshot data)
        {
            if (_stopped) return;
            _latestSnapshot = data; _refreshing = data.refreshing;
            if (!_refreshing) { _detectingProviderId = null; _retrievingBalance = false; }
            _hiddenProviders = data.hiddenProviders == null ? new List<HiddenProviderSnapshot>() : new List<HiddenProviderSnapshot>(data.hiddenProviders);
            _hiddenButton.Text = "已移出 (" + _hiddenProviders.Count.ToString(CultureInfo.InvariantCulture) + ")";
            _refreshButton.Enabled = !data.refreshing; _refreshButton.Text = data.refreshing ? "正在刷新…" : "立即刷新";
            MergeSettings(data); string preserveId = _selectedId; int scroll = _grid.FirstDisplayedScrollingRowIndex;
            _updatingGrid = true; _grid.SuspendLayout();
            // Measure once after the batch. Native auto sizing then follows later column-width changes.
            _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            try
            {
                _providers.Clear(); _grid.Rows.Clear(); int good = 0, needsAttention = 0, selectedIndex = -1;
                if (data.providers != null)
                    foreach (ProviderSnapshot p in data.providers)
                    {
                        if (p == null || String.IsNullOrEmpty(p.id)) continue; _providers[p.id] = p;
                        int index = _grid.Rows.Add((p.current ? "● " : "") + p.name + "\r\n" + AppLabel(p.app), p.origin ?? "地址未配置", BalanceText(p), BalanceKindText(p), AdapterDisplay(p), StatusText(p), LocalDate(p.status == "stale" ? p.lastSuccessAt : p.updatedAt, false));
                        DataGridViewRow row = _grid.Rows[index]; row.Tag = p.id;
                        row.Cells[0].ToolTipText = p.name + " / " + AppLabel(p.app) + (p.current ? " · 当前使用" : ""); row.Cells[1].ToolTipText = p.origin ?? "";
                        row.Cells[2].Style.ForeColor = p.status == "stale" ? Muted : (p.status == "ok" && p.lowBalance ? Warning : Ink);
                        row.Cells[2].Style.SelectionForeColor = row.Cells[2].Style.ForeColor;
                        row.Cells[5].Style.ForeColor = p.status == "ok" && !p.lowBalance && !MissingAccountBalance(p) ? Teal : ((p.status == "pending" || p.status == "disabled") ? Muted : Warning);
                        row.Cells[5].Style.SelectionForeColor = row.Cells[5].Style.ForeColor; row.Cells[5].ToolTipText = p.message ?? "";
                        row.Cells[6].ToolTipText = "更新：" + LocalDate(p.updatedAt, true) + "\r\n最近成功：" + LocalDate(p.lastSuccessAt, true);
                        if (p.current) row.Cells[0].Style.ForeColor = Teal;
                        if (p.status == "ok" && !MissingAccountBalance(p)) good++; else if (p.status != "pending" && p.status != "disabled") needsAttention++;
                        if (String.Equals(p.id, preserveId, StringComparison.Ordinal)) selectedIndex = index; HandleLowBalance(p);
                    }
                _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCellsExceptHeaders;
                _grid.ClearSelection();
                if (_grid.Rows.Count > 0)
                {
                    if (selectedIndex < 0) selectedIndex = 0;
                    _grid.CurrentCell = _grid.Rows[selectedIndex].Cells[0]; _grid.Rows[selectedIndex].Selected = true;
                    if (scroll >= 0 && scroll < _grid.Rows.Count) _grid.FirstDisplayedScrollingRowIndex = scroll;
                    _selectedId = (string)_grid.Rows[selectedIndex].Tag;
                }
                else _selectedId = null;
                _emptyLabel.Text = _hiddenProviders.Count > 0 ? "列表中暂时没有监控项\r\n点击右上“已移出”可恢复配置，继续监控。" : "尚未发现可查询的中转站配置\r\n请先在 CC Switch 中添加 API 配置，再点击立即刷新。";
                _emptyLabel.Visible = _grid.Rows.Count == 0; if (_emptyLabel.Visible) _emptyLabel.BringToFront();
                _summaryLabel.Text = "正在监控 " + _providers.Count.ToString(CultureInfo.InvariantCulture) + " 个配置    ·    " + good.ToString(CultureInfo.InvariantCulture) + " 个已更新" + (needsAttention > 0 ? "    ·    " + needsAttention.ToString(CultureInfo.InvariantCulture) + " 个需关注" : "") + "    ·    ● 当前使用";
                List<string> removed = new List<string>(); foreach (string id in _lastLow.Keys) if (!_providers.ContainsKey(id)) removed.Add(id); foreach (string id in removed) _lastLow.Remove(id);
            }
            finally { _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCellsExceptHeaders; _grid.ResumeLayout(); _updatingGrid = false; }
            string visibilityMessage = null; List<string> completedVisibility = new List<string>();
            HashSet<string> hiddenIds = new HashSet<string>(StringComparer.Ordinal); foreach (HiddenProviderSnapshot hidden in _hiddenProviders) hiddenIds.Add(hidden.id);
            foreach (KeyValuePair<string, bool> pending in _pendingVisibility)
                if (pending.Value ? hiddenIds.Contains(pending.Key) : _providers.ContainsKey(pending.Key))
                {
                    completedVisibility.Add(pending.Key);
                    visibilityMessage = pending.Value ? "已移出列表并停止监控。可从右上“已移出”恢复；CC Switch 配置未改变。" : "已恢复到列表，继续监控。";
                }
            foreach (string id in completedVisibility) _pendingVisibility.Remove(id);
            ShowSelection();
            if (_hiddenDialog != null && !_hiddenDialog.IsDisposed) _hiddenDialog.UpdateProviders(_hiddenProviders, _pendingVisibility);
            _checkedLabel.Text = "最近查询：" + LocalDate(data.checkedAt, true) + "    ·    " + IntervalText(data.intervalSeconds) + "自动刷新    ·    配置变更自动检测";
            if (!SettingsDirty && !_waitingForSettings) SetMessage(!String.IsNullOrWhiteSpace(data.message) ? data.message : (data.refreshing ? "正在查询配置的中转站，请稍候…" : "余额已更新，配置变化会自动检测。"), String.IsNullOrWhiteSpace(data.message) ? Muted : Warning);
            if (visibilityMessage != null) SetMessage(visibilityMessage + (SettingsDirty ? " 未保存的设置已保留。" : ""), Teal);
        }
        private void SetProviderVisibility(string id, bool hidden)
        {
            if (_preview || _stopped || _pendingVisibility.ContainsKey(id)) return;
            if (hidden && !_providers.ContainsKey(id)) return;
            _pendingVisibility[id] = hidden; ShowSelection();
            if (_hiddenDialog != null && !_hiddenDialog.IsDisposed) _hiddenDialog.UpdateProviders(_hiddenProviders, _pendingVisibility);
            SetMessage(hidden ? "正在移出列表；此操作不修改 CC Switch，可随时恢复。" : "正在恢复到列表…", Muted);
            try { _client.SetProviderHidden(id, hidden); } catch { OnErrorReceived("列表更新失败，请重试。"); }
        }
        private void ShowHiddenProviders()
        {
            if (_hiddenDialog != null && !_hiddenDialog.IsDisposed) { _hiddenDialog.Activate(); return; }
            using (HiddenProvidersDialog dialog = new HiddenProvidersDialog(delegate(string id) { SetProviderVisibility(id, false); }))
            {
                _hiddenDialog = dialog;
                try { dialog.UpdateProviders(_hiddenProviders, _pendingVisibility); dialog.ShowDialog(this); }
                finally { _hiddenDialog = null; }
            }
        }
        private void MergeSettings(Snapshot data)
        {
            if (data.providers != null)
                foreach (ProviderSnapshot p in data.providers)
                {
                    if (p == null || String.IsNullOrEmpty(p.id)) continue;
                    if (!_editedThresholds.Contains(p.id)) _thresholds[p.id] = p.threshold;
                    if (!_editedAdapters.Contains(p.id)) _adapters[p.id] = CopyAdapter(p.adapterConfig);
                }
            if (!_intervalDirty)
            {
                _updatingSettings = true;
                try
                {
                    int seconds = data.intervalSeconds >= 60 && data.intervalSeconds <= 86400 ? data.intervalSeconds : 300; int index = -1;
                    for (int i = 0; i < _interval.Items.Count; i++) if (((IntervalItem)_interval.Items[i]).Seconds == seconds) { index = i; break; }
                    if (index < 0) index = _interval.Items.Add(new IntervalItem(seconds)); _interval.SelectedIndex = index;
                }
                finally { _updatingSettings = false; }
            }
        }
        private void SelectGridProvider() { _selectedId = _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Tag as string : null; ShowSelection(); }
        private void ShowSelection()
        {
            ProviderSnapshot p;
            if (_selectedId == null || !_providers.TryGetValue(_selectedId, out p))
            {
                _selectedId = null; _selectedTitle.Text = "选择配置，查看详情和设置"; _threshold.Enabled = false; _adapterButton.Enabled = false; _hideButton.Enabled = false;
                _thresholdUnit.Text = ""; _selectedAdapter.Text = "查询适配：自动检测"; _retryDetectionButton.Visible = false; _adapterButton.Text = "手动配置…";
                _details.Text = "先在 CC Switch 中添加带有 API 地址的配置。无法自动识别的站点可按站点文档设置查询适配。"; return;
            }
            _selectedTitle.Text = p.name + "  /  " + AppLabel(p.app) + (p.current ? "  ·  当前使用" : ""); _tips.SetToolTip(_selectedTitle, _selectedTitle.Text + "\r\n" + p.origin);
            _updatingSettings = true; try { double value; if (_thresholds.TryGetValue(p.id, out value)) SetNumericValue(_threshold, value); } finally { _updatingSettings = false; }
            _threshold.Enabled = !_waitingForSettings; _adapterButton.Enabled = !_waitingForSettings; _thresholdUnit.Text = UnitLabel(p.unit); _hideButton.Enabled = !_pendingVisibility.ContainsKey(p.id);
            _tips.SetToolTip(_threshold, "余额小于或等于此值时提醒。支持 USD、CNY、EUR、GBP、JPY、HKD；原始额度暂不触发金额提醒。设为 0 可停用正余额提醒。");
            AdapterConfig config; _adapters.TryGetValue(p.id, out config);
            _selectedAdapter.Text = "查询适配：" + AdapterName(config == null ? "auto" : config.type) + (_editedAdapters.Contains(p.id) ? "（待保存）" : ""); _tips.SetToolTip(_selectedAdapter, _selectedAdapter.Text);
            bool unresolved = p.status == "unsupported";
            bool missingBalance = MissingAccountBalance(p);
            bool retryable = unresolved || p.status == "error" || p.status == "stale" || (p.status == "ok" && missingBalance);
            bool requesting = _refreshing && _detectingProviderId == p.id;
            _retryDetectionButton.Visible = retryable || requesting;
            _retryDetectionButton.Text = requesting ? (_retrievingBalance ? "正在获取…" : "正在适配…") : (missingBalance ? "获取余额" : "适配");
            _retryDetectionButton.Enabled = retryable && !_waitingForSettings && !_refreshing && !_editedAdapters.Contains(p.id) && !_pendingVisibility.ContainsKey(p.id);
            _tips.SetToolTip(_retryDetectionButton, _editedAdapters.Contains(p.id) ? "查询适配已修改，请先保存设置，再重试。" : (missingBalance ? "单独重试此配置的余额查询；已有手动配置会保留。能否获取账户余额取决于查询凭据的权限。" : "重新检测所选站点；已有手动配置会保留，并按该配置重试。不会执行脚本或登录网站。"));
            _adapterButton.Text = "手动配置…";
            string usageUnit = String.IsNullOrWhiteSpace(p.usageUnit) ? p.unit : p.usageUnit;
            _details.Text = StatusText(p) + (String.IsNullOrWhiteSpace(p.message) ? "" : " · " + p.message) + "\r\n今日实际用量：" + OptionalAmount(p.todayUsage, usageUnit) + "    累计实际用量：" + OptionalAmount(p.totalUsage, usageUnit)
                + "\r\n最近查询：" + LocalDate(p.updatedAt, true) + "    最近成功：" + LocalDate(p.lastSuccessAt, true);
            if (p.status == "ok" && !p.unlimited && !IsAlertCurrency(p.unit)) _details.Text += "\r\n当前单位暂不触发金额提醒；支持 USD、CNY、EUR、GBP、JPY、HKD。";
            if (unresolved) _details.Text = "适配失败，可点击“适配”重试。"
                + "\r\n重新适配会检测已支持的余额协议。API 地址和 API Key 自动读取 CC Switch。"
                + "\r\n已暂停定时适配；配置更改后会自动重新检测。"
                + "\r\n" + (_editedAdapters.Contains(p.id) ? "适配方式已修改，请先保存设置。" : "最近查询：" + LocalDate(p.updatedAt, true));
            else if(retryable) _details.Text = "可点击“适配”重试所选站点，或使用“手动配置…”。已有手动配置会保留。\r\n" + _details.Text;
            if (missingBalance)
                _details.Text = "API Key未设限额，不代表账户资金无限；当前Key未返回账户余额。"
                    + "\r\n可点击“获取余额”重试；读取账户余额需要有效查询凭据，普通 API Key 可能无此权限。"
                    + "\r\n今日实际用量：" + OptionalAmount(p.todayUsage, usageUnit) + "    累计实际用量：" + OptionalAmount(p.totalUsage, usageUnit)
                    + "\r\n最近查询：" + LocalDate(p.updatedAt, true);
            _tips.SetToolTip(_details, _details.Text);
        }
        private void RetryDetection()
        {
            if (_preview || _waitingForSettings || _refreshing || _selectedId == null || _editedAdapters.Contains(_selectedId) || _pendingVisibility.ContainsKey(_selectedId)) return;
            ProviderSnapshot p; if (!_providers.TryGetValue(_selectedId, out p) || (p.status != "unsupported" && p.status != "error" && p.status != "stale" && !(p.status == "ok" && MissingAccountBalance(p)))) return;
            _detectingProviderId = p.id; _retrievingBalance = MissingAccountBalance(p); _refreshing = true; _refreshButton.Enabled = false;
            _refreshButton.Text = _retrievingBalance ? "正在获取…" : "正在适配…"; ShowSelection();
            SetMessage(_retrievingBalance ? "正在单独重试此配置的余额查询，已有手动配置会保留…" : "正在重新检测所选站点，已有手动配置会保留…", Muted);
            try { _client.RetryDetection(_selectedId); } catch { OnErrorReceived("查询未完成，请稍后重试。"); }
        }
        private void ConfigureAdapter()
        {
            if (_selectedId == null || _waitingForSettings) return; ProviderSnapshot provider; if (!_providers.TryGetValue(_selectedId, out provider)) return;
            AdapterConfig config; _adapters.TryGetValue(_selectedId, out config); string editedId = _selectedId;
            AdapterConfig initial = CopyAdapter(config);
            if (provider.status == "unsupported" && initial.type == "auto") initial.type = "custom";
            using (AdapterDialog dialog = new AdapterDialog(provider.name, provider.origin, initial))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _adapters[editedId] = dialog.Config; _editedAdapters.Add(editedId); MarkSettingsDirty(); ShowSelection();
            }
        }
        private static AdapterConfig CopyAdapter(AdapterConfig source)
        {
            if (source == null) return new AdapterConfig { type = "auto" };
            return new AdapterConfig { type = String.IsNullOrEmpty(source.type) ? "auto" : source.type, path = source.path, remainingPath = source.remainingPath, amountMode = source.amountMode, unit = source.unit, balanceKind = source.balanceKind, divisor = source.divisor };
        }
        private void HandleLowBalance(ProviderSnapshot provider)
        {
            if (provider.status == "pending") { _lastLow.Remove(provider.id); return; }
            if (provider.status != "ok") return; bool wasLow; _lastLow.TryGetValue(provider.id, out wasLow); _lastLow[provider.id] = provider.lowBalance && !provider.unlimited;
            if (_preview || wasLow || !provider.lowBalance || provider.unlimited || !_notifications.Checked || _tray == null) return;
            _tray.ShowBalloonTip(7000, provider.name + " 余额不足", "当前余额 " + OptionalAmount(provider.remaining, provider.unit) + "，已达到提醒阈值 " + Amount(provider.threshold, provider.unit) + "。", ToolTipIcon.Warning);
        }
        private bool LoadNotifications()
        {
            try
            {
                string file = Path.Combine(Program.DataDirectory, "ui.json"); if (!File.Exists(file)) return true;
                Dictionary<string, object> values = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file)); object enabled;
                return values == null || !values.TryGetValue("notifications", out enabled) || !(enabled is bool) || (bool)enabled;
            }
            catch { return true; }
        }
        private void SaveNotifications()
        {
            if (_preview) return;
            try
            {
                Directory.CreateDirectory(Program.DataDirectory); string file = Path.Combine(Program.DataDirectory, "ui.json"); string temporary = file + ".tmp";
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(new { notifications = _notifications.Checked }), new UTF8Encoding(false));
                if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file); if (_notifications.Checked) _lastLow.Clear();
                SetMessage(_notifications.Checked ? "已启用桌面提醒，下次查询时检查低余额。" : "已关闭桌面提醒，余额仍会继续刷新。", Teal);
            }
            catch { OnErrorReceived("提醒开关保存失败，下次打开时可能恢复原设置。"); }
        }
        private void OnErrorReceived(string message)
        {
            RunOnUi(delegate { _refreshing = false; _detectingProviderId = null; _retrievingBalance = false; _refreshButton.Enabled = true; _refreshButton.Text = "立即刷新";
                if (_waitingForSettings) { _waitingForSettings = false; SetSettingsEnabled(true); _saveButton.Text = "保存设置"; }
                bool visibilityFailed = _pendingVisibility.Count > 0; _pendingVisibility.Clear();
                if (_hiddenDialog != null && !_hiddenDialog.IsDisposed) { _hiddenDialog.UpdateProviders(_hiddenProviders, _pendingVisibility); if (visibilityFailed) _hiddenDialog.ShowError("恢复未完成，请重新尝试。"); }
                ShowSelection(); SetMessage(String.IsNullOrWhiteSpace(message) ? "操作未完成，请稍后重试。" : message, Warning); });
        }
        private void SetMessage(string message, Color color) { _messageLabel.Text = message; _messageLabel.ForeColor = color; _tips.SetToolTip(_messageLabel, message); }
        private void RunOnUi(Action action)
        {
            if (_stopped || IsDisposed || Disposing) return;
            if (!IsHandleCreated) { if (System.Threading.Thread.CurrentThread.ManagedThreadId == _uiThreadId) action(); return; }
            if (InvokeRequired) { try { BeginInvoke(new MethodInvoker(delegate { if (!_stopped && !IsDisposed) action(); })); } catch (InvalidOperationException) { } return; } action();
        }
        private static void SetNumericValue(NumericUpDown control, double value)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value)) return; control.Value = (decimal)Math.Max((double)control.Minimum, Math.Min((double)control.Maximum, value));
        }
        private static string Money(double value)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value)) return "--";
            return value != 0 && Math.Abs(value) < 0.01 ? value.ToString("G6", CultureInfo.InvariantCulture) : value.ToString("N2", CultureInfo.InvariantCulture);
        }
        private static string UnitLabel(string unit) { return String.IsNullOrWhiteSpace(unit) ? "单位待确认" : unit.Trim(); }
        private static string Amount(double value, string unit) { return Money(value) + (String.IsNullOrWhiteSpace(unit) ? "" : " " + unit.Trim()); }
        private static string OptionalAmount(double? value, string unit) { return value.HasValue ? Amount(value.Value, unit) : "未提供"; }
        private static bool HasReportedAccountBalance(ProviderSnapshot p) { return p.remaining.HasValue && ((!String.IsNullOrEmpty(p.balanceKindLabel) && p.balanceKindLabel.Contains("账户")) || p.adapter == "newapi-account" || p.adapter == "openrouter" || p.adapter == "deepseek" || (p.adapterConfig != null && p.adapterConfig.type == "custom" && p.adapterConfig.balanceKind == "account")); }
        private static bool MissingAccountBalance(ProviderSnapshot p) { return p.unlimited && p.adapter == "newapi-token" && !HasReportedAccountBalance(p); }
        private static string BalanceText(ProviderSnapshot p) { return MissingAccountBalance(p) ? "未获取账户余额" : (p.remaining.HasValue ? Amount(p.remaining.Value, p.unit) : (p.unlimited ? "不限额" : "—")); }
        private static string BalanceKindText(ProviderSnapshot p)
        {
            string label = String.IsNullOrWhiteSpace(p.balanceKindLabel) ? "待确认" : p.balanceKindLabel;
            if (p.remaining.HasValue && !p.unlimited && p.adapterConfig != null && p.adapterConfig.amountMode == "manual" && !label.Contains("手动指定")) label += "（手动指定）";
            return label;
        }
        private static string AppLabel(string value)
        {
            switch ((value ?? "").ToLowerInvariant()) { case "codex": return "Codex"; case "claude": return "Claude"; case "gemini": return "Gemini"; default: return String.IsNullOrEmpty(value) ? "应用未标注" : value; }
        }
        private static string StatusText(ProviderSnapshot p)
        {
            switch (p.status)
            {
                case "ok": return MissingAccountBalance(p) ? "余额未获取" : (p.lowBalance && !p.unlimited ? "达到提醒阈值" : "已更新");
                case "stale": return "查询失败 · 旧余额"; case "pending": return "等待查询"; case "unsupported": return "适配失败";
                case "missing": return "配置不完整"; case "disabled": return "已停用"; default: return "查询失败";
            }
        }
        private static string AdapterDisplay(ProviderSnapshot p) { return !String.IsNullOrWhiteSpace(p.adapterLabel) ? p.adapterLabel : AdapterName(p.adapter); }
        private static bool IsAlertCurrency(string unit) { return unit == "USD" || unit == "CNY" || unit == "EUR" || unit == "GBP" || unit == "JPY" || unit == "HKD"; }
        private static string AdapterName(string type)
        {
            switch (type)
            {
                case "sub2api": return "Sub2API"; case "newapi-token": return "New API 密钥额度"; case "newapi-account": return "New API 账户余额";
                case "openrouter": return "OpenRouter"; case "deepseek": return "DeepSeek"; case "custom": return "自定义接口"; default: return "自动检测";
            }
        }
        private static string LocalDate(string value, bool includeDate)
        {
            DateTimeOffset parsed;
            if (String.IsNullOrEmpty(value) || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed)) return "尚未查询";
            return parsed.ToLocalTime().ToString(includeDate ? "yyyy-MM-dd HH:mm:ss" : "MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        private static string IntervalText(int seconds) { return seconds % 60 == 0 ? "每 " + (seconds / 60).ToString(CultureInfo.InvariantCulture) + " 分钟" : "每 " + seconds.ToString(CultureInfo.InvariantCulture) + " 秒"; }
        private sealed class IntervalItem { public readonly int Seconds; public IntervalItem(int seconds) { Seconds = seconds; } public override string ToString() { return IntervalText(Seconds); } }
        private sealed class BufferedGrid : DataGridView { public BufferedGrid() { DoubleBuffered = true; } }
        private class BorderPanel : Panel
        {
            public BorderPanel() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
            protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using (Pen pen = new Pen(Color.FromArgb(221, 229, 236))) e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1)); }
        }
        private sealed class HiddenProvidersDialog : Form
        {
            private readonly Action<string> _restore;
            private readonly DataGridView _grid;
            private readonly Button _restoreButton;
            private readonly Label _status, _empty;
            private readonly HashSet<string> _restoring = new HashSet<string>(StringComparer.Ordinal);
            private bool _updating;
            public HiddenProvidersDialog(Action<string> restore)
            {
                SuspendLayout(); _restore = restore; Text = "已移出的配置"; Icon = Program.AppIcon; ShowInTaskbar = false;
                StartPosition = FormStartPosition.CenterParent; Font = new Font("Microsoft YaHei UI", 9F); BackColor = Color.FromArgb(244, 247, 250); ForeColor = Ink;
                AutoScaleMode = AutoScaleMode.None; ClientSize = new Size(760, 500); MinimumSize = new Size(680, 420); MinimizeBox = false; MaximizeBox = false;
                TableLayoutPanel layout = new TableLayoutPanel(); layout.Dock = DockStyle.Fill; layout.Padding = new Padding(20); layout.ColumnCount = 1; layout.RowCount = 3;
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64F)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F)); Controls.Add(layout);
                Label help = LabelAt("这些配置已停止在本软件中监控，CC Switch 配置未改变。\r\n选择配置并恢复后，会重新加入列表并查询余额。", 0, 0, 720, 58, 9.5F, FontStyle.Regular, Muted); help.Dock = DockStyle.Fill; help.Margin = Padding.Empty; help.AutoEllipsis = false; layout.Controls.Add(help, 0, 0);
                BorderPanel list = new BorderPanel(); list.Dock = DockStyle.Fill; list.Padding = new Padding(1); list.Margin = new Padding(0, 0, 0, 6); layout.Controls.Add(list, 0, 1);
                _grid = new BufferedGrid(); _grid.Dock = DockStyle.Fill; _grid.BackgroundColor = Color.White; _grid.BorderStyle = BorderStyle.None; _grid.RowHeadersVisible = false; _grid.ReadOnly = true;
                _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false; _grid.AllowUserToResizeRows = false; _grid.MultiSelect = false; _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
                _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCellsExceptHeaders; _grid.EnableHeadersVisualStyles = false;
                _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; _grid.GridColor = Color.FromArgb(235, 240, 243); _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
                _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(226, 245, 240); _grid.DefaultCellStyle.SelectionForeColor = Ink;
                _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(234, 241, 245); _grid.ColumnHeadersDefaultCellStyle.ForeColor = Ink; _grid.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
                _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
                foreach (string heading in new string[] { "配置名称", "应用", "中转站地址" }) { DataGridViewTextBoxColumn column = new DataGridViewTextBoxColumn(); column.HeaderText = heading; column.SortMode = DataGridViewColumnSortMode.NotSortable; column.FillWeight = heading == "应用" ? 20 : 40; column.MinimumWidth = 70; _grid.Columns.Add(column); }
                _grid.SelectionChanged += delegate { if (!_updating) UpdateSelection(); }; list.Controls.Add(_grid);
                _empty = LabelAt("暂无已移出的配置", 0, 0, 600, 80, 11F, FontStyle.Regular, Muted); _empty.Dock = DockStyle.Fill; _empty.TextAlign = ContentAlignment.MiddleCenter; _empty.Visible = false; list.Controls.Add(_empty);
                TableLayoutPanel actions = new TableLayoutPanel(); actions.Dock = DockStyle.Fill; actions.Margin = Padding.Empty; actions.ColumnCount = 3; actions.RowCount = 1;
                actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 156F)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112F)); actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); layout.Controls.Add(actions, 0, 2);
                _status = LabelAt("", 0, 0, 390, 40, 9F, FontStyle.Regular, Muted); _status.Dock = DockStyle.Fill; _status.Margin = Padding.Empty; actions.Controls.Add(_status, 0, 0);
                _restoreButton = ActionButton("恢复到列表", 0, 0, 144, 36); _restoreButton.Dock = DockStyle.Fill; _restoreButton.Margin = new Padding(6, 10, 6, 8); _restoreButton.Enabled = false;
                _restoreButton.Click += delegate { if (_grid.SelectedRows.Count > 0) { string id = _grid.SelectedRows[0].Tag as string; if (id != null && !_restoring.Contains(id)) _restore(id); } }; actions.Controls.Add(_restoreButton, 1, 0);
                Button close = SecondaryButton("关闭", 0, 0, 100, 36); close.Dock = DockStyle.Fill; close.Margin = new Padding(6, 10, 0, 8); close.DialogResult = DialogResult.Cancel; actions.Controls.Add(close, 2, 0); CancelButton = close;
                AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96); ResumeLayout(true); PerformAutoScale();
                float scale; using (Graphics graphics = CreateGraphics()) scale = graphics.DpiY / 96F;
                _grid.RowTemplate.MinimumHeight = (int)Math.Ceiling(60F * scale); _grid.RowTemplate.Height = _grid.RowTemplate.MinimumHeight; _grid.ColumnHeadersHeight = (int)Math.Ceiling(38F * scale);
                _grid.DefaultCellStyle.Padding = new Padding((int)(10 * scale), (int)(10 * scale), (int)(6 * scale), (int)(10 * scale));
                _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding((int)(10 * scale), 0, 0, 0);
                foreach (DataGridViewColumn column in _grid.Columns) column.MinimumWidth = (int)Math.Ceiling(column.MinimumWidth * scale);
                Rectangle area = Screen.FromControl(this).WorkingArea; Size bounded = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height)); MinimumSize = new Size(Math.Min(MinimumSize.Width, bounded.Width), Math.Min(MinimumSize.Height, bounded.Height)); Size = bounded;
            }
            public void UpdateProviders(IList<HiddenProviderSnapshot> providers, IDictionary<string, bool> pending)
            {
                string selected = _grid.SelectedRows.Count == 0 ? null : _grid.SelectedRows[0].Tag as string; _restoring.Clear();
                foreach (KeyValuePair<string, bool> item in pending) if (!item.Value) _restoring.Add(item.Key);
                _updating = true; _grid.SuspendLayout(); _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
                try
                {
                    _grid.Rows.Clear(); int selectedIndex = 0;
                    foreach (HiddenProviderSnapshot provider in providers)
                    {
                        int index = _grid.Rows.Add(provider.name, AppLabel(provider.app), provider.origin); _grid.Rows[index].Tag = provider.id;
                        if (provider.id == selected) selectedIndex = index;
                    }
                    _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCellsExceptHeaders;
                    if (_grid.Rows.Count > 0) { _grid.CurrentCell = _grid.Rows[selectedIndex].Cells[0]; _grid.Rows[selectedIndex].Selected = true; }
                    _empty.Visible = _grid.Rows.Count == 0; if (_empty.Visible) _empty.BringToFront();
                    _status.Text = "已移出 " + providers.Count.ToString(CultureInfo.InvariantCulture) + " 个配置"; _status.ForeColor = Muted;
                }
                finally { _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCellsExceptHeaders; _grid.ResumeLayout(); _updating = false; }
                UpdateSelection();
            }
            public void ShowError(string message) { _status.Text = message; _status.ForeColor = Warning; }
            private void UpdateSelection()
            {
                string id = _grid.SelectedRows.Count == 0 ? null : _grid.SelectedRows[0].Tag as string; bool waiting = id != null && _restoring.Contains(id);
                _restoreButton.Enabled = id != null && !waiting; _restoreButton.Text = waiting ? "正在恢复…" : "恢复到列表";
            }
        }
        private sealed class AdapterDialog : Form
        {
            private readonly ComboBox _type, _kind, _amountMode;
            private readonly TextBox _path, _remainingPath, _unit;
            private readonly NumericUpDown _divisor;
            private readonly Panel _custom, _amount;
            private readonly Label _error;
            private bool _nonCustomManual, _updatingMode;
            public AdapterConfig Config { get; private set; }
            public AdapterDialog(string name, string origin, AdapterConfig config)
            {
                SuspendLayout();
                Text = "查询适配 · " + name; Icon = Program.AppIcon; FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
                Font = new Font("Microsoft YaHei UI", 9F); BackColor = Color.White; ForeColor = Ink; AutoScaleMode = AutoScaleMode.None; ClientSize = new Size(628, 650);
                Controls.Add(LabelAt("设置此配置的余额查询方式", 24, 17, 580, 32, 15F, FontStyle.Bold, Ink));
                Controls.Add(LabelAt(origin ?? "地址未配置", 25, 54, 575, 25, 9F, FontStyle.Regular, Muted));
                Label note = LabelAt("API 地址和 API Key 自动读取 CC Switch，无需再次填写。\r\n只有“自定义 JSON”需要填写路径和字段；金额规则可单独设置。", 25, 92, 575, 48, 9F, FontStyle.Regular, Muted); note.AutoEllipsis = false; Controls.Add(note);
                Controls.Add(LabelAt("查询方式", 25, 148, 135, 27, 9F, FontStyle.Regular, Ink));
                _type = new ComboBox(); _type.Location = new Point(163, 148); _type.Size = new Size(432, 30); _type.DropDownStyle = ComboBoxStyle.DropDownList;
                foreach (string type in new string[] { "auto", "sub2api", "newapi-token", "newapi-account", "openrouter", "deepseek", "custom" }) _type.Items.Add(new AdapterItem(type));
                _type.SelectedIndex = 0; for (int i = 0; i < _type.Items.Count; i++) if (((AdapterItem)_type.Items[i]).Type == config.type) _type.SelectedIndex = i; Controls.Add(_type);
                _custom = new Panel(); _custom.Location = new Point(25, 190); _custom.Size = new Size(570, 81); Controls.Add(_custom);
                _custom.Controls.Add(LabelAt("GET 接口路径", 0, 0, 134, 27, 9F, FontStyle.Regular, Ink));
                _path = Field(config.path ?? "", 138, 0, 432); _custom.Controls.Add(_path);
                _custom.Controls.Add(LabelAt("JSON 余额字段", 0, 43, 134, 27, 9F, FontStyle.Regular, Ink));
                _remainingPath = Field(config.remainingPath ?? "", 138, 43, 432); _custom.Controls.Add(_remainingPath);
                Controls.Add(LabelAt("金额规则", 25, 281, 134, 27, 9F, FontStyle.Regular, Ink));
                _amountMode = new ComboBox(); _amountMode.Location = new Point(163, 281); _amountMode.Size = new Size(432, 30); _amountMode.DropDownStyle = ComboBoxStyle.DropDownList;
                _amountMode.Items.AddRange(new object[] { "按接口自动读取", "手动设置单位、除数和余额范围" }); _amountMode.SelectedIndex = 0; Controls.Add(_amountMode);
                _nonCustomManual = config.type != "custom" && config.amountMode == "manual";
                _amount = new Panel(); _amount.Location = new Point(25, 324); _amount.Size = new Size(570, 147); Controls.Add(_amount);
                _amount.Controls.Add(LabelAt("金额单位", 0, 0, 134, 27, 9F, FontStyle.Regular, Ink));
                _unit = Field(config.unit ?? "", 138, 0, 130); _amount.Controls.Add(_unit);
                _amount.Controls.Add(LabelAt("换算除数", 284, 0, 103, 27, 9F, FontStyle.Regular, Ink));
                _divisor = new NumericUpDown(); _divisor.Location = new Point(389, 0); _divisor.Size = new Size(181, 27);
                _divisor.DecimalPlaces = 6; _divisor.Minimum = 0.000001M; _divisor.Maximum = 1000000000000000M; _divisor.Value = 1M; _divisor.ThousandsSeparator = true;
                if (config.divisor.HasValue) SetNumericValue(_divisor, config.divisor.Value); _amount.Controls.Add(_divisor);
                _amount.Controls.Add(LabelAt("余额范围", 0, 43, 134, 27, 9F, FontStyle.Regular, Ink));
                _kind = new ComboBox(); _kind.Location = new Point(138, 43); _kind.Size = new Size(432, 30); _kind.DropDownStyle = ComboBoxStyle.DropDownList;
                _kind.Items.AddRange(new object[] { "额度（quota）", "密钥余额（key）", "账户余额（account）" }); _kind.SelectedIndex = config.balanceKind == "account" ? 2 : (config.balanceKind == "key" ? 1 : 0); _amount.Controls.Add(_kind);
                Label help = LabelAt("单位示例：USD、CNY、quota；显示余额 = 返回的原始余额 ÷ 除数。\r\n手动余额范围会标注“手动指定”；不能将未返回的余额变成金额。\r\n规则仅换算余额；实际用量保留接口报告的单位。", 0, 85, 570, 62, 8.5F, FontStyle.Regular, Muted); help.AutoEllipsis = false; _amount.Controls.Add(help);
                Label authHelp = LabelAt("New API 账户余额需要 CC Switch 中同站点的 accessToken 和 userId；普通 API Key 可能无此权限。\r\n自定义接口示例：/api/balance，字段 data.balance。仅支持同站点 GET + API Key；要求 Cookie、网页登录或 POST 的接口暂不支持。", 25, 481, 575, 75, 8.5F, FontStyle.Regular, Muted); authHelp.AutoEllipsis = false; Controls.Add(authHelp);
                _error = LabelAt("", 25, 559, 575, 32, 8.5F, FontStyle.Regular, Warning); _error.AutoEllipsis = false; Controls.Add(_error);
                Button cancel = new Button(); cancel.Text = "取消"; cancel.Location = new Point(315, 595); cancel.Size = new Size(109, 34); cancel.DialogResult = DialogResult.Cancel; Controls.Add(cancel); CancelButton = cancel;
                Button apply = ActionButton("应用到待保存设置", 437, 595, 164, 34); apply.Font = new Font(Font, FontStyle.Bold); apply.Click += delegate { Apply(); }; Controls.Add(apply); AcceptButton = apply;
                _type.SelectedIndexChanged += delegate { UpdateMode(); };
                _amountMode.SelectedIndexChanged += delegate { if (!_updatingMode) { _nonCustomManual = _amountMode.SelectedIndex == 1; UpdateAmountControls(); } }; UpdateMode();
                AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96); ResumeLayout(true); PerformAutoScale();
                Rectangle area = Screen.FromControl(this).WorkingArea;
                if (Width > area.Width || Height > area.Height)
                {
                    Size contents = ClientSize; AutoScroll = true; AutoScrollMinSize = contents;
                    Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
                }
            }
            private static TextBox Field(string value, int x, int y, int width) { TextBox box = new TextBox(); box.Text = value; box.Location = new Point(x, y); box.Size = new Size(width, 27); return box; }
            private void UpdateMode()
            {
                bool custom = ((AdapterItem)_type.SelectedItem).Type == "custom"; _custom.Enabled = custom;
                _updatingMode = true; try { _amountMode.SelectedIndex = custom || _nonCustomManual ? 1 : 0; _amountMode.Enabled = !custom; } finally { _updatingMode = false; }
                UpdateAmountControls();
            }
            private void UpdateAmountControls() { _amount.Enabled = _amountMode.SelectedIndex == 1; if (_error != null) _error.Text = ""; }
            private void Apply()
            {
                string type = ((AdapterItem)_type.SelectedItem).Type;
                bool manual = type == "custom" || _amountMode.SelectedIndex == 1;
                if (!manual) { Config = new AdapterConfig { type = type, amountMode = "auto" }; DialogResult = DialogResult.OK; Close(); return; }
                string path = _path.Text.Trim(); string remaining = _remainingPath.Text.Trim(); string unit = _unit.Text.Trim();
                if (type == "custom" && (!Regex.IsMatch(path, "^/[A-Za-z0-9_./-]*$") || path.StartsWith("//", StringComparison.Ordinal) || path.Length > 256 || Array.Exists(path.Split('/'), delegate(string part) { return part == "." || part == ".."; })))
                { _error.Text = "接口须为单个 / 开头的站内路径，仅含字母、数字、下划线、横线和点，不能含查询参数或跳转。"; _path.Focus(); return; }
                if (type == "custom" && (!Regex.IsMatch(remaining, "^[A-Za-z_][A-Za-z0-9_]*(\\.(?:[A-Za-z_][A-Za-z0-9_]*|[0-9]+))*$") || remaining.Length > 200 || Array.Exists(remaining.Split('.'), delegate(string part) { return part == "__proto__" || part == "constructor" || part == "prototype"; })))
                { _error.Text = "请填写点分隔的 JSON 字段路径，例如 data.balance。"; _remainingPath.Focus(); return; }
                if (!Regex.IsMatch(unit, "^(?:[A-Za-z]{2,12}|[¥$€£]|人民币|积分|点数)$")) { _error.Text = "请明确填写单位，例如 USD、CNY、quota 或积分。"; _unit.Focus(); return; }
                Config = new AdapterConfig { type = type, amountMode = type == "custom" ? null : "manual", path = type == "custom" ? path : null, remainingPath = type == "custom" ? remaining : null, unit = unit, divisor = (double)_divisor.Value, balanceKind = _kind.SelectedIndex == 2 ? "account" : (_kind.SelectedIndex == 1 ? "key" : "quota") };
                DialogResult = DialogResult.OK; Close();
            }
            private sealed class AdapterItem { public readonly string Type; public AdapterItem(string type) { Type = type; } public override string ToString() { return AdapterName(Type); } }
        }
    }
}
