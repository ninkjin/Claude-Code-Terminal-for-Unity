using System.Net;
using System.Net.Sockets;

namespace ClaudeTerminalWebViewHost;

public sealed class TerminalHostControlServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly Action<int, int, int, int> applyBounds;
    private readonly CancellationTokenSource cancellation = new();

    public TerminalHostControlServer(int port, Action<int, int, int, int> applyBounds)
    {
        listener = new TcpListener(IPAddress.Loopback, port);
        this.applyBounds = applyBounds;
    }

    public void Start()
    {
        listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        using (var reader = new StreamReader(client.GetStream()))
        {
            while (!cancellation.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if (line == null)
                {
                    break;
                }

                HandleLine(line);
            }
        }
    }

    private void HandleLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || parts[0] != "bounds")
        {
            return;
        }

        if (int.TryParse(parts[1], out var left) &&
            int.TryParse(parts[2], out var top) &&
            int.TryParse(parts[3], out var width) &&
            int.TryParse(parts[4], out var height))
        {
            applyBounds(left, top, width, height);
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        listener.Stop();
        cancellation.Dispose();
    }
}
