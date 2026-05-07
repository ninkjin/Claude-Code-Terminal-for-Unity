using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

var options = CommandLineOptions.Parse(args);
Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine($"Claude Terminal Bridge listening on 127.0.0.1:{options.Port}");
Console.WriteLine($"Command: {options.Command}");

if (options.WebPort > 0)
{
    using var webServer = new WebTerminalServer(options);
    await webServer.RunAsync();
}
else
{
    using var server = new BridgeServer(options);
    await server.RunAsync();
}

internal sealed class WebTerminalServer : IDisposable
{
    private readonly CommandLineOptions options;
    private readonly HttpListener listener = new();
    private ITerminalSession? session;

    public WebTerminalServer(CommandLineOptions options)
    {
        this.options = options;
        listener.Prefixes.Add($"http://127.0.0.1:{options.WebPort}/");
    }

    public async Task RunAsync()
    {
        listener.Start();
        Console.WriteLine($"xterm.js terminal available at http://127.0.0.1:{options.WebPort}/");

        try
        {
            while (listener.IsListening)
            {
                var context = await listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context));
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (HttpListenerException) when (!listener.IsListening)
        {
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        if (context.Request.IsWebSocketRequest && context.Request.Url?.AbsolutePath == "/ws")
        {
            await HandleWebSocketAsync(context);
            return;
        }

        if (context.Request.Url?.AbsolutePath == "/" || context.Request.Url?.AbsolutePath == "/index.html")
        {
            var html = Encoding.UTF8.GetBytes(BuildTerminalHtml(options.WebPort));
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = html.Length;
            await context.Response.OutputStream.WriteAsync(html, 0, html.Length);
            context.Response.Close();
            return;
        }

        context.Response.StatusCode = 404;
        context.Response.Close();
    }

    private async Task HandleWebSocketAsync(HttpListenerContext context)
    {
        var webSocketContext = await context.AcceptWebSocketAsync(null);
        var socket = webSocketContext.WebSocket;
        var sendLock = new SemaphoreSlim(1, 1);

        session = new ConPtySession(options.Command, options.WorkingDirectory, options.Columns, options.Rows);
        session.OutputReceived += text => _ = SendWebSocketTextAsync(socket, sendLock, text);
        session.Exited += exitCode => _ = SendWebSocketTextAsync(socket, sendLock, $"\r\n[process exited: {exitCode}]\r\n");

        try
        {
            session.Start();
            await ReceiveWebSocketInputAsync(socket);
        }
        finally
        {
            session.Dispose();
            session = null;
            sendLock.Dispose();
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
            }
        }
    }

    private async Task ReceiveWebSocketInputAsync(WebSocket socket)
    {
        var buffer = new byte[8192];
        while (socket.State == WebSocketState.Open)
        {
            using var messageStream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                messageStream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            var text = Encoding.UTF8.GetString(messageStream.ToArray());
            await HandleWebSocketClientMessageAsync(text);
        }
    }

    private async Task HandleWebSocketClientMessageAsync(string text)
    {
        var message = BridgeMessage.TryFromJson(text);
        if (message == null)
        {
            await session!.WriteInputAsync(text);
            return;
        }

        switch (message.Type)
        {
            case "input":
                await session!.WriteInputAsync(NormalizeTerminalInput(message.Data));
                break;
            case "resize":
                ResizeSession(message.Data);
                break;
            default:
                await session!.WriteInputAsync(text);
                break;
        }
    }

    private void ResizeSession(string data)
    {
        var parts = data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return;
        }

        if (short.TryParse(parts[0], out var columns) &&
            short.TryParse(parts[1], out var rows))
        {
            session?.Resize(columns, rows);
        }
    }

    private static async Task SendWebSocketTextAsync(WebSocket socket, SemaphoreSlim sendLock, string text)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        await sendLock.WaitAsync();
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static string NormalizeTerminalInput(string input)
    {
        return input.Replace("\r\n", "\r").Replace("\n", "\r");
    }

    private static string BuildTerminalHtml(int webPort)
    {
        return $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Claude Code Terminal</title>
  <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/css/xterm.css">
  <style>
    html, body, #terminal {
      width: 100%;
      height: 100%;
      margin: 0;
      background: #0c0c0c;
      overflow: hidden;
      max-width: 100vw;
      overscroll-behavior: none;
    }

    html, body {
      position: fixed;
      inset: 0;
    }

    .xterm {
      width: 100%;
      height: 100%;
      padding: 8px;
      box-sizing: border-box;
      overflow: hidden;
    }

    #terminal {
      position: fixed;
      inset: 0;
      contain: strict;
    }

    .xterm-viewport,
    .xterm-screen {
      max-width: 100% !important;
      overflow-x: hidden !important;
    }

    .xterm .composition-view,
    .xterm .composition-view.active {
      display: block !important;
      width: 1px !important;
      max-width: 1px !important;
      height: 1em !important;
      max-height: 1em !important;
      overflow: hidden !important;
      white-space: nowrap !important;
      color: transparent !important;
      background: transparent !important;
      opacity: 0.01 !important;
      box-sizing: border-box !important;
      contain: paint !important;
      pointer-events: none !important;
    }

    #status {
      position: fixed;
      right: 10px;
      top: 8px;
      z-index: 2;
      font: 12px/1.4 Consolas, monospace;
      color: #9ca3af;
      background: rgba(12, 12, 12, 0.72);
      padding: 3px 7px;
      border: 1px solid #333;
      border-radius: 4px;
      pointer-events: none;
    }
  </style>
</head>
<body>
  <div id="terminal"></div>
  <div id="status">connecting...</div>
  <script src="https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/lib/xterm.js"></script>
  <script src="https://cdn.jsdelivr.net/npm/@xterm/addon-fit@0.10.0/lib/addon-fit.js"></script>
  <script>
    const status = document.getElementById('status');
    const terminalElement = document.getElementById('terminal');
    const terminal = new Terminal({
      cursorBlink: true,
      convertEol: false,
      allowProposedApi: true,
      fontFamily: 'Cascadia Mono, Consolas, Menlo, monospace',
      fontSize: 14,
      theme: {
        background: '#0c0c0c',
        foreground: '#cccccc',
        cursor: '#f5f5f5',
        selectionBackground: '#264f78'
      }
    });

    const fitAddon = new FitAddon.FitAddon();
    terminal.loadAddon(fitAddon);
    terminal.open(terminalElement);
    fitAddon.fit();
    terminal.focus();

    const socket = new WebSocket(`ws://127.0.0.1:{{webPort}}/ws`);

    function sendBridgeMessage(type, data) {
      if (socket.readyState === WebSocket.OPEN) {
        socket.send(JSON.stringify({ type, data }));
      }
    }

    function fitTerminal() {
      fitAddon.fit();
      schedulePinHorizontalScroll();
      sendBridgeMessage('resize', `${terminal.cols} ${terminal.rows}`);
    }
    window.fitTerminal = fitTerminal;

    let pendingHorizontalPin = false;
    let isComposingWithIme = false;
    let pendingCompositionClamp = false;
    let pendingPaintRefresh = false;
    function schedulePinHorizontalScroll() {
      if (pendingHorizontalPin) {
        return;
      }

      pendingHorizontalPin = true;
      requestAnimationFrame(() => {
        pendingHorizontalPin = false;
        pinHorizontalScroll();
      });
    }

    function pinHorizontalScroll() {
      document.documentElement.scrollLeft = 0;
      document.body.scrollLeft = 0;
      terminalElement.scrollLeft = 0;

      const viewport = terminalElement.querySelector('.xterm-viewport');
      const screen = terminalElement.querySelector('.xterm-screen');
      if (viewport) viewport.scrollLeft = 0;
      if (screen) screen.scrollLeft = 0;
    }

    function scheduleTerminalPaintRefresh() {
      if (pendingPaintRefresh) {
        return;
      }

      pendingPaintRefresh = true;
      requestAnimationFrame(() => {
        pendingPaintRefresh = false;
        schedulePinHorizontalScroll();
        terminal.focus();
        if (typeof terminal.refresh === 'function') {
          terminal.refresh(0, Math.max(0, terminal.rows - 1));
        }
        window.dispatchEvent(new Event('resize'));
      });
    }

    function scheduleCompositionClamp() {
      if (pendingCompositionClamp) {
        return;
      }

      pendingCompositionClamp = true;
      requestAnimationFrame(() => {
        pendingCompositionClamp = false;
        clampCompositionView();
      });
    }

    function clampCompositionView() {
      const view = terminalElement.querySelector('.composition-view');
      if (!view) {
        return;
      }

      view.style.display = 'block';
      view.style.width = '1px';
      view.style.maxWidth = '1px';
      view.style.height = '1em';
      view.style.maxHeight = '1em';
      view.style.overflow = 'hidden';
      view.style.whiteSpace = 'nowrap';
      view.style.color = 'transparent';
      view.style.background = 'transparent';
      view.style.opacity = '0.01';
    }

    function pinAfterImeCommit() {
      isComposingWithIme = false;
      schedulePinHorizontalScroll();
      setTimeout(schedulePinHorizontalScroll, 50);
      setTimeout(schedulePinHorizontalScroll, 150);
    }

    socket.addEventListener('open', () => {
      status.textContent = 'connected';
      fitTerminal();
      terminal.focus();
    });

    socket.addEventListener('message', event => terminal.write(event.data));
    socket.addEventListener('close', () => {
      status.textContent = 'closed';
      terminal.write('\r\n[connection closed]\r\n');
    });
    socket.addEventListener('error', () => {
      status.textContent = 'error';
      terminal.write('\r\n[websocket error]\r\n');
    });

    function sendTerminalInput(data) {
      schedulePinHorizontalScroll();
      sendBridgeMessage('input', data);
    }

    terminal.onData(data => {
      sendTerminalInput(data);
    });

    function pasteIntoTerminal(text) {
      if (!text) {
        return;
      }

      schedulePinHorizontalScroll();
      if (typeof terminal.paste === 'function') {
        terminal.paste(text);
        return;
      }

      sendBridgeMessage('input', text);
    }

    async function pasteClipboardText() {
      try {
        const text = await navigator.clipboard.readText();
        pasteIntoTerminal(text);
      } catch {
        terminal.write('\r\n[clipboard paste failed]\r\n');
      }
    }

    async function copySelectionToClipboard() {
      const text = terminal.getSelection();
      if (!text) {
        return false;
      }

      try {
        await navigator.clipboard.writeText(text);
        return true;
      } catch {
        return false;
      }
    }

    function getControlCharacter(key) {
      if (!key || key.length !== 1) {
        return null;
      }

      const lower = key.toLowerCase();
      if (lower >= 'a' && lower <= 'z') {
        return String.fromCharCode(lower.charCodeAt(0) - 96);
      }

      switch (key) {
        case '[':
          return '\x1b';
        case '\\':
          return '\x1c';
        case ']':
          return '\x1d';
        case '^':
        case '6':
          return '\x1e';
        case '_':
        case '-':
          return '\x1f';
        case '?':
          return '\x7f';
        default:
          return null;
      }
    }

    terminal.attachCustomKeyEventHandler(event => {
      if (event.type !== 'keydown') {
        return true;
      }

      const usesControlModifier = event.ctrlKey || event.metaKey;
      if (!usesControlModifier || event.altKey || !event.key) {
        return true;
      }

      const key = event.key.toLowerCase();
      if (key === 'v') {
        event.preventDefault();
        pasteClipboardText();
        return false;
      }

      if (key === 'c' && typeof terminal.hasSelection === 'function' && terminal.hasSelection()) {
        event.preventDefault();
        copySelectionToClipboard();
        return false;
      }

      const controlCharacter = getControlCharacter(event.key);
      if (controlCharacter !== null) {
        event.preventDefault();
        sendTerminalInput(controlCharacter);
        scheduleTerminalPaintRefresh();
        return false;
      }

      return true;
    });

    terminalElement.addEventListener('paste', event => {
      const text = event.clipboardData && event.clipboardData.getData('text/plain');
      if (!text) {
        return;
      }

      event.preventDefault();
      pasteIntoTerminal(text);
    });

    document.addEventListener('compositionstart', () => {
      isComposingWithIme = true;
      scheduleCompositionClamp();
    }, true);
    document.addEventListener('compositionupdate', scheduleCompositionClamp, true);
    document.addEventListener('compositionend', pinAfterImeCommit, true);
    document.addEventListener('input', () => {
      if (isComposingWithIme) {
        scheduleCompositionClamp();
      } else {
        schedulePinHorizontalScroll();
      }
    }, true);
    document.addEventListener('scroll', () => {
      if (!isComposingWithIme) {
        schedulePinHorizontalScroll();
      }
    }, true);

    window.addEventListener('resize', () => {
      fitTerminal();
    });
  </script>
</body>
</html>
""";
    }

    public void Dispose()
    {
        session?.Dispose();
        try
        {
            if (listener.IsListening)
            {
                listener.Stop();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (HttpListenerException)
        {
        }

        listener.Close();
    }
}

internal sealed class BridgeServer : IDisposable
{
    private readonly CommandLineOptions options;
    private readonly TcpListener listener;
    private ITerminalSession? session;

    public BridgeServer(CommandLineOptions options)
    {
        this.options = options;
        listener = new TcpListener(IPAddress.Loopback, options.Port);
    }

    public async Task RunAsync()
    {
        listener.Start();

        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };

        await SendAsync(writer, "status", "starting");

        session = new ConPtySession(options.Command, options.WorkingDirectory, options.Columns, options.Rows);
        session.OutputReceived += text => _ = SendAsync(writer, "output", text);
        session.Exited += exitCode => _ = SendAsync(writer, "status", $"exited:{exitCode}");

        try
        {
            session.Start();
            await SendAsync(writer, "status", "running");

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                var message = BridgeMessage.FromJson(line);
                if (message.Type == "input")
                {
                    await session.WriteInputAsync(NormalizeTerminalInput(message.Data));
                }
                else if (message.Type == "stop")
                {
                    session.Stop();
                    await SendAsync(writer, "status", "stopped");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            await SendAsync(writer, "error", ex.Message);
            Console.Error.WriteLine(ex);
        }
    }

    private static Task SendAsync(StreamWriter writer, string type, string data)
    {
        return writer.WriteLineAsync(new BridgeMessage(type, data).ToJson());
    }

    private static string NormalizeTerminalInput(string input)
    {
        return input.Replace("\r\n", "\r").Replace("\n", "\r");
    }

    public void Dispose()
    {
        session?.Dispose();
        try
        {
            listener.Stop();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }
}

internal interface ITerminalSession : IDisposable
{
    event Action<string>? OutputReceived;
    event Action<int>? Exited;
    void Start();
    Task WriteInputAsync(string text);
    void Resize(short columns, short rows);
    void Stop();
}

internal sealed class RedirectedProcessSession : ITerminalSession
{
    private readonly string command;
    private readonly string workingDirectory;
    private Process? process;

    public event Action<string>? OutputReceived;
    public event Action<int>? Exited;

    public RedirectedProcessSession(string command, string workingDirectory)
    {
        this.command = command;
        this.workingDirectory = workingDirectory;
    }

    public void Start()
    {
        var startInfo = BuildStartInfo(command, workingDirectory);
        process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.Exited += (_, _) => Exited?.Invoke(process.ExitCode);
        process.Start();

        _ = Task.Run(() => ReadStreamAsync(process.StandardOutput));
        _ = Task.Run(() => ReadStreamAsync(process.StandardError));
    }

    public async Task WriteInputAsync(string text)
    {
        if (process == null || process.HasExited)
        {
            return;
        }

        await process.StandardInput.WriteAsync(text);
        await process.StandardInput.FlushAsync();
    }

    public void Stop()
    {
        if (process == null || process.HasExited)
        {
            return;
        }

        try
        {
            process.StandardInput.WriteLine("exit");
            process.StandardInput.Flush();
            if (!process.WaitForExit(1000))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    public void Resize(short columns, short rows)
    {
    }

    public void Dispose()
    {
        Stop();
        process?.Dispose();
    }

    private async Task ReadStreamAsync(StreamReader reader)
    {
        var buffer = new char[2048];
        while (process != null && !process.HasExited)
        {
            var count = await reader.ReadAsync(buffer, 0, buffer.Length);
            if (count <= 0)
            {
                break;
            }

            OutputReceived?.Invoke(new string(buffer, 0, count));
        }
    }

    private static ProcessStartInfo BuildStartInfo(string configuredCommand, string workingDirectory)
    {
        var trimmed = string.IsNullOrWhiteSpace(configuredCommand) ? "claude" : configuredCommand.Trim();
        var shell = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(shell))
        {
            shell = "cmd.exe";
        }

        var lower = trimmed.ToLowerInvariant();
        var arguments = lower.StartsWith("cmd", StringComparison.Ordinal)
            ? trimmed.Substring(trimmed.IndexOf(' ') >= 0 ? trimmed.IndexOf(' ') : trimmed.Length).Trim()
            : $"/k \"chcp 65001>nul && {trimmed}\"";

        if (string.IsNullOrWhiteSpace(arguments))
        {
            arguments = "/k";
        }

        return new ProcessStartInfo
        {
            FileName = shell,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        };
    }
}

internal sealed class ConPtySession : ITerminalSession
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private static readonly IntPtr ProcThreadAttributePseudoConsole = new(0x00020016);

    private readonly string command;
    private readonly string workingDirectory;
    private readonly SemaphoreSlim inputLock = new(1, 1);

    private IntPtr pseudoConsole;
    private IntPtr processHandle;
    private IntPtr threadHandle;
    private IntPtr pseudoConsoleInputRead;
    private IntPtr pseudoConsoleOutputWrite;
    private FileStream? inputWriter;
    private FileStream? outputReader;
    private CancellationTokenSource? cancellation;

    public event Action<string>? OutputReceived;
    public event Action<int>? Exited;

    public ConPtySession(string command, string workingDirectory, short columns, short rows)
    {
        this.command = command;
        this.workingDirectory = workingDirectory;
        Columns = columns;
        Rows = rows;
    }

    private short Columns { get; set; }
    private short Rows { get; set; }

    public void Start()
    {
        cancellation = new CancellationTokenSource();

        CreatePipePair(out var inputRead, out var inputWrite);
        CreatePipePair(out var outputRead, out var outputWrite);

        var hr = CreatePseudoConsole(new Coord(Columns, Rows), inputRead, outputWrite, 0, out pseudoConsole);
        pseudoConsoleInputRead = inputRead;
        pseudoConsoleOutputWrite = outputWrite;

        if (hr != 0)
        {
            SafeClose(inputWrite);
            SafeClose(outputRead);
            SafeClose(pseudoConsoleInputRead);
            SafeClose(pseudoConsoleOutputWrite);
            pseudoConsoleInputRead = IntPtr.Zero;
            pseudoConsoleOutputWrite = IntPtr.Zero;
            Marshal.ThrowExceptionForHR(hr);
        }

        inputWriter = new FileStream(new SafeFileHandle(inputWrite, ownsHandle: true), FileAccess.Write, 4096, isAsync: false);
        outputReader = new FileStream(new SafeFileHandle(outputRead, ownsHandle: true), FileAccess.Read, 4096, isAsync: false);

        CreateAttachedProcess();

        _ = Task.Run(() => ReadOutputAsync(cancellation.Token));
        _ = Task.Run(() => WaitForExitAsync(cancellation.Token));
    }

    public async Task WriteInputAsync(string text)
    {
        if (inputWriter == null)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        await inputLock.WaitAsync();
        try
        {
            await inputWriter.WriteAsync(bytes, 0, bytes.Length);
            await inputWriter.FlushAsync();
        }
        finally
        {
            inputLock.Release();
        }
    }

    public void Stop()
    {
        cancellation?.Cancel();

        if (processHandle != IntPtr.Zero)
        {
            TerminateProcess(processHandle, 0);
        }
    }

    public void Resize(short columns, short rows)
    {
        columns = Math.Max((short)20, columns);
        rows = Math.Max((short)5, rows);

        Columns = columns;
        Rows = rows;

        if (pseudoConsole == IntPtr.Zero)
        {
            return;
        }

        ResizePseudoConsole(pseudoConsole, new Coord(columns, rows));
    }

    private async Task ReadOutputAsync(CancellationToken token)
    {
        if (outputReader == null)
        {
            return;
        }

        var buffer = new byte[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

        while (!token.IsCancellationRequested)
        {
            var count = await outputReader.ReadAsync(buffer, 0, buffer.Length, token);
            if (count <= 0)
            {
                break;
            }

            var charCount = decoder.GetChars(buffer, 0, count, chars, 0);
            if (charCount > 0)
            {
                OutputReceived?.Invoke(new string(chars, 0, charCount));
            }
        }
    }

    private Task WaitForExitAsync(CancellationToken token)
    {
        if (processHandle == IntPtr.Zero)
        {
            return Task.CompletedTask;
        }

        WaitForSingleObject(processHandle, uint.MaxValue);
        if (!token.IsCancellationRequested && GetExitCodeProcess(processHandle, out var exitCode))
        {
            Exited?.Invoke(unchecked((int)exitCode));
        }

        return Task.CompletedTask;
    }

    private void CreateAttachedProcess()
    {
        var startupInfo = new StartupInfoEx();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();

        var attributeListSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
        startupInfo.lpAttributeList = Marshal.AllocHGlobal(attributeListSize);

        if (!InitializeProcThreadAttributeList(startupInfo.lpAttributeList, 1, 0, ref attributeListSize))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (!UpdateProcThreadAttribute(
                    startupInfo.lpAttributeList,
                    0,
                    ProcThreadAttributePseudoConsole,
                    pseudoConsole,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var processInfo = new ProcessInformation();
            var commandLine = new StringBuilder(BuildCommandLine(command));

            var created = CreateProcess(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                true,
                ExtendedStartupInfoPresent,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out processInfo);

            if (!created)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            processHandle = processInfo.hProcess;
            threadHandle = processInfo.hThread;
        }
        finally
        {
            DeleteProcThreadAttributeList(startupInfo.lpAttributeList);
            Marshal.FreeHGlobal(startupInfo.lpAttributeList);
        }
    }

    private static string BuildCommandLine(string configuredCommand)
    {
        var trimmed = string.IsNullOrWhiteSpace(configuredCommand) ? "claude" : configuredCommand.Trim();
        var lower = trimmed.ToLowerInvariant();
        if (lower.StartsWith("cmd", StringComparison.Ordinal) ||
            lower.StartsWith("powershell", StringComparison.Ordinal) ||
            lower.StartsWith("pwsh", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var shell = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(shell))
        {
            shell = "cmd.exe";
        }

        return $"\"{shell}\" /k \"chcp 65001>nul && {trimmed}\"";
    }

    private static void CreatePipePair(out IntPtr readPipe, out IntPtr writePipe)
    {
        var securityAttributes = new SecurityAttributes
        {
            nLength = Marshal.SizeOf<SecurityAttributes>(),
            bInheritHandle = false
        };

        if (!CreatePipe(out readPipe, out writePipe, ref securityAttributes, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        Stop();
        inputWriter?.Dispose();
        outputReader?.Dispose();
        inputLock.Dispose();
        cancellation?.Dispose();

        if (pseudoConsole != IntPtr.Zero)
        {
            ClosePseudoConsole(pseudoConsole);
            pseudoConsole = IntPtr.Zero;
        }

        SafeClose(pseudoConsoleInputRead);
        SafeClose(pseudoConsoleOutputWrite);
        SafeClose(threadHandle);
        SafeClose(processHandle);
        pseudoConsoleInputRead = IntPtr.Zero;
        pseudoConsoleOutputWrite = IntPtr.Zero;
        threadHandle = IntPtr.Zero;
        processHandle = IntPtr.Zero;
    }

    private static void SafeClose(IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SecurityAttributes lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        ref StartupInfoEx lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord
    {
        public readonly short X;
        public readonly short Y;

        public Coord(short x, short y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }
}

internal sealed class CommandLineOptions
{
    public int Port { get; private set; } = 50557;
    public int WebPort { get; private set; }
    public string Command { get; private set; } = "claude";
    public string WorkingDirectory { get; private set; } = Environment.CurrentDirectory;
    public short Columns { get; private set; } = 120;
    public short Rows { get; private set; } = 30;

    public static CommandLineOptions Parse(string[] args)
    {
        var result = new CommandLineOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            var value = i + 1 < args.Length ? args[i + 1] : string.Empty;

            switch (key)
            {
                case "--port" when int.TryParse(value, out var port):
                    result.Port = port;
                    i++;
                    break;
                case "--web-port" when int.TryParse(value, out var webPort):
                    result.WebPort = webPort;
                    i++;
                    break;
                case "--command" when !string.IsNullOrWhiteSpace(value):
                    result.Command = value;
                    i++;
                    break;
                case "--working-directory" when !string.IsNullOrWhiteSpace(value):
                    result.WorkingDirectory = value;
                    i++;
                    break;
                case "--columns" when short.TryParse(value, out var columns):
                    result.Columns = columns;
                    i++;
                    break;
                case "--rows" when short.TryParse(value, out var rows):
                    result.Rows = rows;
                    i++;
                    break;
            }
        }

        return result;
    }
}

internal sealed class BridgeMessage
{
    public string type { get; set; } = string.Empty;
    public string data { get; set; } = string.Empty;

    public BridgeMessage()
    {
    }

    public BridgeMessage(string type, string data)
    {
        this.type = type;
        this.data = data;
    }

    [JsonIgnore]
    public string Type => type ?? string.Empty;

    [JsonIgnore]
    public string Data => data ?? string.Empty;

    public string ToJson()
    {
        return JsonSerializer.Serialize(this);
    }

    public static BridgeMessage FromJson(string json)
    {
        return JsonSerializer.Deserialize<BridgeMessage>(json) ?? new BridgeMessage();
    }

    public static BridgeMessage? TryFromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<BridgeMessage>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
