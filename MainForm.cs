using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace FloatTube;

/// <summary>
/// A floating, always-on-top mini-player for YouTube.
///
/// The video area is a real Chromium browser (Microsoft Edge WebView2),
/// so playback, playlists, sign-in and cookies all behave exactly like a
/// normal desktop browser. The window around it is a borderless,
/// draggable, resizable WinForms shell with its own playback bar.
///
/// The browser profile lives in %LOCALAPPDATA%\FloatTube\profile, so a
/// Google sign-in persists across launches. Nothing is routed through
/// any third-party proxy — traffic goes straight from this app to YouTube.
/// </summary>
public sealed class MainForm : Form
{
    private const string HomeUrl = "https://www.youtube.com/";

    // YouTube's own sign-in endpoint — Google shows its account chooser here.
    private const string SignInUrl =
        "https://accounts.google.com/ServiceLogin?service=youtube&uilel=3&hl=en&passive=true" +
        "&continue=https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26app%3Ddesktop%26hl%3Den%26next%3D%252F";

    // A plain desktop-Chrome user agent makes Google's sign-in page treat
    // the embedded browser like a normal browser instead of an "app webview".
    private const string ChromeUA =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Old minimum window size = 100% YouTube UI. Below it, the page zooms down
    // together with the window; at/above it, the UI snaps back to normal.
    private const int RefWidth = 430, RefHeight = 320;
    private double _lastZoom = 1.0;

    // click-through / hotkey plumbing
    private const int WmHotkey = 0x0312;
    private const int ClickThroughHotkeyId = 0x1CE5;
    private const int GwlExStyle = -20;
    private const int ExStyleLayered = 0x00080000;
    private const int ExStyleTransparent = 0x00000020;

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(5);

    private readonly WebView2 _web = new();
    private readonly ToolTip  _tips = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };

    private readonly Font _glyphFont = new("Segoe UI Symbol", 10f);
    private readonly Font _uiFont    = new("Segoe UI", 9f);

    private readonly AppSettings _settings;
    private readonly ActivityFilter _activityFilter;

    private ContextMenuStrip _strip = null!;
    private TableLayoutPanel _topBar = null!;
    private Panel _chromeWrap = null!;

    // top bar
    private Label      _appLabel = null!;
    private TextBox    _search   = null!;
    private Button     _navBack = null!, _navFwd = null!, _go = null!;
    private Button     _account = null!, _pin = null!, _minBtn = null!, _menuBtn = null!, _closeBtn = null!;

    // bottom bar
    private SeekBar  _seek = null!;
    private Button   _prevBtn = null!, _back10Btn = null!, _playBtn = null!;
    private Button   _fwd10Btn = null!, _nextBtn = null!, _loopBtn = null!, _volBtn = null!;
    private TrackBar _volume = null!;
    private Label    _timeLabel = null!, _grip = null!;

    private bool     _coreReady;
    private bool     _polling;
    private bool     _pinned = true;
    private bool     _compatUA = true;
    private bool     _syncingVolume;
    private string?  _pendingUrl;
    private DateTime _seekQuietUntil = DateTime.MinValue;

    // idle auto-hide + click-through state
    private bool     _autoHideUi = true;
    private bool     _uiHidden;
    private bool     _clickThrough;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private HotkeyCombo _clickThroughCombo = HotkeyCombo.Default;

    public MainForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost         = true;
        BackColor       = Color.FromArgb(0x0F, 0x0F, 0x12);
        StartPosition   = FormStartPosition.Manual;
        MinimumSize     = new Size(200, 160);
        Padding         = new Padding(6); // thin frame ring -> every edge is grabbable
        Text            = "EXotica Floatube";

        try
        {
            using var ico = typeof(MainForm).Assembly.GetManifestResourceStream("FloatTube.app.ico");
            if (ico != null) Icon = new Icon(ico);
        }
        catch { /* icon is cosmetic */ }

        var wa = Screen.PrimaryScreen!.WorkingArea;
        Size     = new Size(560, 372);
        Location = new Point(wa.Right - Width - 24, wa.Bottom - Height - 24);

        _settings          = AppSettings.Load();
        _autoHideUi        = _settings.AutoHideUi;
        _blockAds          = _settings.BlockAds;
        _clickThroughCombo = _settings.ClickThrough;

        _strip      = BuildMenuStrip();
        _topBar     = BuildTopBar();
        _chromeWrap = BuildBottomChrome();

        _web.Dock = DockStyle.Fill;
        _web.DefaultBackgroundColor = Color.Black;

        Controls.Add(_web);         // innermost: fills whatever the bars leave
        Controls.Add(_chromeWrap);  // bottom strip
        Controls.Add(_topBar);      // top strip

        // watches input anywhere in the app (toolbars, video area, menus)
        _activityFilter = new ActivityFilter(NoteActivity);
        Application.AddMessageFilter(_activityFilter);

        _timer.Tick += async (_, _) => { CheckIdle(); await PollPlayerAsync(); };
    }

    // ------------------------------------------------------------------
    //  UI construction
    // ------------------------------------------------------------------

    private Button MakeButton(string glyph, string tip)
    {
        var b = new Button
        {
            Text = glyph, Width = 36, Dock = DockStyle.Fill, Margin = new Padding(1),
            FlatStyle = FlatStyle.Flat, ForeColor = Color.FromArgb(0xE5, 0xE5, 0xE5),
            BackColor = Color.FromArgb(0x18, 0x18, 0x1C), Font = _glyphFont, TabStop = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x3C, 0x3C, 0x42);
        _tips.SetToolTip(b, tip);
        return b;
    }

    private Button MakeTextButton(string text, string tip)
    {
        var b = MakeButton("", tip);
        b.Text = text;
        b.Font = _uiFont;
        b.Width = 72;
        return b;
    }

    private TableLayoutPanel BuildTopBar()
    {
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 40, Margin = Padding.Empty,
            Padding = new Padding(6, 0, 4, 0),
            BackColor = Color.FromArgb(0x18, 0x18, 0x1C),
            ColumnCount = 10,
        };
        bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));         // 0 title
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));         // 1 back
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));         // 2 fwd
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));    // 3 search
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));         // 4 go
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));         // 5 account
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));         // 6 pin
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));         // 7 minimize
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));         // 8 menu
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));         // 9 close

        _appLabel = new Label
        {
            Text = " EXotica Floatube", AutoSize = true, Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(0x22, 0xC5, 0x5E),
            Font = new Font("Segoe UI Semibold", 10f),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        _tips.SetToolTip(_appLabel, "EXotica Floatube — drag to move · double-click to maximize");

        _navBack = MakeButton("←", "Back");
        _navFwd  = MakeButton("→", "Forward");

        _search = new TextBox
        {
            Dock = DockStyle.Fill, Font = _uiFont,
            PlaceholderText = "Search YouTube, or paste a video / playlist link…",
            BackColor = Color.FromArgb(0x12, 0x12, 0x16),
            ForeColor = Color.Gainsboro,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(4, 8, 4, 2),
        };

        _go       = MakeTextButton("Search", "Search YouTube");
        _account  = MakeTextButton("Account", "Sign in / switch Google account");
        _pin      = MakeButton("📌", "Pin on top (click to toggle)");
        _minBtn   = MakeButton("—", "Minimize");
        _menuBtn  = MakeButton("⋯", "More options");
        _closeBtn = MakeButton("✕", "Close");
        _closeBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xC4, 0x2B, 0x1C);

        bar.Controls.Add(_appLabel, 0, 0);
        bar.Controls.Add(_navBack , 1, 0);
        bar.Controls.Add(_navFwd  , 2, 0);
        bar.Controls.Add(_search  , 3, 0);
        bar.Controls.Add(_go      , 4, 0);
        bar.Controls.Add(_account , 5, 0);
        bar.Controls.Add(_pin     , 6, 0);
        bar.Controls.Add(_minBtn  , 7, 0);
        bar.Controls.Add(_menuBtn , 8, 0);
        bar.Controls.Add(_closeBtn, 9, 0);

        // drag the whole window from the bar / title
        bar.MouseDown              += DragMove;
        _appLabel.MouseDown        += DragMove;
        bar.MouseDoubleClick       += (_, _) => ToggleMaximize();
        _appLabel.MouseDoubleClick += (_, _) => ToggleMaximize();

        _navBack.Click  += (_, _) => { if (_web.CanGoBack) _web.GoBack(); };
        _navFwd.Click   += (_, _) => { if (_web.CanGoForward) _web.GoForward(); };
        _go.Click       += (_, _) => Search(_search.Text);
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Search(_search.Text); }
        };
        _account.Click += (_, _) => NavigateTo(SignInUrl);
        _pin.Click     += (_, _) => SetPinned(!_pinned);
        _minBtn.Click  += (_, _) => WindowState = FormWindowState.Minimized;
        _menuBtn.Click += (_, _) => _strip.Show(_menuBtn, new Point(0, _menuBtn.Height));
        _closeBtn.Click += (_, _) => Close();

        return bar;
    }

    private Panel BuildBottomChrome()
    {
        var wrap = new Panel
        {
            Dock = DockStyle.Bottom, Height = 42,
            BackColor = Color.FromArgb(0x18, 0x18, 0x1C),
        };

        _seek = new SeekBar { Dock = DockStyle.Top, Height = 6, Maximum = 1000 };
        _seek.SeekFraction += SeekToFraction;

        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty,
            Padding = new Padding(4, 1, 2, 1),
            BackColor = Color.Transparent,
            ColumnCount = 11,
        };
        bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        for (int i = 0; i < 11; i++)
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles[6] = new ColumnStyle(SizeType.Percent, 100f); // spacer

        _prevBtn   = MakeButton("⏮", "Previous video (playlist)");
        _back10Btn = MakeButton("↺10", "Back 10 seconds");
        _playBtn   = MakeButton("▶", "Play / pause");
        _fwd10Btn  = MakeButton("↻10", "Forward 10 seconds");
        _nextBtn   = MakeButton("⏭", "Next video (playlist / up-next)");
        _loopBtn   = MakeButton("🔁", "Loop current video");

        var spacer = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

        _timeLabel = new Label
        {
            Text = "–:–– / –:––", AutoSize = true, Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(0xC0, 0xC0, 0xC8), Font = _uiFont,
            TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(6, 0, 6, 0),
        };

        _volBtn = MakeButton("🔊", "Mute / unmute");
        _volBtn.Width = 34;

        _volume = new TrackBar
        {
            Minimum = 0, Maximum = 100, Value = 100, TickStyle = TickStyle.None,
            AutoSize = false, Height = 26, Width = 90, Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(0x18, 0x18, 0x1C), Margin = new Padding(2, 5, 2, 2),
        };
        _tips.SetToolTip(_volume, "Volume");

        _grip = new Label
        {
            Text = "◢", Width = 20, Dock = DockStyle.Fill, Margin = Padding.Empty,
            ForeColor = Color.FromArgb(0x70, 0x70, 0x78), Font = _uiFont,
            TextAlign = ContentAlignment.BottomRight,
        };
        _tips.SetToolTip(_grip, "Drag to resize (window edges work too)");
        _grip.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WmNclButtonDown, (IntPtr)HtBottomRight, IntPtr.Zero);
            }
        };

        _prevBtn.Click   += (_, _) => PreviousVideo();
        _back10Btn.Click += (_, _) => SeekOffset(-10);
        _playBtn.Click   += (_, _) => TogglePlay();
        _fwd10Btn.Click  += (_, _) => SeekOffset(10);
        _nextBtn.Click   += (_, _) => NextVideo();
        _loopBtn.Click   += (_, _) => ToggleLoop();
        _volBtn.Click    += (_, _) => ToggleMute();
        _volume.ValueChanged += (_, _) =>
        {
            if (!_syncingVolume) SetVolume(_volume.Value / 100.0);
        };

        bar.Controls.Add(_prevBtn,   0, 0);
        bar.Controls.Add(_back10Btn, 1, 0);
        bar.Controls.Add(_playBtn,   2, 0);
        bar.Controls.Add(_fwd10Btn,  3, 0);
        bar.Controls.Add(_nextBtn,   4, 0);
        bar.Controls.Add(_loopBtn,   5, 0);
        bar.Controls.Add(spacer,     6, 0);
        bar.Controls.Add(_timeLabel, 7, 0);
        bar.Controls.Add(_volBtn,    8, 0);
        bar.Controls.Add(_volume,    9, 0);
        bar.Controls.Add(_grip,     10, 0);

        wrap.Controls.Add(bar);    // innermost: fills below the seek strip
        wrap.Controls.Add(_seek);  // thin strip along the top of the bottom bar
        return wrap;
    }

    private ContextMenuStrip BuildMenuStrip()
    {
        var m = new ContextMenuStrip();

        m.Items.Add("Sign in / choose Google account…", null, (_, _) => NavigateTo(SignInUrl));

        // port of browser-login.js: sign in inside the user's REAL browser,
        // then import the session cookies into this app
        var viaBrowser = new ToolStripMenuItem("Sign in via your installed browser");
        var foundBrowsers = BrowserLogin.FindBrowsers();
        if (foundBrowsers.Count == 0)
        {
            viaBrowser.DropDownItems.Add("(no Chrome / Edge / Brave found)").Enabled = false;
        }
        else
        {
            foreach (var b in foundBrowsers)
            {
                var browser = b;
                viaBrowser.DropDownItems.Add(browser.Name + "…", null,
                    (_, _) => _ = SignInViaBrowserAsync(browser));
            }
        }
        m.Items.Add(viaBrowser);

        m.Items.Add(new ToolStripSeparator());

        var pin = new ToolStripMenuItem("Always on top") { Checked = true, CheckOnClick = true };
        pin.CheckedChanged += (_, _) => SetPinned(pin.Checked);
        m.Items.Add(pin);

        var autoHide = new ToolStripMenuItem("Auto-hide UI when idle (5 s)")
            { Checked = _autoHideUi, CheckOnClick = true };
        autoHide.CheckedChanged += (_, _) =>
        {
            _autoHideUi = autoHide.Checked;
            _settings.AutoHideUi = _autoHideUi;
            _settings.Save();
            if (!_autoHideUi) SetChromeVisible(true);
        };
        m.Items.Add(autoHide);

        var blockAds = new ToolStripMenuItem("Block YouTube ads (experimental)")
            { Checked = _blockAds, CheckOnClick = true };
        blockAds.CheckedChanged += (_, _) => _ = BlockAdsChangedAsync(blockAds.Checked);
        m.Items.Add(blockAds);

        m.Items.Add("Disable interaction (click-through)…", null, (_, _) => OpenClickThroughDialog());

        var opacityItem = new ToolStripMenuItem("Opacity");
        foreach (var pct in new[] { 100, 85, 70, 55 })
        {
            var v = pct;
            opacityItem.DropDownItems.Add(v + "%", null, (_, _) => Opacity = v / 100.0);
        }
        m.Items.Add(opacityItem);

        var sizeItem = new ToolStripMenuItem("Window size");
        sizeItem.DropDownItems.Add("Small  (480×270)", null, (_, _) => ResizeVideo(480, 270));
        sizeItem.DropDownItems.Add("Medium (640×360)", null, (_, _) => ResizeVideo(640, 360));
        sizeItem.DropDownItems.Add("Large  (854×480)", null, (_, _) => ResizeVideo(854, 480));
        m.Items.Add(sizeItem);

        var compat = new ToolStripMenuItem("Compatibility mode (act like Chrome)")
            { Checked = _compatUA, CheckOnClick = true };
        compat.CheckedChanged += (_, _) =>
        {
            _compatUA = compat.Checked;
            if (_web.CoreWebView2 != null)
            {
                _web.CoreWebView2.Settings.UserAgent = _compatUA ? ChromeUA : null;
                _web.CoreWebView2.Reload();
            }
        };
        m.Items.Add(compat);

        m.Items.Add(new ToolStripSeparator());

        m.Items.Add("Open current page in your default browser", null, (_, _) =>
        {
            try
            {
                var src = _web.Source;
                if (src is { IsAbsoluteUri: true })
                    Process.Start(new ProcessStartInfo(src.ToString()) { UseShellExecute = true });
            }
            catch { }
        });
        m.Items.Add("Go to YouTube home", null, (_, _) => NavigateTo(HomeUrl));
        m.Items.Add(new ToolStripSeparator());

        var exSocial = new ToolStripMenuItem("EX social");
        exSocial.DropDownItems.Add("Discord — EXotica Inc. Community", null,
            (_, _) => OpenLink("https://discord.gg/vTz8F2wM8F"));
        exSocial.DropDownItems.Add("TikTok — @exotica1nc_", null,
            (_, _) => OpenLink("https://www.tiktok.com/@exotica1nc_"));
        exSocial.DropDownItems.Add("YouTube — EXotica channel", null,
            (_, _) => OpenLink("https://www.youtube.com/channel/UCY6vvxKsc5RSP98RNERLIXg"));
        m.Items.Add(exSocial);
        m.Items.Add("Support us 💚 (MoMo QR)", null, (_, _) =>
        {
            using var dlg = new SupportDialog(
                "Support us 💚",
                "Enjoying EXotica Floatube? Scan the QR below with MoMo\n" +
                "(or any VietQR-compatible banking app) — every bit\n" +
                "helps keep the project alive. Thank you!",
                "FloatTube.support-qr.png",
                "MoMo · NGUYEN HOANG THIEN AN · account •••••••032");
            dlg.ShowDialog(this);
        });
        m.Items.Add("Support us ☕ (Ko-fi — coming soon)", null, (_, _) =>
        {
            using var dlg = new SupportDialog(
                "Support us ☕",
                "Donate via Ko-fi — coming soon!\n" +
                "We're still setting up the Ko-fi page.",
                "FloatTube.support-kofi.png",
                "Follow the EX social links for the announcement.",
                Color.White);
            dlg.ShowDialog(this);
        });
        m.Items.Add(new ToolStripSeparator());

        m.Items.Add("About EXotica Floatube…", null, (_, _) =>
            MessageBox.Show(this,
                "EXotica Floatube 1.1\n\nA floating, always-on-top YouTube mini-player.\n" +
                "Runs on the Microsoft Edge WebView2 browser engine.\n\n" +
                "Your sign-in and cookies live only in this app's local profile —\n" +
                "nothing is proxied through any third-party server.",
                "About EXotica Floatube", MessageBoxButtons.OK, MessageBoxIcon.Information));

        return m;
    }

    // ------------------------------------------------------------------
    //  Browser startup
    // ------------------------------------------------------------------

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        SetRoundCorners();
        SetPinned(true);
        RegisterClickThroughHotkey();
        await InitializeBrowserAsync();
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            Directory.CreateDirectory(Paths.DataRoot);
            var profileDir = Path.Combine(Paths.DataRoot, "profile");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: profileDir);
            await _web.EnsureCoreWebView2Async(env);

            var settings = _web.CoreWebView2.Settings;
            if (_compatUA) settings.UserAgent = ChromeUA;
            settings.IsStatusBarEnabled = false;

            // ad blocking: request filter (network) + per-page script (hide & skip)
            _webEnv = env;
            _web.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            _web.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
            await ApplyAdBlockSettingAsync();

            _coreReady = true;
            _web.CoreWebView2.Navigate(_pendingUrl ?? HomeUrl);
            _pendingUrl = null;
            _timer.Start();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            var r = MessageBox.Show(this,
                "EXotica Floatube embeds a browser using the Microsoft Edge WebView2 Runtime,\n" +
                "which doesn't appear to be installed on this PC.\n\n" +
                "Open the runtime download page now?",
                "EXotica Floatube — missing WebView2 Runtime",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r == DialogResult.Yes)
                Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703") { UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            MessageBox.Show(this,
                "Couldn't start the embedded browser:\n\n" + ex.Message +
                "\n\nDetails were written to:\n" + Path.Combine(Paths.DataRoot, "error.log"),
                "EXotica Floatube", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ------------------------------------------------------------------
    //  Idle auto-hide: fold the toolbars away, keep only the video
    // ------------------------------------------------------------------

    private void NoteActivity()
    {
        _lastActivityUtc = DateTime.UtcNow;
        if (_uiHidden) SetChromeVisible(true);
    }

    private void SetChromeVisible(bool visible)
    {
        if (visible == !_uiHidden) return;
        _topBar.Visible = visible;
        _chromeWrap.Visible = visible;
        _uiHidden = !visible;
    }

    private void CheckIdle()
    {
        if (!_autoHideUi || _uiHidden || !_coreReady || WindowState == FormWindowState.Minimized)
            return;
        if (DateTime.UtcNow - _lastActivityUtc < IdleTimeout) return;
        if (_strip.Visible) return;

        // don't hide while the pointer is actually resting on a bar or the frame
        if (Bounds.Contains(Cursor.Position))
        {
            var p = PointToClient(Cursor.Position);
            if (_topBar.Visible && p.Y <= _topBar.Bottom) return;
            if (_chromeWrap.Visible && p.Y >= _chromeWrap.Top) return;
        }
        SetChromeVisible(false);
    }

    // ------------------------------------------------------------------
    //  Click-through mode ("disable interaction") + global hotkey
    // ------------------------------------------------------------------

    [DllImport("user32.dll")] private static extern int  GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int  SetWindowLong(IntPtr hWnd, int index, int newLong);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private void RegisterClickThroughHotkey()
    {
        try
        {
            UnregisterHotKey(Handle, ClickThroughHotkeyId); // best effort
            if (!RegisterHotKey(Handle, ClickThroughHotkeyId,
                    _clickThroughCombo.Modifiers, _clickThroughCombo.Key))
            {
                MessageBox.Show(this,
                    $"Couldn't bind {_clickThroughCombo} — another program is already using that hotkey.\n" +
                    "Open ⋯ → Disable interaction (click-through)… to pick another one.",
                    "EXotica Floatube", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch { }
    }

    private void ToggleClickThrough(bool on)
    {
        _clickThrough = on;
        int ex = GetWindowLong(Handle, GwlExStyle);
        if (on)
        {
            // WS_EX_LAYERED | WS_EX_TRANSPARENT -> mouse clicks fall through
            SetWindowLong(Handle, GwlExStyle, ex | ExStyleLayered | ExStyleTransparent);
        }
        else
        {
            SetWindowLong(Handle, GwlExStyle, ex & ~ExStyleTransparent);
        }

        SetChromeVisible(true);
        SetStatus(on
            ? $"Click-through ON — press {_clickThroughCombo} to re-enable input"
            : "EXotica Floatube");
        _lastActivityUtc = DateTime.UtcNow;
    }

    private static void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no default browser configured */ }
    }

    private void OpenClickThroughDialog()
    {
        using var dlg = new ClickThroughDialog(_clickThroughCombo);
        var result = dlg.ShowDialog(this);
        if (dlg.HotkeyChanged)
        {
            _clickThroughCombo = dlg.Combo;
            _settings.ClickThrough = _clickThroughCombo;
            _settings.Save();
            RegisterClickThroughHotkey();
        }
        if (result == DialogResult.Yes) // "Enable click-through now"
            ToggleClickThrough(true);
    }

    // ------------------------------------------------------------------
    //  Navigation & search
    // ------------------------------------------------------------------

    private void Search(string query)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0) return;

        string url;
        if (query.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            query.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = query;
        }
        else if (query.StartsWith("youtu.be/", StringComparison.OrdinalIgnoreCase) ||
                 query.StartsWith("www.youtube.com/", StringComparison.OrdinalIgnoreCase) ||
                 query.StartsWith("youtube.com/", StringComparison.OrdinalIgnoreCase) ||
                 query.StartsWith("m.youtube.com/", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + query;
        }
        else
        {
            url = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query);
        }
        NavigateTo(url);
    }

    private void NavigateTo(string url)
    {
        if (_coreReady && _web.CoreWebView2 != null)
            _web.CoreWebView2.Navigate(url);
        else
            _pendingUrl = url;
    }

    // ------------------------------------------------------------------
    //  Sign in via an installed browser (browser-login.js flow)
    // ------------------------------------------------------------------

    private void SetStatus(string text) => _appLabel.Text = " " + text;

    private async Task SignInViaBrowserAsync(DetectedBrowser browser)
    {
        if (!_coreReady || _web.CoreWebView2 == null) return;
        try
        {
            SetStatus($"Sign in: finish it in the {browser.Name} window…");
            var loginProfile = Path.Combine(Paths.DataRoot, "browser-login");
            var result = await BrowserLogin.SignInAsync(browser.ExePath, loginProfile, SetStatus);

            // Recreate the session cookies inside the embedded browser.
            var cookieManager = _web.CoreWebView2.CookieManager;
            int imported = 0;
            foreach (var c in result.Cookies)
            {
                if (c.Domain.Length == 0 ||
                    !(c.Domain.Contains("google",  StringComparison.OrdinalIgnoreCase) ||
                      c.Domain.Contains("youtube", StringComparison.OrdinalIgnoreCase)))
                    continue;
                try
                {
                    var wc = cookieManager.CreateCookie(
                        c.Name, c.Value, c.Domain.TrimStart('.'),
                        c.Path.Length > 0 ? c.Path : "/");
                    wc.IsSecure   = c.Secure;
                    wc.IsHttpOnly = c.HttpOnly;
                    wc.SameSite = c.SameSite switch
                    {
                        "Lax"    => CoreWebView2CookieSameSiteKind.Lax,
                        "Strict" => CoreWebView2CookieSameSiteKind.Strict,
                        _        => CoreWebView2CookieSameSiteKind.None,
                    };
                    if (c.Expires > 0)
                        wc.Expires = DateTimeOffset.FromUnixTimeSeconds((long)c.Expires).UtcDateTime;
                    cookieManager.AddOrUpdateCookie(wc);
                    imported++;
                }
                catch { /* skip a malformed cookie */ }
            }

            if (result.UserAgent.Length > 0)
                _web.CoreWebView2.Settings.UserAgent = result.UserAgent;

            SetStatus("EXotica Floatube");
            MessageBox.Show(this,
                $"Signed in with {browser.Name} — imported {imported} cookies.\n\n" +
                "Reloading YouTube with your account.",
                "EXotica Floatube", MessageBoxButtons.OK, MessageBoxIcon.Information);
            NavigateTo(HomeUrl);
        }
        catch (Exception ex)
        {
            SetStatus("EXotica Floatube");
            LogCrash(ex);
            MessageBox.Show(this, "Browser sign-in didn't finish:\n\n" + ex.Message,
                "EXotica Floatube", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ------------------------------------------------------------------
    //  Ad blocking: network blocklist + cosmetic hiding + ad auto-skip
    // ------------------------------------------------------------------

    private CoreWebView2Environment? _webEnv;
    private bool _blockAds;
    private string? _adScriptId;

    private async Task BlockAdsChangedAsync(bool enable)
    {
        _blockAds = enable;
        _settings.BlockAds = enable;
        _settings.Save();
        await ApplyAdBlockSettingAsync();
        try { _web.CoreWebView2?.Reload(); } catch { }
    }

    private async Task ApplyAdBlockSettingAsync()
    {
        var core = _web.CoreWebView2;
        if (core == null) return;
        try
        {
            if (_blockAds && _adScriptId == null)
                _adScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(AdBlockScript);
            else if (!_blockAds && _adScriptId != null)
            {
                core.RemoveScriptToExecuteOnDocumentCreated(_adScriptId);
                _adScriptId = null;
            }
        }
        catch { /* runtime restarting */ }
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!_blockAds || _webEnv == null) return;
        if (IsAdUrl(e.Request.Uri))
        {
            try { e.Response = _webEnv.CreateWebResourceResponse(null, 403, "Blocked", ""); }
            catch { }
        }
    }

    private static readonly string[] AdHosts =
    {
        "doubleclick.net", "googlesyndication.com", "googleadservices.com",
        "googletagservices.com", "moatads.com", "scorecardresearch.com",
    };

    // NOTE: we deliberately do NOT block YouTube's own /pagead / stats / midroll
    // endpoints — killing those makes the player think the ad was never delivered,
    // so it re-serves ads (that retry loop is what caused "even MORE ads").
    // Only pure third-party ad networks are blocked at the network layer.
    private static bool IsAdUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var host = u.Host;
        if (host.StartsWith("adservice.google", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var h in AdHosts)
            if (host.Equals(h, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // Injected into every page: hides sponsored placements/overlays and
    // auto-skips (or blasts through) video ads.
    // v2: instead of blocking ad requests (which makes YouTube re-serve ads),
    // strip ad placements out of the player's own data — the same core idea
    // uBlock Origin uses (json-prune). Plus cosmetics + skip-button clicking.
    private const string AdBlockScript =
        @"(function(){
if(window.__ftAdBlock)return;window.__ftAdBlock=true;
var ADKEYS={adPlacements:1,playerAds:1,adSlots:1,adBreakHeartbeatParams:1,prerollVideoPlayerConfig:1,midrollFrequencyRanges:1,adBreakParams:1,adSafetyReason:1,serializedAdServingDataEntry:1};
function clean(o){
if(!o||typeof o!=='object')return o;
if(Array.isArray(o)){for(var i=0;i<o.length;i++)clean(o[i]);return o;}
for(var k in o){if(ADKEYS[k]){try{delete o[k];}catch(e){}}else{clean(o[k]);}}
return o;}
function prune(t){
if(!/adPlacements|playerAds|adSlots|adBreak|prerollVideoPlayerConfig|midrollFrequencyRanges/.test(t))return t;
try{var o=JSON.parse(t);clean(o);return JSON.stringify(o);}catch(e){return t;}}
try{var op=JSON.parse;JSON.parse=function(){var r=op.apply(this,arguments);try{clean(r);}catch(e){}return r;};}catch(e){}
if(window.fetch){try{var of=window.fetch;window.fetch=function(){
return of.apply(this,arguments).then(function(resp){
try{var url=(resp&&resp.url)||'';
if(url.indexOf('/youtubei/')>=0||url.indexOf('get_video_info')>=0){
return resp.clone().text().then(function(t){
return new Response(prune(t),{status:resp.status,statusText:resp.statusText,headers:resp.headers});});}
}catch(e){}
return resp;});};}catch(e){}}
try{
var OP=XMLHttpRequest.prototype.open;
XMLHttpRequest.prototype.open=function(m,u){try{this.__ftU=(''+u);}catch(e){}return OP.apply(this,arguments);};
var SD=XMLHttpRequest.prototype.send;
XMLHttpRequest.prototype.send=function(){
var x=this;
try{if(x.__ftU&&(x.__ftU.indexOf('/youtubei/')>=0||x.__ftU.indexOf('get_video_info')>=0)){
var d=Object.getOwnPropertyDescriptor(XMLHttpRequest.prototype,'responseText');
if(d&&d.get){
Object.defineProperty(x,'responseText',{configurable:true,get:function(){return prune(d.get.call(x));}});
Object.defineProperty(x,'response',{configurable:true,get:function(){var v=x.responseText;try{return x.responseType==='json'?JSON.parse(v):v;}catch(e){return v;}}});}
}}catch(e){}
return SD.apply(this,arguments);};
}catch(e){}
setInterval(function(){try{var r=window.ytInitialPlayerResponse;if(r){delete r.adPlacements;delete r.playerAds;delete r.adSlots;}}catch(e){}},250);
var css=document.createElement('style');
css.textContent=[
'.video-ads','.ytp-ad-module','.ytp-ad-overlay-container','.ytp-ad-text-overlay',
'.ytp-ad-image-overlay','.ytp-ad-player-overlay','ytd-ad-slot-renderer',
'ytd-display-ad-renderer','ytd-promoted-sparkles-web-renderer','ytd-promoted-video-renderer',
'ytd-compact-promoted-video-renderer','ytd-in-feed-ad-layout-renderer','ytd-banner-promo-renderer',
'ytd-statement-banner-renderer','ytd-brand-video-singleton-renderer',
'ytd-player-legacy-desktop-watch-ads-renderer','#player-ads','#masthead-ad',
'ytd-rich-item-renderer:has(ytd-ad-slot-renderer)','ytd-rich-item-renderer:has(.badge-style-type-ad)',
'ytd-video-renderer:has(.badge-style-type-ad)','ytd-search-pyv-renderer',
'ytd-promoted-sparkles-text-search-renderer','ytd-carousel-ad-renderer',
'.yt-mealbar-promo-renderer','#panels:has(ytd-ads-engagement-panel-content-renderer)',
'ytd-popup-container:has(ytd-enforcement-message-view-model)',
'ytd-enforcement-message-view-model','tp-yt-paper-dialog:has(ytd-enforcement-message-view-model)'
].join(',')+'{display:none!important;}';
(document.head||document.documentElement).appendChild(css);
setInterval(function(){
try{
var b=document.querySelector('.ytp-ad-skip-button, .ytp-skip-ad-button, .ytp-ad-skip-button-modern, .ytp-ad-skip-button-modern button, .ytp-ad-skip-button-container button');
if(b)b.click();
var x=document.querySelector('.ytp-ad-overlay-close-button');
if(x)x.click();
var ad=document.querySelector('.ad-showing, .ad-interrupting');
var v=document.querySelector('video');
if(v&&ad&&!window.__ftAdBoost){window.__ftAdBoost=true;window.__ftPrevRate=v.playbackRate;v.muted=true;v.playbackRate=16;}
else if(v&&!ad&&window.__ftAdBoost){window.__ftAdBoost=false;v.playbackRate=window.__ftPrevRate||1;}
}catch(e){}
},300);
})();";

    // ------------------------------------------------------------------
    //  Playback control (small JavaScript snippets inside the page)
    // ------------------------------------------------------------------

    private void Js(string script)
    {
        if (!_coreReady || _web.CoreWebView2 == null) return;
        try { _ = _web.CoreWebView2.ExecuteScriptAsync(script); }
        catch { /* app closing / page navigating */ }
    }

    private void TogglePlay() =>
        Js("(function(){var v=document.querySelector('video');" +
           "if(v){if(v.paused){v.play();}else{v.pause();}}})()");

    private void SeekOffset(double seconds) =>
        Js("(function(){var v=document.querySelector('video');if(v){" +
           "var t=v.currentTime+(" + seconds.ToString(CultureInfo.InvariantCulture) + ");" +
           "if(isFinite(v.duration)){t=Math.min(t,v.duration-0.05);}" +
           "v.currentTime=Math.max(0,t);}})()");

    private void SeekToFraction(double f)
    {
        _seekQuietUntil = DateTime.UtcNow.AddSeconds(1.2);
        _seek.Value = Math.Clamp((int)(f * _seek.Maximum), 0, _seek.Maximum);
        Js("(function(){var v=document.querySelector('video');" +
           "if(v&&isFinite(v.duration)&&v.duration>0){v.currentTime=v.duration*" +
           f.ToString("0.###", CultureInfo.InvariantCulture) + ";}})()");
    }

    private void NextVideo() =>
        Js("(function(){var b=document.querySelector('.ytp-next-button');if(b){b.click();}})()");

    private void PreviousVideo() =>
        Js("(function(){var b=document.querySelector('.ytp-prev-button');" +
           "if(b&&b.getAttribute('aria-disabled')!=='true'){b.click();}else{history.back();}})()");

    private void ToggleLoop() =>
        Js("(function(){var v=document.querySelector('video');if(v){v.loop=!v.loop;}})()");

    private void ToggleMute() =>
        Js("(function(){var v=document.querySelector('video');if(v){v.muted=!v.muted;}})()");

    private void SetVolume(double v01)
    {
        var s = v01.ToString("0.###", CultureInfo.InvariantCulture);
        Js("(function(){var v=document.querySelector('video');" +
           "if(v){v.volume=" + s + ";v.muted=(" + s + "<=0);}})()");
    }

    // ------------------------------------------------------------------
    //  Polling: keep the timeline / buttons in sync with the <video> tag
    // ------------------------------------------------------------------

    private const string PollScript =
        "(function(){try{var v=document.querySelector('video');" +
        "var r={ok:!!v,paused:true,title:(document.title||'').replace(/ - YouTube$/,'')};" +
        "if(v){r.t=v.currentTime||0;r.d=isFinite(v.duration)?v.duration:0;r.paused=v.paused;" +
        "r.muted=!!v.muted;r.vol=(typeof v.volume==='number'?v.volume:1);r.loop=!!v.loop;}" +
        "return JSON.stringify(r);}catch(e){return '{\"ok\":false}';}})()";

    private sealed class PlayerState
    {
        public bool   Ok     { get; set; }
        public bool   Paused { get; set; } = true;
        public double T      { get; set; }
        public double D      { get; set; }
        public bool   Muted  { get; set; }
        public double Vol    { get; set; } = 1.0;
        public bool   Loop   { get; set; }
        public string? Title { get; set; }
    }

    private async Task PollPlayerAsync()
    {
        if (_polling || !_coreReady || _web.CoreWebView2 == null || IsDisposed || Disposing) return;
        _polling = true;
        try
        {
            string raw = await _web.CoreWebView2.ExecuteScriptAsync(PollScript);
            if (string.IsNullOrEmpty(raw) || raw == "null") return;

            var inner = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrEmpty(inner)) return;

            var s = JsonSerializer.Deserialize<PlayerState>(inner, JsonOpts);
            if (s != null) ApplyPlayerState(s);
        }
        catch { /* page mid-navigation, etc. */ }
        finally { _polling = false; }
    }

    private void ApplyPlayerState(PlayerState s)
    {
        _playBtn.Text = (s.Ok && !s.Paused) ? "⏸" : "▶";
        _loopBtn.ForeColor = s.Loop ? Color.FromArgb(0x22, 0xC5, 0x5E) : Color.FromArgb(0xE5, 0xE5, 0xE5);

        if (!string.IsNullOrEmpty(s.Title) && s.Ok)
            _tips.SetToolTip(_appLabel, "Now playing: " + s.Title + " — drag to move");

        if (s.Ok && s.D > 0)
        {
            _timeLabel.Text = Fmt(s.T) + " / " + Fmt(s.D);
            if (!_seek.IsDragging && DateTime.UtcNow > _seekQuietUntil)
                _seek.Value = Math.Clamp((int)(s.T / s.D * _seek.Maximum), 0, _seek.Maximum);
        }
        else
        {
            _timeLabel.Text = "–:–– / –:––";
            if (!_seek.IsDragging) _seek.Value = 0;
        }

        if (!_volume.Focused)
        {
            _syncingVolume = true;
            _volume.Value = Math.Clamp((int)Math.Round((s.Muted ? 0 : s.Vol) * 100), 0, 100);
            _syncingVolume = false;
        }
    }

    private static string Fmt(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    // ------------------------------------------------------------------
    //  Window behaviour
    // ------------------------------------------------------------------

    private void SetPinned(bool pinned)
    {
        _pinned = pinned;
        TopMost = pinned;
        if (_pin != null)
        {
            _pin.ForeColor = pinned ? Color.FromArgb(0x22, 0xC5, 0x5E) : Color.FromArgb(0xE5, 0xE5, 0xE5);
            _tips.SetToolTip(_pin, pinned ? "Pinned on top — click to unpin" : "Not pinned — click to pin on top");
        }
    }

    /// <summary>
    /// Shrink below the old minimum size -> the YouTube UI scales down with the
    /// window; grow back to the old minimum -> UI returns to 100%.
    /// </summary>
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState != FormWindowState.Minimized) ApplyAutoZoom();
    }

    private void ApplyAutoZoom()
    {
        if (!_coreReady || _web.CoreWebView2 == null) return;
        double z = Math.Min((double)Width / RefWidth, (double)Height / RefHeight);
        z = Math.Clamp(z, 0.35, 1.0);   // Chromium zoom floor is 25%; we stay well above
        if (Math.Abs(z - _lastZoom) < 0.005) return;
        _lastZoom = z;
        try { _web.ZoomFactor = z; } catch { /* runtime busy navigating */ }
    }

    private void ToggleMaximize()
    {
        if (WindowState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Normal;
        }
        else
        {
            MaximizedBounds = Screen.FromControl(this).WorkingArea;
            WindowState = FormWindowState.Maximized;
        }
    }

    private void ResizeVideo(int w, int h)
    {
        int chrome = Height - _web.Height; // keep toolbars, resize only the video area
        Size = new Size(w, h + Math.Max(chrome, 0));
    }

    private const int WmNclButtonDown = 0x00A1;
    private const int HtCaption       = 0x0002;
    private const int HtBottomRight   = 17;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private void DragMove(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ReleaseCapture();
            SendMessage(Handle, WmNclButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
        }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void SetRoundCorners()
    {
        try
        {
            int rounded = 2; // DWMWCP_ROUND — nice corners on Windows 11
            DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref rounded, sizeof(int));
        }
        catch { /* older Windows — square corners are fine */ }
    }

    /// <summary>Resize from the frame edges even though the window is borderless.</summary>
    protected override void WndProc(ref Message m)
    {
        const int WmNcHitTest      = 0x0084;
        const int HtClient         = 1;
        const int HtLeft           = 10;
        const int HtRight          = 11;
        const int HtTop            = 12;
        const int HtTopLeft        = 13;
        const int HtTopRight       = 14;
        const int HtBottom         = 15;
        const int HtBottomLeft     = 16;
        const int HtBottomRightHc  = 17;

        if (m.Msg == WmHotkey && m.WParam == (IntPtr)ClickThroughHotkeyId)
        {
            ToggleClickThrough(!_clickThrough);
            return;
        }

        if (m.Msg == WmNcHitTest && WindowState == FormWindowState.Normal && !IsDisposed)
        {
            base.WndProc(ref m);
            if ((int)(long)m.Result == HtClient)
            {
                const int edge = 10, corner = 16;
                var p = PointToClient(Cursor.Position);
                bool l = p.X <= edge, r = p.X >= Width - edge, t = p.Y <= edge, b = p.Y >= Height - edge;
                int hit = 0;
                if (t && p.X <= corner)               hit = HtTopLeft;
                else if (t && p.X >= Width - corner)  hit = HtTopRight;
                else if (b && p.X <= corner)          hit = HtBottomLeft;
                else if (b && p.X >= Width - corner)  hit = HtBottomRightHc;
                else if (l) hit = HtLeft;
                else if (r) hit = HtRight;
                else if (t) hit = HtTop;
                else if (b) hit = HtBottom;
                if (hit != 0) m.Result = (IntPtr)hit;
            }
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try { UnregisterHotKey(Handle, ClickThroughHotkeyId); } catch { }
        base.OnFormClosed(e);
    }

    private static void LogCrash(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataRoot);
            File.AppendAllText(Path.Combine(Paths.DataRoot, "error.log"),
                $"[{DateTime.Now:u}] {ex}\r\n\r\n");
        }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.RemoveMessageFilter(_activityFilter);
            _timer.Dispose();
            _glyphFont.Dispose();
            _uiFont.Dispose();
            _tips.Dispose();
            _strip.Dispose();
        }
        base.Dispose(disposing);
    }
}
