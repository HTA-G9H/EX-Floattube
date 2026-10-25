namespace FloatTube;

/// <summary>
/// "Support us" window — shows a donation image (MoMo QR, Ko-fi, …) with a
/// headline, a short message and a footer line. Images are embedded inside
/// the exe as resources, so no extra files are needed.
/// </summary>
internal sealed class SupportDialog : Form
{
    public SupportDialog(string headline, string body, string resourceName,
                         string footer, Color? imageBack = null)
    {
        Text = "Support us — EXotica Floatube";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition   = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(420, 600);
        TopMost = true;
        BackColor = Color.FromArgb(0x18, 0x18, 0x1C);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9.5f);

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(16, 14, 16, 14),
        };

        var title = new Label
        {
            Text = headline,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 14f),
            ForeColor = Color.FromArgb(0x22, 0xC5, 0x5E),
            Margin = new Padding(0, 0, 0, 4),
        };

        var subtitle = new Label
        {
            Text = body,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10),
        };

        var qrBox = new PictureBox
        {
            Width = 356, Height = 384,
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(14, 0, 0, 8),
            BackColor = imageBack ?? Color.FromArgb(0x18, 0x18, 0x1C),
        };
        try
        {
            using var s = typeof(SupportDialog).Assembly.GetManifestResourceStream(resourceName);
            if (s != null)
            {
                using var tmp = Image.FromStream(s);
                qrBox.Image = new Bitmap(tmp);
            }
        }
        catch { }
        if (qrBox.Image == null)
            subtitle.Text += "\n\n(Image failed to load.)";

        var footerLabel = new Label
        {
            Text = footer,
            AutoSize = true,
            ForeColor = Color.Gray,
            Margin = new Padding(0, 0, 0, 10),
        };

        var closeBtn = new Button
        {
            Text = "Close", Width = 120, Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0x2A, 0x2A, 0x2F),
            ForeColor = Color.Gainsboro,
            Margin = new Padding(0),
        };
        closeBtn.FlatAppearance.BorderColor = Color.FromArgb(0x44, 0x44, 0x4A);
        closeBtn.Click += (_, _) => Close();

        flow.Controls.Add(title);
        flow.Controls.Add(subtitle);
        flow.Controls.Add(qrBox);
        flow.Controls.Add(footerLabel);
        flow.Controls.Add(closeBtn);
        Controls.Add(flow);

        FormClosed += (_, _) => qrBox.Image?.Dispose();
    }
}
