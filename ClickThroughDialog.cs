namespace FloatTube;

/// <summary>
/// Small settings dialog for click-through mode: shows the current hotkey,
/// lets the user rebind it, or enable click-through immediately.
/// </summary>
internal sealed class ClickThroughDialog : Form
{
    public HotkeyCombo Combo { get; private set; }
    public bool HotkeyChanged { get; private set; }

    private readonly Label _current;
    private readonly Button _changeBtn;
    private bool _capturing;

    public ClickThroughDialog(HotkeyCombo current)
    {
        Combo = new HotkeyCombo { Modifiers = current.Modifiers, Key = current.Key };

        Text = "Click-through mode";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(390, 258);
        KeyPreview = true;
        TopMost = true;
        BackColor = Color.FromArgb(0x18, 0x18, 0x1C);
        ForeColor = Color.Gainsboro;

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(14, 12, 14, 12),
        };

        var info = new Label
        {
            AutoSize = true, Width = 350, Margin = new Padding(0, 0, 0, 10),
            ForeColor = Color.Gainsboro,
            Text = "While click-through is ON, this window ignores the mouse —\n" +
                   "your clicks pass straight through to the apps underneath.\n" +
                   "Press the hotkey to interact again. (Right-clicking the\n" +
                   "taskbar icon → Close always works too.)",
        };

        _current = new Label
        {
            AutoSize = true, Margin = new Padding(0, 0, 0, 6),
            Font = new Font("Segoe UI Semibold", 10.5f),
            ForeColor = Color.FromArgb(0x22, 0xC5, 0x5E),
            Text = "Hotkey: " + Combo,
        };

        _changeBtn = MakeButton("Change hotkey…");
        _changeBtn.Click += (_, _) =>
        {
            _capturing = true;
            _changeBtn.Text = "Press Ctrl/Alt/Shift + a key…";
        };

        var enableBtn = MakeButton("Enable click-through now");
        enableBtn.Click += (_, _) => { DialogResult = DialogResult.Yes; Close(); };

        var closeBtn = MakeButton("Close");
        closeBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        flow.Controls.Add(info);
        flow.Controls.Add(_current);
        flow.Controls.Add(_changeBtn);
        flow.Controls.Add(enableBtn);
        flow.Controls.Add(closeBtn);
        Controls.Add(flow);
    }

    private static Button MakeButton(string text) => new()
    {
        Text = text, Width = 350, Height = 30, Margin = new Padding(0, 0, 0, 8),
        FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0x2A, 0x2A, 0x2F),
        ForeColor = Color.Gainsboro,
        FlatAppearance = { BorderColor = Color.FromArgb(0x44, 0x44, 0x4A) },
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!_capturing) return;

        var code = e.KeyCode;
        if (code is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin
            or Keys.LControlKey or Keys.RControlKey or Keys.LShiftKey or Keys.RShiftKey
            or Keys.LMenu or Keys.RMenu)
            return; // still holding only modifiers — wait for the real key

        uint mods = 0;
        if (e.Control) mods |= HotkeyCombo.ModControl;
        if (e.Alt)     mods |= HotkeyCombo.ModAlt;
        if (e.Shift)   mods |= HotkeyCombo.ModShift;

        if (mods == 0)
        {
            _changeBtn.Text = "Need Ctrl / Alt / Shift — press again…";
            e.Handled = e.SuppressKeyPress = true;
            return;
        }

        Combo = new HotkeyCombo { Modifiers = mods, Key = (uint)code };
        HotkeyChanged = true;
        _capturing = false;
        _changeBtn.Text = "Change hotkey…";
        _current.Text = "Hotkey: " + Combo;
        e.Handled = e.SuppressKeyPress = true;
    }
}
