namespace FloatTube;

/// <summary>
/// Sniffs input messages anywhere on our UI thread — the toolbars, the embedded
/// browser child windows, menus — so the auto-hiding UI can wake up and reset
/// its idle timer on any interaction with the window. Never consumes messages.
/// </summary>
internal sealed class ActivityFilter : IMessageFilter
{
    private readonly Action _onActivity;

    public ActivityFilter(Action onActivity) => _onActivity = onActivity;

    public bool PreFilterMessage(ref Message m)
    {
        switch (m.Msg)
        {
            case 0x0200: // WM_MOUSEMOVE
            case 0x0201: // WM_LBUTTONDOWN
            case 0x0202: // WM_LBUTTONUP
            case 0x0204: // WM_RBUTTONDOWN
            case 0x0205: // WM_RBUTTONUP
            case 0x0207: // WM_MBUTTONDOWN
            case 0x0208: // WM_MBUTTONUP
            case 0x020A: // WM_MOUSEWHEEL
            case 0x020E: // WM_MOUSEHWHEEL
            case 0x00A0: // WM_NCMOUSEMOVE
            case 0x00A1: // WM_NCLBUTTONDOWN
            case 0x0100: // WM_KEYDOWN
            case 0x0104: // WM_SYSKEYDOWN
            case 0x0106: // WM_SYSCHAR
                _onActivity();
                break;
        }
        return false; // watch only, never eat
    }
}
