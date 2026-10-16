using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WindowsDuo.Core;

namespace WindowsDuo.App;

public sealed class MainForm : Form
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private readonly GlassRenderer _renderer = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly PhoneAngleLink _link;
    private readonly DisplayMetrics.EyeDistance _eye = DisplayMetrics.Derive();
    private readonly GlassPanel _panel;
    private readonly LiveController _live;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 33 };
    private readonly List<GlassCard> _cards = new();

    private readonly GlassLabel _status = new() { AutoSize = false, ForeColor = Theme.TextDim, Font = Theme.FontSmall };
    private readonly GlassLabel _valueReadout = new() { AutoSize = true, ForeColor = Theme.TextDim, Font = Theme.FontSmall };
    private readonly GlassSlider _strength = new() { Minimum = 50, Maximum = 200, Value = 100, Width = 250 };
    private readonly GlassSlider _frost = new() { Minimum = 0, Maximum = 18, Value = 9, Width = 250 };
    private readonly GlassSlider _glow = new() { Minimum = 0, Maximum = 30, Value = 6, Width = 250 };
    private readonly GlassSlider _manual = new() { Minimum = 0, Maximum = 180, Value = 120, Width = 250 };
    private readonly GlassCheck _usePhone = new() { Text = "使用手机角度（TCP）", AutoSize = true, Checked = true, ForeColor = Theme.TextDim, Font = Theme.FontUi, FlatStyle = FlatStyle.Flat };
    private readonly TextBox _phoneBox = new() { Width = 160, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(22, 24, 30), ForeColor = Theme.Text, Font = Theme.FontUi };
    private readonly GlassButton _liveButton;
    private readonly GlassButton _importButton;
    private readonly GlassButton _connectButton;
    private readonly NotifyIcon _tray = new() { Visible = true, Text = "Windows Duo" };
    private readonly TrafficLights _lights = new();
    private readonly HeaderText _header = new();
    private FlowLayoutPanel? _stack;

    private const int WsMinimizeBox = 0x00020000, WsMaximizeBox = 0x00010000;
    private const int WmNcHitTest = 0x0084, WmGetMinMaxInfo = 0x0024;
    private const int DwmwaWindowCornerPreference = 33, DwmcpRound = 2;

    // No WS_THICKFRAME. DWM paints the frame of a thick-framed window in the
    // user's accent colour, which drew a 9px band of solid accent around the page
    // no matter what the window drew underneath. Resizing and maximising are done
    // by hand instead, and the corners are asked for explicitly because a frameless
    // window is square by default.
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= WsMinimizeBox | WsMaximizeBox;
            return cp;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public WinPoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    private string? _imagePath;
    private string? _loadError;
    private bool _shotMode;
    private bool _wasLiveRunning;
    private DesktopBackdrop? _backdrop;

    // The capture runs on its own thread; the paint that consumes it has to run
    // on this one. The block is a child, so invalidating this form does not reach
    // it - and its glass is drawn against the same backdrop, which just moved.
    private void RepaintFromCapture()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                Invalidate();
                PageGlass.InvalidateSurfaces();
            }));
        }
        catch (InvalidOperationException) { /* handle went away mid-post */ }
    }

    public MainForm()
    {
        Text = "Windows Duo";
        BackColor = Theme.ChromeSolid;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.Manual;
        // Borderless, with the resize frame kept so the window can still be
        // dragged by its edges; the header becomes the drag handle via
        // WM_NCHITTEST, and macOS-style dots replace the system buttons.
        FormBorderStyle = FormBorderStyle.None;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        _link = new PhoneAngleLink
        {
            Host = _settings.PhoneHost,
            Port = _settings.AnglePort,
            UseManual = false,
            ManualAngle = Mapping.OpenAngle,
        };
        _panel = new GlassPanel(_renderer) { Dock = DockStyle.Fill };
        _liveButton = new GlassButton { Text = "启用实时桌面效果", Prominent = true, Width = 250 };
        _importButton = new GlassButton { Text = "插入图片", Width = 250, Height = 36 };
        _importButton.Click += (_, _) => ImportScreenshot();
        _connectButton = new GlassButton { Text = "连接", Width = 250, Height = 36 };
        _connectButton.Click += (_, _) => ConnectPhone();
        _live = new LiveController(_renderer, _link, _settings) { Eye = _eye.Eye };

        _strength.Value = (int)Math.Round(_settings.Strength * 100);
        _frost.Value = (int)Math.Round(_settings.Frost * 100);
        _glow.Value = (int)Math.Round(_settings.Glow * 100);

        Controls.Add(BuildLayout());

        _lights.CloseRequested += Close;
        _lights.MinimizeRequested += () => WindowState = FormWindowState.Minimized;
        _lights.MaximizeRequested += () =>
        {
            WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal
                : FormWindowState.Maximized;
            _lights.IsMaximized = WindowState == FormWindowState.Maximized;
            _lights.Invalidate();
        };
        Resize += (_, _) =>
        {
            PositionLights();
            // The window can also be maximised from outside the dots (double-click
            // on the caption band, Win+Up), so the glyph has to follow the state.
            bool max = WindowState == FormWindowState.Maximized;
            if (_lights.IsMaximized != max)
            {
                _lights.IsMaximized = max;
                _lights.Invalidate();
            }
        };
        Controls.Add(_header);
        Controls.Add(_lights);
        _lights.BringToFront();
        _header.BringToFront();

        // Typing the address only records it. Connecting is the button's job: a
        // half-typed address should not start a connection attempt, and the phone
        // side has to be told to listen.
        _phoneBox.Text = _settings.PhoneHost;
        _phoneBox.TextChanged += (_, _) =>
        {
            _settings.PhoneHost = _phoneBox.Text.Trim();
            _settings.Save();
        };

        _liveButton.Click += (_, _) => _live.Toggle();
        _live.Owner = this;
        _live.StateChanged += () => { if (InvokeRequired) BeginInvoke(UpdateLiveUi); else UpdateLiveUi(); };

        _tray.Icon = System.Drawing.SystemIcons.Application;
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("显示设置窗口", null, (_, _) => RestoreWindow());
        trayMenu.Items.Add("停止实时效果", null, (_, _) => _live.Stop());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("退出", null, (_, _) => Close());
        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => RestoreWindow();

        _tick.Tick += (_, _) => UpdateFrame();
        _tick.Start();
        _panel.Start();

        // Without a source the canvas renders nothing and reads as a black hole
        // inside the glass, so give the effect the built-in backdrop to work on
        // until a screenshot is imported.
        if (!_renderer.HasSource)
        {
            var seed = Theme.Backdrop(new System.Drawing.Size(1600, 1000));
            var (rgba, w, h) = ImageSource.FromBitmap(seed);
            _renderer.SetSource(rgba, w, h, mips: true);
        }

        FormClosed += (_, _) =>
        {
            _settings.Save();
            _backdrop?.Dispose();
            _live.Dispose();
            _link.Dispose();
            _renderer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        };
    }

    // The page is one pane of glass over whatever is behind the window. Until the
    // first frame of that arrives - and if the capture is unavailable - it falls
    // back to the generated colour field, which is also what the shot harness
    // renders, so a screenshot stays reproducible.
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        UploadBackdropFrame();
        Theme.PaintBackdropFor(this, e.Graphics);
    }

    // The window itself, the containers that paint the page as their background,
    // and the controls drawn straight onto it. The cards are left out on purpose:
    // they are opaque, they only ever show the page in the sliver outside their
    // rounded corners, and repainting a dozen cards every captured frame is what
    // makes a frame expensive.
    private Control[] BuildSurfaces()
    {
        var list = new List<Control> { this, _header, _lights };
        if (_hero is not null) list.Add(_hero);
        if (_body is not null) list.Add(_body);
        if (_stack is not null) list.Add(_stack);

        // There are no cards any more, only text and controls drawn straight on the
        // glass, and they are transparent: all of it has to be drawn again when the
        // page changes.
        foreach (var card in _cards) AddDescendants(list, card);
        return list.ToArray();
    }

    private static void AddDescendants(List<Control> list, Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            list.Add(child);
            AddDescendants(list, child);
        }
    }

    // Backdrop diagnostics, for --page-probe.
    public bool BackdropExclusionOk => _backdrop?.ExclusionOk ?? false;
    public int BackdropFrames => _backdrop?.Frames ?? 0;

    // Hands the newest captured desktop frame to the renderer on this thread: the
    // D3D context is shared with the slab draws and is not thread-safe.
    private void UploadBackdropFrame()
    {
        if (_backdrop is null || !_backdrop.TryTake(out var frame)) return;
        try
        {
            PageGlass.SetDesktopFrame(frame.Bits, frame.Width, frame.Height);
        }
        finally
        {
            _backdrop.Release(frame);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
    }

    // The cards are built at (0,0) and moved into place by the flow layout; with
    // a clipping region on each card the stale first paint is not always cleared,
    // so force one full repaint once the tree and the window size are final.
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var wa = Screen.FromControl(this).WorkingArea;
        float scale = DeviceDpi / 96f;
        int w = Math.Min((int)(1180 * scale), wa.Width - 40);
        int h = Math.Min((int)(680 * scale), wa.Height - 40);
        // The column has to fit at the smallest allowed window, so the minimum is
        // the height it needs, not half the default.
        MinimumSize = new System.Drawing.Size(Theme.Scaled(520, scale), Theme.Scaled(660, scale));
        ClientSize = new System.Drawing.Size(w, h);
        _lights.ApplyDpi();
        PositionLights();
        _header.Location = new System.Drawing.Point(Theme.Scaled(26, scale), Theme.Scaled(52, scale));
        _header.Size = new System.Drawing.Size(Theme.Scaled(460, scale), Theme.Scaled(80, scale));
        if (_body is not null)
            _body.Padding = new Padding(Theme.Scaled(18, scale), Theme.Scaled(116, scale),
                                        Theme.Scaled(18, scale), Theme.Scaled(18, scale));

        // Everything that shows the page has to be told when the page changes,
        // and the cards are slabs drawn into it.
        PageGlass.SetPageSurfaces(BuildSurfaces());
        ApplyMinimumHeight();
        if (ClientSize.Height < MinimumSize.Height - (Height - ClientSize.Height))
            ClientSize = new System.Drawing.Size(ClientSize.Width, MinimumSize.Height - (Height - ClientSize.Height));

        // Started here rather than when the handle was created: the window is now
        // the size it will keep, so the first captured frame already matches it.
        // Started earlier, the page spent the first frames stretching an older,
        // differently sized frame over the window, which reads as a blur.
        if (!_shotMode)
        {
            _backdrop = new DesktopBackdrop(Handle, RepaintFromCapture);
            _backdrop.Start();
        }

        Location = new System.Drawing.Point(
            wa.Left + (wa.Width - Width) / 2,
            wa.Top + (wa.Height - Height) / 2);
        Invalidate(true);
        Refresh();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        PageGlass.Note($"window: handle created, client={ClientSize} dpi={DeviceDpi}");

        int corners = DwmcpRound;
        DwmSetWindowAttribute(Handle, DwmwaWindowCornerPreference, ref corners, sizeof(int));

        Invalidate(true);

    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0005) // WM_SIZE
            PageGlass.Note($"window: size {ClientSize} state={WindowState}");

        if (m.Msg == WmGetMinMaxInfo)
        {
            // A frameless window would otherwise maximise over the taskbar.
            var screen = Screen.FromControl(this);
            var area = screen.WorkingArea;
            var bounds = screen.Bounds;
            var mmi = Marshal.PtrToStructure<MinMaxInfo>(m.LParam);
            mmi.MaxPosition = new WinPoint { X = area.Left - bounds.Left, Y = area.Top - bounds.Top };
            mmi.MaxSize = new WinPoint { X = area.Width, Y = area.Height };
            mmi.MinTrackSize = new WinPoint { X = MinimumSize.Width, Y = MinimumSize.Height };
            Marshal.StructureToPtr(mmi, m.LParam, false);
            return;
        }

        // Frameless window: the header band doubles as the caption so the window
        // can be dragged and double-clicked, and the resize edges are reported by
        // hand because there is no frame left to do it.
        if (m.Msg == WmNcHitTest)
        {
            base.WndProc(ref m);
            if (m.Result == (IntPtr)1)
            {
                int lp = m.LParam.ToInt32();
                var p = PointToClient(new System.Drawing.Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                int grip = Theme.Scaled(6, Theme.ScaleFor(this));
                // A maximised window has no size to drag to: reporting the edges as
                // resize borders there let a grab on the frame resize the maximised
                // window instead of restoring it.
                bool sized = WindowState != FormWindowState.Maximized;
                bool left = sized && p.X < grip, right = sized && p.X >= ClientSize.Width - grip;
                bool top = sized && p.Y < grip, bottom = sized && p.Y >= ClientSize.Height - grip;

                if (top && left) m.Result = (IntPtr)13;
                else if (top && right) m.Result = (IntPtr)14;
                else if (bottom && left) m.Result = (IntPtr)16;
                else if (bottom && right) m.Result = (IntPtr)17;
                else if (left) m.Result = (IntPtr)10;
                else if (right) m.Result = (IntPtr)11;
                else if (top) m.Result = (IntPtr)12;
                else if (bottom) m.Result = (IntPtr)15;
                // The band the title occupies, so the whole header drags the window.
                else if (p.Y < Theme.Scaled(136, Theme.ScaleFor(this))) m.Result = (IntPtr)2;
            }
            return;
        }
        base.WndProc(ref m);
    }

    // ---------------------------------------------------------------- layout

    private Control BuildLayout()
    {
        _body = BuildBody();
        _body.Padding = new Padding(18, 116, 18, 18);
        return _body;
    }

    private Control? _body;
    private HeroPanel? _hero;

    private Control BuildBody()
    {
        var body = new BackdropTable
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.ChromeSolid,
            Padding = new Padding(18, 0, 18, 18),
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 63));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));

        _hero = new HeroPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 12, 0) };
        _hero.Controls.Add(_panel);
        PageGlass.SetBlockControl(_hero);

        _stack = new BackdropFlow
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = false,
            BackColor = Theme.ChromeSolid,
            Margin = new Padding(0),
        };
        BuildCards();
        _stack.SizeChanged += (_, _) => ResizeCards();

        body.Controls.Add(_hero, 0, 0);
        body.Controls.Add(_stack, 1, 0);
        return body;
    }

    private void BuildCards()
    {
        _cards.Add(Card("角度来源（手机）",
            Hint("在 DUO app 打开开关后，界面会显示本机地址"),
            FieldRow("手机 IP", _phoneBox, 28),
            _connectButton));

        _cards.Add(Card("内容",
            _importButton));

        _cards.Add(Card("参数",
            Slider("深度强度", _strength, v => $"{v / 100.0:0.00}×"),
            Slider("磨砂", _frost, v => $"{v / 100.0:0.00}"),
            Slider("边沿泛光", _glow, v => $"{v / 100.0:0.00}"),
            Slider("模拟角度", _manual, v => $"{v}°"),
            _usePhone,
            _valueReadout));

        _cards.Add(Card("实时桌面",
            _liveButton));

        _status.Height = Theme.Scaled(62, Theme.ScaleFor(this));
        _cards.Add(Card("状态", _status));

        foreach (var card in _cards) _stack!.Controls.Add(card);
        ResizeCards();
    }

    /// The column does not scroll, so the window may not be made shorter than the
    /// column. Measured rather than guessed, because the content is what it is.
    private void ApplyMinimumHeight()
    {
        if (_stack is null || _cards.Count == 0) return;
        float scale = Theme.ScaleFor(this);
        int content = 0;
        foreach (var card in _cards) content += card.Height + card.Margin.Vertical;
        // The column's own margin plus the body's padding below it have to fit too,
        // or the last card's bottom edge is clipped by a hair.
        int slack = Theme.Scaled(8, scale) + Theme.Scaled(18, scale) * 2;
        MinimumSize = new System.Drawing.Size(MinimumSize.Width, content + _stack.Top + slack);
    }

    private void ResizeCards()
    {
        if (_stack is null) return;
        int width = _stack.ClientSize.Width - 6;
        if (width <= 0) return;
        foreach (var card in _cards) card.Width = width;
    }

    // Card height is derived from its content: a fixed height let the body (which
    // is taller than the card) spill over the next card, since WinForms does not
    // clip children to their parent.
    // Children go straight onto the card with explicit positions. A nested
    // container here is what made the card contents ghost on screen.
    private GlassCard Card(string title, params Control[] children)
    {
        float scale = Theme.ScaleFor(this);
        var card = new GlassCard { Margin = new Padding(0, 0, 0, Theme.Scaled(6, scale)) };
        card.Controls.Add(new GlassLabel
        {
            Text = title, Font = Theme.FontBold, ForeColor = Theme.Text,
            AutoSize = true,
        });
        var title1 = card.Controls[0];
        int inset = Theme.Scaled(22, scale);
        title1.Location = new System.Drawing.Point(inset, Theme.Scaled(16, scale));

        int y = title1.Bottom + Theme.Scaled(12, scale);
        foreach (var c in children)
        {
            c.Location = new System.Drawing.Point(inset, y);
            c.Width = Math.Max(60, card.Width - inset * 2);
            card.Controls.Add(c);
            y += c.Height + Theme.Scaled(4, scale);
        }

        card.Height = y + Theme.Scaled(8, scale);
        card.Resize += (_, _) =>
        {
            foreach (Control c in card.Controls)
                if (c is not Label l || l.Text != title) c.Width = Math.Max(60, card.ClientSize.Width - inset * 2);
        };
        return card;
    }

    /// Two lines of room: the narrowest column fits about half of this sentence
    /// per line.
    private GlassHint Hint(string text) => new()
    {
        Text = text, Height = Theme.Scaled(30, Theme.ScaleFor(this)), Width = 260,
    };

    private Control FieldRow(string caption, Control field, int height)
    {
        var row = new GlassRow { Height = Theme.Scaled(height, Theme.ScaleFor(this)), Width = 260 };
        row.Controls.Add(new GlassLabel
        {
            Text = caption, Font = Theme.FontUi, ForeColor = Theme.TextDim,
            AutoSize = true, Location = new System.Drawing.Point(0, 5),
        });
        field.Location = new System.Drawing.Point(72, 1);
        row.Controls.Add(field);
        // The column is only ~200px wide at the smallest window, and a field wider
        // than its row is not clipped - it just runs out of the card.
        row.Resize += (_, _) => field.Width = Math.Clamp(
            row.ClientSize.Width - field.Left - 2, 60, 160);
        return row;
    }

    /// The slider draws its own caption and value. A row built out of a panel and
    /// two labels would be four transparent controls to repaint every time the
    /// glass behind them moves; this is one.
    private Control Slider(string caption, GlassSlider slider, Func<int, string> fmt)
    {
        slider.Caption = caption;
        slider.Format = fmt;
        slider.Width = 260;
        slider.Height = Theme.Scaled(38, Theme.ScaleFor(this));
        return slider;
    }

    // ---------------------------------------------------------------- actions

    /// Anchors the window controls to the top-right corner, where the system
    /// buttons would be.
    private void PositionLights()
    {
        float scale = Theme.ScaleFor(this);
        int margin = Theme.Scaled(22, scale);
        _lights.Location = new System.Drawing.Point(
            Math.Max(margin, ClientSize.Width - _lights.Width - margin),
            Theme.Scaled(20, scale));
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void UpdateLiveUi()
    {
        bool running = _live.Running;
        _liveButton.Text = running ? "停止实时桌面效果" : "启用实时桌面效果";
        _tray.Text = running ? "Windows Duo · 实时中" : "Windows Duo";

        // The setup window can disappear while the effect covers the screen, so
        // say out loud where to go to get it back.
        if (running && !_wasLiveRunning)
            _tray.ShowBalloonTip(5000, "Windows Duo 实时效果",
                "已开启。双击托盘图标取回设置窗口，或在托盘菜单里停止。",
                ToolTipIcon.Info);
        _wasLiveRunning = running;
    }

    internal LiveController Live => _live;

    internal void PrepareShot(string imagePath, int manualAngle, bool keystone)
    {
        _shotMode = true;
        LoadImage(imagePath);
        _manual.Value = Math.Clamp(manualAngle, 0, 180);
        _usePhone.Checked = false;
        // The keystone model is no longer a UI choice; the shot harness still asks
        // for it directly.
        _renderer.Model = keystone ? GlassModel.Keystone : GlassModel.EyePoint;
    }

    /// Used by --shot to take the window out of its periodic repaint, so a capture
    /// cannot land on a half-updated surface.
    internal void FreezeForShot()
    {
        UpdateFrame();
        _tick.Stop();
        Invalidate(true);
        Update();
    }

    internal string PanelDiagnostics =>
        $"swapchain={_panel.SwapChainReady} hasSource={_renderer.HasSource} " +
        $"source={_renderer.SourceWidth}x{_renderer.SourceHeight} " +
        $"panelSize={_panel.ClientSize.Width}x{_panel.ClientSize.Height} " +
        $"renders={_panel.RenderCalls} presents={_panel.PresentCalls} " +
        $"image={_imagePath ?? "-"} loadError={_loadError ?? "-"} err={_panel.LastError ?? "-"}";

    // Walks the real control tree. Reading positions off a scaled screenshot got
    // the diagnosis wrong twice; this is the ground truth.
    internal string LayoutDump()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"form client={ClientSize} dpi={DeviceDpi}");
        sb.AppendLine($"lights bounds={_lights.Bounds} visible={_lights.Visible} parent={_lights.Parent?.GetType().Name}");
        sb.AppendLine($"header bounds={_header.Bounds} visible={_header.Visible}");
        foreach (var card in _cards)
        {
            sb.AppendLine($"card '{FirstText(card)}' bounds={card.Bounds} region={(card.Region is null ? "none" : "set")}");
            foreach (Control c in card.Controls)
            {
                string text = c is Label l ? l.Text : c is Button b ? b.Text : c.GetType().Name;
                sb.AppendLine($"    {c.GetType().Name} bounds={c.Bounds} text='{Trim(text)}'");
                if (c is FlowLayoutPanel)
                    foreach (Control g in c.Controls)
                        sb.AppendLine($"        {g.GetType().Name} bounds={g.Bounds} text='{Trim(g is Label gl ? gl.Text : g is Button gb ? gb.Text : "")}'");
            }
        }
        return sb.ToString();
    }

    private static string FirstText(Control card)
    {
        foreach (Control c in card.Controls)
            if (c is Label l) return l.Text;
        return "";
    }

    private static string Trim(string s) => s.Length > 22 ? s[..22] + "…" : s;

    /// Opens the connection to the phone. The pump is not started until this is
    /// pressed, so nothing is dialled while the address is still being typed.
    private void ConnectPhone()
    {
        _settings.PhoneHost = _phoneBox.Text.Trim();
        _settings.Save();
        _link.Host = _settings.PhoneHost;
        _link.Start();
    }

    private void ImportScreenshot()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "插入图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp|所有文件|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (_live.Running) _live.Stop();
        if (!LoadImage(dialog.FileName))
            MessageBox.Show(this, $"插入图片失败：{_loadError}", "Windows Duo",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// Returns false and leaves the reason in `_loadError`. Reporting it is the
    /// caller's business: the shot harness loads a path that is allowed not to
    /// exist, and a modal dialog there would hang it.
    private bool LoadImage(string path)
    {
        try
        {
            _loadError = null;
            if (!File.Exists(path)) { _loadError = $"文件不存在: {path}"; return false; }
            var (rgba, w, h) = ImageSource.Load(path);
            _renderer.SetSource(rgba, w, h, mips: true);
            _imagePath = path;
            return true;
        }
        catch (Exception ex)
        {
            _loadError = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private void UpdateFrame()
    {
        bool live = _live.Running;
        _liveButton.Text = live ? "停止实时桌面效果" : "启用实时桌面效果";

        if (!live)
        {
            double angle = _usePhone.Checked && !_link.UseManual && _link.IsAvailable ? _link.MeasuredAngle : _manual.Value;
            float tilt = Mapping.Tilt(angle, Mapping.OpenAngle, _strength.Value / 100.0);
            _panel.SetParams(tilt, _frost.Value / 100f, _eye.Eye);

            string source = _usePhone.Checked ? _link.Status : "手动模拟";
            _status.Text = $"角度 {angle:0.0}°  →  tilt {tilt:0.0}°\n{source}";
            _valueReadout.Text = $"实际角度 {angle:0.0}°（tilt {tilt:0.0}°）";
            if (_panel.LastError is not null) _status.Text += "\n" + _panel.LastError;
        }
        else
        {
            _status.Text = _live.Status;
            _valueReadout.Text = "实时桌面模式中";
        }

        // Keep model/settings in sync while live.
        _settings.Frost = _frost.Value / 100f;
        _settings.Glow = _glow.Value / 100f;
        _settings.Strength = _strength.Value / 100f;
        _renderer.BleedK = _settings.Glow;
        _live.BleedK = _settings.Glow;

    }
}
