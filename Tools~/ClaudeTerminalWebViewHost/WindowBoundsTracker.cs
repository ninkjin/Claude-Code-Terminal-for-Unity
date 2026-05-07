namespace ClaudeTerminalWebViewHost;

public sealed class WindowBoundsTracker
{
    private WindowBounds lastBounds;
    private bool hasLastBounds;

    public bool ShouldApply(int left, int top, int width, int height, bool force = false)
    {
        var bounds = new WindowBounds(left, top, width, height);
        if (!force && hasLastBounds && bounds.Equals(lastBounds))
        {
            return false;
        }

        lastBounds = bounds;
        hasLastBounds = true;
        return true;
    }

    private readonly record struct WindowBounds(int Left, int Top, int Width, int Height);
}
