namespace KandoHaptics.Ipc;

using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using KandoHaptics.Logging;

public enum KandoConnectionState
{
    Stopped,
    WaitingForKando,
    Connecting,
    Connected,
}

public sealed class KandoIpcClientOptions
{
    public string IpcInfoPath { get; init; } = KandoIpcInfo.DefaultPath;

    public TimeSpan MinReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(15);

    public int MaxMessageBytes { get; init; } = 64 * 1024;

    /// <summary>Следить за <c>ipc-info.json</c>, чтобы подключаться сразу после запуска Kando.</summary>
    public bool WatchIpcInfoFile { get; init; } = true;
}

/// <summary>
/// Клиент Kando WebSocket IPC API v2. Находит Kando через <c>ipc-info.json</c>,
/// подключается только к 127.0.0.1, отправляет <c>start-observing</c> и передаёт
/// события <c>menu-interaction</c> в обработчик. После потери соединения переподключается
/// с ограниченной экспоненциальной задержкой (по умолчанию 1–10 с), каждый раз заново
/// читая файл, поэтому смена порта после перезапуска Kando подхватывается автоматически.
/// </summary>
public sealed class KandoIpcClient : IAsyncDisposable
{
    private static readonly byte[] StartObservingMessage = Encoding.UTF8.GetBytes("{\"type\":\"start-observing\"}");
    private static readonly byte[] StopObservingMessage = Encoding.UTF8.GetBytes("{\"type\":\"stop-observing\"}");

    private readonly KandoIpcClientOptions _options;
    private readonly Action<MenuInteractionEvent> _onInteraction;
    private readonly IKandoLogger _log;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private FileSystemWatcher? _watcher;
    private TaskCompletionSource _wake = NewWake();
    private ClientWebSocket? _activeSocket;
    private volatile KandoConnectionState _state = KandoConnectionState.Stopped;
    private string? _lastProblem;
    private int? _lastPort;
    private int _invalidMessageCount;

    public KandoIpcClient(KandoIpcClientOptions options, Action<MenuInteractionEvent> onInteraction, IKandoLogger log)
    {
        this._options = options;
        this._onInteraction = onInteraction;
        this._log = log;
    }

    public event Action<KandoConnectionState>? StateChanged;

    public KandoConnectionState State => this._state;

    /// <summary>Порт текущего соединения или <c>null</c>, если соединения нет.</summary>
    public int? ConnectedPort { get; private set; }

    /// <summary>Число успешных подключений с момента старта (для диагностики и тестов).</summary>
    public int ConnectionCount { get; private set; }

    public void Start()
    {
        lock (this._sync)
        {
            if (this._loop is not null)
            {
                return;
            }

            this._cts = new CancellationTokenSource();
            var token = this._cts.Token;
            this._loop = Task.Run(() => this.RunAsync(token));
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;
        lock (this._sync)
        {
            loop = this._loop;
            cts = this._cts;
            this._loop = null;
            this._cts = null;
        }

        if (loop is null || cts is null)
        {
            return;
        }

        // Сначала пытаемся закрыть соединение корректно (stop-observing + close frame),
        // затем отменяем всё остальное.
        await this.TryCloseActiveSocketGracefullyAsync().ConfigureAwait(false);
        cts.Cancel();
        try
        {
            await loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            this._log.Warning("IPC-цикл не завершился вовремя при остановке.");
        }
        finally
        {
            cts.Dispose();
            this.DisposeWatcher();
            this.SetState(KandoConnectionState.Stopped);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync().ConfigureAwait(false);
        this._sendLock.Dispose();
    }

    internal static TimeSpan ComputeBackoff(int failures, TimeSpan min, TimeSpan max)
    {
        var exponent = Math.Clamp(failures, 0, 16);
        var ticks = min.Ticks * (1L << exponent);
        return TimeSpan.FromTicks(Math.Min(ticks, max.Ticks));
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task RunAsync(CancellationToken ct)
    {
        this._log.Info($"IPC-клиент запущен, файл обнаружения: {this._options.IpcInfoPath}");
        var failures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                this.EnsureWatcher();
                var result = KandoIpcInfoReader.Read(this._options.IpcInfoPath);
                if (!result.IsOk)
                {
                    this.SetState(KandoConnectionState.WaitingForKando);
                    this.ReportIpcInfoProblem(result);
                    await this.WaitBeforeRetryAsync(ComputeBackoff(failures++, this._options.MinReconnectDelay, this._options.MaxReconnectDelay), ct).ConfigureAwait(false);
                    continue;
                }

                var info = result.Info!;
                var connected = false;
                try
                {
                    connected = await this.RunSessionAsync(info, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is WebSocketException or IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
                {
                    this.ReportProblem($"connect:{info.Port}", LogLevel.Info, $"Kando недоступен на порту {info.Port}: {Describe(ex)}. Повтор позже.");
                }

                if (connected)
                {
                    failures = 0;
                }

                this.SetState(KandoConnectionState.WaitingForKando);
                await this.WaitBeforeRetryAsync(ComputeBackoff(failures++, this._options.MinReconnectDelay, this._options.MaxReconnectDelay), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Последний рубеж: IPC-цикл не должен ронять плагин.
            this._log.Error("Непредвиденная ошибка IPC-цикла; клиент остановлен.", ex);
        }
        finally
        {
            this.SetState(KandoConnectionState.Stopped);
            this._log.Info("IPC-клиент остановлен.");
        }
    }

    /// <returns><c>true</c>, если соединение было установлено (даже если потом разорвано).</returns>
    private async Task<bool> RunSessionAsync(KandoIpcInfo info, CancellationToken ct)
    {
        this.SetState(KandoConnectionState.Connecting);

        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null; // loopback: системный прокси не должен вмешиваться
        socket.Options.KeepAliveInterval = this._options.KeepAliveInterval;

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(this._options.ConnectTimeout);
            try
            {
                await socket.ConnectAsync(info.WebSocketUri, connectCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"таймаут подключения {this._options.ConnectTimeout.TotalSeconds:0.#} с");
            }
        }

        await this.SendAsync(socket, StartObservingMessage, ct).ConfigureAwait(false);

        if (this._lastPort is { } previous && previous != info.Port)
        {
            this._log.Info($"Порт Kando изменился: {previous} → {info.Port}.");
        }

        this._lastPort = info.Port;
        this._lastProblem = null;
        this.ConnectedPort = info.Port;
        this.ConnectionCount++;
        this._activeSocket = socket;
        this.SetState(KandoConnectionState.Connected);
        this._log.Info($"Подключено к Kando IPC v{info.ApiVersion}: ws://127.0.0.1:{info.Port}, start-observing отправлен.");

        try
        {
            var reason = await this.ReceiveLoopAsync(socket, ct).ConfigureAwait(false);
            this._log.Info($"Соединение с Kando закрыто ({reason}).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is WebSocketException or IOException)
        {
            this._log.Info($"Соединение с Kando потеряно: {Describe(ex)}.");
        }
        finally
        {
            this._activeSocket = null;
            this.ConnectedPort = null;
        }

        return true;
    }

    private async Task<string> ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        var message = new ArrayBufferWriter<byte>(4096);
        var oversized = false;
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await TryCompleteCloseHandshakeAsync(socket).ConfigureAwait(false);
                    return socket.CloseStatus?.ToString() ?? "close frame";
                }

                if (!oversized)
                {
                    if (message.WrittenCount + result.Count > this._options.MaxMessageBytes)
                    {
                        oversized = true;
                        message.ResetWrittenCount();
                    }
                    else
                    {
                        message.Write(buffer.AsSpan(0, result.Count));
                    }
                }

                if (!result.EndOfMessage)
                {
                    continue; // фрагментированное сообщение: собираем дальше
                }

                if (oversized)
                {
                    this._log.Warning($"Сообщение Kando длиннее {this._options.MaxMessageBytes} байт пропущено.");
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    this.HandleMessage(message.WrittenMemory);
                }

                message.ResetWrittenCount();
                oversized = false;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void HandleMessage(ReadOnlyMemory<byte> utf8)
    {
        var parsed = KandoMessageParser.Parse(utf8);
        switch (parsed.Kind)
        {
            case KandoMessageKind.MenuInteraction:
                try
                {
                    this._onInteraction(parsed.Interaction!);
                }
                catch (Exception ex)
                {
                    this._log.Error($"Ошибка обработчика события {parsed.Interaction}.", ex);
                }

                break;
            case KandoMessageKind.Error:
                this._log.Warning($"Kando сообщил об ошибке IPC: {parsed.ErrorReason ?? "?"} — {parsed.ErrorDescription ?? string.Empty}");
                break;
            case KandoMessageKind.Unsupported:
                this._log.Debug($"Пропущено сообщение Kando неподдерживаемого типа '{parsed.Type}'.");
                break;
            default:
                // Не спамим журнал при потоке мусора; первые несколько — Warning.
                var count = Interlocked.Increment(ref this._invalidMessageCount);
                this._log.Log(count <= 5 ? LogLevel.Warning : LogLevel.Debug, "Получено некорректное сообщение Kando (не JSON или неверная структура); пропущено.");
                break;
        }
    }

    private async Task SendAsync(ClientWebSocket socket, byte[] payload, CancellationToken ct)
    {
        await this._sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            this._sendLock.Release();
        }
    }

    private async Task TryCloseActiveSocketGracefullyAsync()
    {
        var socket = this._activeSocket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await this.SendAsync(socket, StopObservingMessage, timeout.Token).ConfigureAwait(false);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "plugin unloading", timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // Соединение всё равно будет прервано отменой.
        }
    }

    private static async Task TryCompleteCloseHandshakeAsync(ClientWebSocket socket)
    {
        if (socket.State != WebSocketState.CloseReceived)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task WaitBeforeRetryAsync(TimeSpan delay, CancellationToken ct)
    {
        var wake = Volatile.Read(ref this._wake);
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delayTask = Task.Delay(delay, delayCts.Token);
        var completed = await Task.WhenAny(delayTask, wake.Task).ConfigureAwait(false);
        delayCts.Cancel();
        ct.ThrowIfCancellationRequested();

        if (completed == wake.Task)
        {
            Interlocked.CompareExchange(ref this._wake, NewWake(), wake);
            this._log.Debug("ipc-info.json изменился — повторная попытка подключения.");

            // Kando пишет файл чуть раньше, чем начинает слушать порт.
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }
    }

    private void EnsureWatcher()
    {
        if (!this._options.WatchIpcInfoFile || this._watcher is not null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(this._options.IpcInfoPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(directory, Path.GetFileName(this._options.IpcInfoPath))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            watcher.Created += this.OnIpcInfoChanged;
            watcher.Changed += this.OnIpcInfoChanged;
            watcher.Renamed += this.OnIpcInfoChanged;
            watcher.Error += (_, _) => this.DisposeWatcher();
            watcher.EnableRaisingEvents = true;
            this._watcher = watcher;
        }
        catch (Exception ex)
        {
            this._log.Debug($"Не удалось следить за ipc-info.json: {ex.Message}");
        }
    }

    private void OnIpcInfoChanged(object sender, FileSystemEventArgs e)
    {
        if (this._state != KandoConnectionState.Connected)
        {
            Volatile.Read(ref this._wake).TrySetResult();
        }
    }

    private void DisposeWatcher()
    {
        var watcher = Interlocked.Exchange(ref this._watcher, null);
        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }

    private void ReportIpcInfoProblem(IpcInfoReadResult result)
    {
        switch (result.Status)
        {
            case IpcInfoStatus.NotFound:
                this.ReportProblem("notfound", LogLevel.Info, $"Kando не обнаружен (нет {this._options.IpcInfoPath}). Ожидание запуска Kando.");
                break;
            case IpcInfoStatus.UnsupportedApiVersion:
                this.ReportProblem($"version:{result.Info?.ApiVersion}", LogLevel.Warning, $"Неподдерживаемая версия Kando IPC API: {result.Detail}. Обновите Kando Haptics; плагин продолжит ждать.");
                break;
            default:
                this.ReportProblem($"info:{result.Status}", LogLevel.Warning, $"ipc-info.json некорректен ({result.Status}: {result.Detail}). Повтор позже.");
                break;
        }
    }

    /// <summary>Пишет проблему на заданном уровне только при её смене, повторы — в Debug.</summary>
    private void ReportProblem(string key, LogLevel level, string message)
    {
        if (this._lastProblem == key)
        {
            this._log.Debug(message);
            return;
        }

        this._lastProblem = key;
        this._log.Log(level, message);
    }

    private void SetState(KandoConnectionState state)
    {
        if (this._state == state)
        {
            return;
        }

        this._state = state;
        try
        {
            this.StateChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            this._log.Warning("Ошибка обработчика смены состояния IPC.", ex);
        }
    }

    private static string Describe(Exception ex) =>
        ex.InnerException is { } inner ? $"{ex.Message} ({inner.Message})" : ex.Message;
}
