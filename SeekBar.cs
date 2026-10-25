using System.Runtime.InteropServices;

namespace FloatTube;

/// <summary>
/// A thin click-to-seek progress bar used as the video timeline.
/// Clicking (or dragging) reports a 0..1 fraction via <see cref="SeekFraction"/>.
/// </summary>
public sealed class SeekBar : ProgressBar
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public event Action<double>? SeekFraction;

    public bool IsDragging { get; private set; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            // PBM_SETBARCOLOR — paint the fill YouTube-red.
            SendMessage(Handle, 0x0409, IntPtr.Zero, (IntPtr)ColorTranslator.ToWin32(Color.FromArgb(0xFF, 0x00, 0x33)));
        }
        catch { /* cosmetic only */ }
    }

    private void Fire(MouseEventArgs e)
    {
        double f = Math.Clamp((double)e.X / Math.Max(1, Width - 1), 0.0, 1.0);
        SeekFraction?.Invoke(f);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { IsDragging = true; Fire(e); }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsDragging) Fire(e);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        IsDragging = false;
        base.OnMouseUp(e);
    }
}
