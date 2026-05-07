using System.Runtime.InteropServices;

namespace ClaudeTerminalWebViewHost;

public sealed class EmbeddedWindowController
{
    private readonly Form form;
    private readonly nint parentWindowHandle;
    private readonly bool useNativeChildWindow;
    private readonly WindowBoundsTracker boundsTracker = new();
    private WindowBounds lastAppliedBounds;
    private bool hasLastAppliedBounds;

    public EmbeddedWindowController(Form form, nint parentWindowHandle, bool useNativeChildWindow)
    {
        this.form = form;
        this.parentWindowHandle = parentWindowHandle;
        this.useNativeChildWindow = useNativeChildWindow;
    }

    public bool HasParentWindow => parentWindowHandle != nint.Zero;

    public void Attach()
    {
        if (!HasParentWindow)
        {
            return;
        }

        if (useNativeChildWindow)
        {
            SetParent(form.Handle, parentWindowHandle);
            var childStyle = GetWindowLongPtr(form.Handle, GwlStyle).ToInt64();
            childStyle &= ~(WsPopup | WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSysMenu);
            childStyle |= WsChild | WsVisible;
            SetWindowLongPtr(form.Handle, GwlStyle, new nint(childStyle));
            return;
        }

        var popupStyle = GetWindowLongPtr(form.Handle, GwlStyle).ToInt64();
        popupStyle &= ~(WsChild | WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSysMenu);
        popupStyle |= WsPopup | WsVisible;
        SetWindowLongPtr(form.Handle, GwlStyle, new nint(popupStyle));
        SetWindowLongPtr(form.Handle, GwlHwndParent, parentWindowHandle);
    }

    public bool ApplyScreenBounds(int left, int top, int width, int height, bool force = false)
    {
        width = Math.Max(160, width);
        height = Math.Max(120, height);
        if (!boundsTracker.ShouldApply(left, top, width, height, force))
        {
            return false;
        }

        lastAppliedBounds = new WindowBounds(left, top, width, height);
        hasLastAppliedBounds = true;

        if (!HasParentWindow || !useNativeChildWindow)
        {
            SetWindowPos(
                form.Handle,
                HwndTop,
                left,
                top,
                width,
                height,
                SwpNoActivate | SwpFrameChanged | SwpShowWindow);
            RedrawWindow(form.Handle, nint.Zero, nint.Zero, RdwInvalidate | RdwUpdateNow | RdwAllChildren);
            return true;
        }

        var point = new Point(left, top);
        ScreenToClient(parentWindowHandle, ref point);
        SetWindowPos(
            form.Handle,
            nint.Zero,
            point.X,
            point.Y,
            width,
            height,
            SwpNoZOrder | SwpNoActivate | SwpFrameChanged | SwpShowWindow);
        RedrawWindow(form.Handle, nint.Zero, nint.Zero, RdwInvalidate | RdwUpdateNow | RdwAllChildren);
        return true;
    }

    public bool ReapplyLastBounds()
    {
        return hasLastAppliedBounds &&
            ApplyScreenBounds(
                lastAppliedBounds.Left,
                lastAppliedBounds.Top,
                lastAppliedBounds.Width,
                lastAppliedBounds.Height,
                force: true);
    }

    private const int GwlStyle = -16;
    private const int GwlHwndParent = -8;
    private const long WsChild = 0x40000000L;
    private const long WsPopup = 0x80000000L;
    private const long WsVisible = 0x10000000L;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const long WsSysMenu = 0x00080000L;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const uint RdwInvalidate = 0x0001;
    private const uint RdwUpdateNow = 0x0100;
    private const uint RdwAllChildren = 0x0080;
    private static readonly nint HwndTop = nint.Zero;

    private readonly record struct WindowBounds(int Left, int Top, int Width, int Height);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetParent(nint hWndChild, nint hWndNewParent);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RedrawWindow(nint hWnd, nint lprcUpdate, nint hrgnUpdate, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ScreenToClient(nint hWnd, ref Point lpPoint);
}
