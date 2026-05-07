namespace ClaudeTerminalWebViewHost;

public sealed class HostOptions
{
    public Uri Url { get; private init; } = new("http://127.0.0.1:50558/");
    public string Title { get; private init; } = "Claude Code Terminal";
    public bool Embedded { get; private init; }
    public bool UseNativeChildWindow { get; private init; }
    public nint ParentWindowHandle { get; private init; }
    public int ControlPort { get; private init; }
    public int Left { get; private init; }
    public int Top { get; private init; }
    public int Width { get; private init; } = 1100;
    public int Height { get; private init; } = 760;

    public static HostOptions Parse(string[] args)
    {
        var url = new Uri("http://127.0.0.1:50558/");
        var title = "Claude Code Terminal";
        var embedded = false;
        var useNativeChildWindow = false;
        var parentWindowHandle = nint.Zero;
        var controlPort = 0;
        var left = 0;
        var top = 0;
        var width = 1100;
        var height = 760;

        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            var value = i + 1 < args.Length ? args[i + 1] : string.Empty;

            switch (key)
            {
                case "--embedded":
                    embedded = true;
                    break;
                case "--native-child-window":
                    useNativeChildWindow = true;
                    break;
                case "--url" when Uri.TryCreate(value, UriKind.Absolute, out var parsedUrl):
                    url = parsedUrl;
                    i++;
                    break;
                case "--title" when !string.IsNullOrWhiteSpace(value):
                    title = value;
                    i++;
                    break;
                case "--parent-hwnd" when long.TryParse(value, out var parsedParentWindowHandle):
                    parentWindowHandle = (nint)parsedParentWindowHandle;
                    i++;
                    break;
                case "--control-port" when int.TryParse(value, out var parsedControlPort):
                    controlPort = parsedControlPort;
                    i++;
                    break;
                case "--left" when int.TryParse(value, out var parsedLeft):
                    left = parsedLeft;
                    i++;
                    break;
                case "--top" when int.TryParse(value, out var parsedTop):
                    top = parsedTop;
                    i++;
                    break;
                case "--width" when int.TryParse(value, out var parsedWidth):
                    width = Math.Max(640, parsedWidth);
                    i++;
                    break;
                case "--height" when int.TryParse(value, out var parsedHeight):
                    height = Math.Max(420, parsedHeight);
                    i++;
                    break;
            }
        }

        return new HostOptions
        {
            Url = url,
            Title = title,
            Embedded = embedded,
            UseNativeChildWindow = useNativeChildWindow,
            ParentWindowHandle = parentWindowHandle,
            ControlPort = controlPort,
            Left = left,
            Top = top,
            Width = width,
            Height = height
        };
    }
}
