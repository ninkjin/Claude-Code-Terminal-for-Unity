using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeTerminal.Editor
{
    public sealed class ClaudeTerminalClient : IDisposable
    {
        private readonly ConcurrentQueue<Action> mainThreadEvents = new ConcurrentQueue<Action>();
        private TcpClient client;
        private StreamReader reader;
        private StreamWriter writer;
        private CancellationTokenSource cancellation;

        public bool IsConnected => client != null && client.Connected;

        public string LastError { get; private set; } = string.Empty;

        public event Action<string> OutputReceived;
        public event Action<string> StatusChanged;
        public event Action<string> ErrorReceived;

        public async Task ConnectAsync(int port, int timeoutMilliseconds = 8000)
        {
            DisposeConnectionOnly();
            cancellation = new CancellationTokenSource();

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            Exception lastException = null;

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    client = new TcpClient();
                    await client.ConnectAsync("127.0.0.1", port);
                    var stream = client.GetStream();
                    reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
                    writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true)
                    {
                        AutoFlush = true
                    };

                    _ = Task.Run(() => ReadLoopAsync(cancellation.Token));
                    return;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    await Task.Delay(200);
                }
            }

            LastError = lastException?.Message ?? "连接 bridge 超时。";
            EnqueueError(LastError);
        }

        public void Pump()
        {
            while (mainThreadEvents.TryDequeue(out var action))
            {
                action.Invoke();
            }
        }

        public Task SendInputAsync(string text)
        {
            return SendAsync(new ClaudeTerminalMessage("input", text));
        }

        public Task StopRemoteProcessAsync()
        {
            return SendAsync(new ClaudeTerminalMessage("stop"));
        }

        public void Dispose()
        {
            DisposeConnectionOnly();
        }

        private async Task SendAsync(ClaudeTerminalMessage message)
        {
            if (writer == null)
            {
                return;
            }

            try
            {
                await writer.WriteAsync(message.ToJsonLine());
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                EnqueueError(ex.Message);
            }
        }

        private async Task ReadLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null)
                    {
                        break;
                    }

                    var message = ClaudeTerminalMessage.FromJsonLine(line);
                    switch (message.Type)
                    {
                        case "output":
                            mainThreadEvents.Enqueue(() => OutputReceived?.Invoke(message.Data));
                            break;
                        case "status":
                            mainThreadEvents.Enqueue(() => StatusChanged?.Invoke(message.Data));
                            break;
                        case "error":
                            mainThreadEvents.Enqueue(() => ErrorReceived?.Invoke(message.Data));
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    LastError = ex.Message;
                    EnqueueError(ex.Message);
                }
            }
        }

        private void EnqueueError(string message)
        {
            mainThreadEvents.Enqueue(() => ErrorReceived?.Invoke(message));
        }

        private void DisposeConnectionOnly()
        {
            cancellation?.Cancel();
            writer?.Dispose();
            reader?.Dispose();
            client?.Close();
            cancellation?.Dispose();
            writer = null;
            reader = null;
            client = null;
            cancellation = null;
        }
    }
}
