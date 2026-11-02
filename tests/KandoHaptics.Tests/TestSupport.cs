namespace KandoHaptics.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using KandoHaptics.Haptics;
using KandoHaptics.Logging;

/// <summary>Фиксирует каждый вызов: waveform, время, порядковый номер.</summary>
public sealed class MockHapticProvider : IHapticProvider
{
    private readonly ConcurrentQueue<Call> _calls = new();
    private readonly TimeProvider _time;
    private int _sequence;

    public MockHapticProvider(TimeProvider? time = null) => this._time = time ?? TimeProvider.System;

    public Func<string, Exception?>? FailWith { get; set; }

    public IReadOnlyList<Call> Calls => this._calls.ToArray();

    public IReadOnlyList<string> Waveforms => this._calls.Select(call => call.Waveform).ToArray();

    public int Count => this._calls.Count;

    public void Play(string waveform)
    {
        if (this.FailWith?.Invoke(waveform) is { } error)
        {
            throw error;
        }

        this._calls.Enqueue(new Call(waveform, this._time.GetUtcNow(), Interlocked.Increment(ref this._sequence)));
    }

    public void Clear() => this._calls.Clear();

    public sealed record Call(string Waveform, DateTimeOffset At, int Sequence);
}

public sealed class TestLogger : IKandoLogger
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => this._entries.ToArray();

    public bool IsEnabled(LogLevel level) => true;

    public void Log(LogLevel level, string message, Exception? exception = null) => this._entries.Enqueue((level, message));

    public bool Contains(LogLevel level, string fragment) =>
        this._entries.Any(entry => entry.Level == level && entry.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Временный каталог, удаляемый после теста.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        this.Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kh-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(this.Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(this.Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.Path, recursive: true);
        }
        catch
        {
        }
    }
}

/// <summary>
/// Минимальный WebSocket-сервер на 127.0.0.1, имитирующий Kando IPC: принимает
/// соединение, записывает полученные сообщения и отправляет события по команде теста.
/// </summary>
public sealed class FakeKandoServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<string> _received = new();
    private readonly Task _acceptLoop;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private WebSocket? _socket;
    private TcpClient? _client;
    private TaskCompletionSource _startObserving = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeKandoServer()
    {
        this._listener = new TcpListener(IPAddress.Loopback, 0);
        this._listener.Start();
        this.Port = ((IPEndPoint)this._listener.LocalEndpoint).Port;
        this._acceptLoop = Task.Run(this.AcceptLoopAsync);
    }

    public int Port { get; }

    public int AcceptedConnections { get; private set; }

    public IReadOnlyList<string> Received => this._received.ToArray();

    public void WriteIpcInfo(string path, int apiVersion = 2) =>
        System.IO.File.WriteAllText(path, $"{{\n  \"port\": {this.Port},\n  \"apiVersion\": {apiVersion}\n}}");

    public Task WaitForStartObservingAsync(TimeSpan? timeout = null) =>
        this._startObserving.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10));

    public Task WaitForConnectionAsync(TimeSpan? timeout = null) =>
        this._connected.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10));

    public async Task SendAsync(string json)
    {
        var socket = this._socket ?? throw new InvalidOperationException("no client");
        await this._sendLock.WaitAsync();
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            this._sendLock.Release();
        }
    }

    public Task SendInteractionAsync(string interaction, params int[] path) =>
        this.SendAsync($"{{\"type\":\"menu-interaction\",\"interaction\":\"{interaction}\",\"path\":[{string.Join(",", path)}]}}");

    /// <summary>Отправить сообщение несколькими WebSocket-фрагментами.</summary>
    public async Task SendFragmentedAsync(string json, int chunkSize)
    {
        var socket = this._socket ?? throw new InvalidOperationException("no client");
        var bytes = Encoding.UTF8.GetBytes(json);
        await this._sendLock.WaitAsync();
        try
        {
            for (var offset = 0; offset < bytes.Length; offset += chunkSize)
            {
                var count = Math.Min(chunkSize, bytes.Length - offset);
                await socket.SendAsync(bytes.AsMemory(offset, count), WebSocketMessageType.Text, offset + count >= bytes.Length, CancellationToken.None);
            }
        }
        finally
        {
            this._sendLock.Release();
        }
    }

    /// <summary>Резко оборвать соединение (как при падении/закрытии Kando).</summary>
    public void DropConnection()
    {
        this._socket?.Abort();
        this._client?.Close();
        this._socket = null;
        this.ResetSignals();
    }

    public async Task CloseConnectionAsync()
    {
        if (this._socket is { State: WebSocketState.Open } socket)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
            }
            catch
            {
            }
        }

        this.ResetSignals();
    }

    public async ValueTask DisposeAsync()
    {
        this._cts.Cancel();
        this._listener.Stop();
        this._socket?.Abort();
        this._client?.Close();
        try
        {
            await this._acceptLoop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
    }

    private void ResetSignals()
    {
        this._startObserving = new(TaskCreationOptions.RunContinuationsAsynchronously);
        this._connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task AcceptLoopAsync()
    {
        while (!this._cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await this._listener.AcceptTcpClientAsync(this._cts.Token);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => this.HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var request = await ReadHttpRequestAsync(stream);
            var keyLine = request.Split("\r\n").FirstOrDefault(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
            if (keyLine is null)
            {
                client.Close();
                return;
            }

            var key = keyLine["Sec-WebSocket-Key:".Length..].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));

            var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
            this._client = client;
            this._socket = socket;
            this.AcceptedConnections++;
            this._connected.TrySetResult();

            var buffer = new byte[4096];
            var message = new MemoryStream();
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, this._cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    this._received.Enqueue("<close>");
                    try
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                    }
                    catch
                    {
                    }

                    break;
                }

                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    var text = Encoding.UTF8.GetString(message.ToArray());
                    message.SetLength(0);
                    this._received.Enqueue(text);
                    if (text.Contains("start-observing", StringComparison.Ordinal))
                    {
                        this._startObserving.TrySetResult();
                    }
                }
            }
        }
        catch
        {
            // Соединение оборвано тестом или клиентом.
        }
    }

    private static async Task<string> ReadHttpRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[1];
        var builder = new StringBuilder();
        while (!builder.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            builder.Append((char)buffer[0]);
        }

        return builder.ToString();
    }
}

public static class Eventually
{
    /// <summary>Ждёт выполнения условия (для асинхронных сценариев с реальной сетью).</summary>
    public static async Task TrueAsync(Func<bool> condition, string because, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail($"Условие не выполнено за {timeoutMs} мс: {because}");
    }
}

public static class RepoPaths
{
    /// <summary>Корень репозитория (каталог с KandoHaptics.sln).</summary>
    public static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "KandoHaptics.sln")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("KandoHaptics.sln not found");
        }
    }

    public static string PluginPackage => System.IO.Path.Combine(Root, "src", "KandoHaptics.Plugin", "package");
}
