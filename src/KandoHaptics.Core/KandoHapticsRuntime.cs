namespace KandoHaptics;

using System.Threading.Channels;
using KandoHaptics.Configuration;
using KandoHaptics.Haptics;
using KandoHaptics.Ipc;
using KandoHaptics.Logging;
using KandoHaptics.Processing;

public sealed class KandoHapticsRuntimeOptions
{
    public string SettingsPath { get; init; } = SettingsManager.DefaultPath;

    public bool WatchSettings { get; init; } = true;

    public KandoIpcClientOptions Ipc { get; init; } = new();

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// Собирает все компоненты: Kando IPC → очередь → InteractionProcessor → HapticEngine → провайдер.
/// Приём WebSocket и обработка событий разнесены: клиент только кладёт событие в очередь,
/// диспетчер обрабатывает его в отдельной задаче. Без зависимости от Logitech SDK.
/// </summary>
public sealed class KandoHapticsRuntime : IAsyncDisposable
{
    private readonly KandoHapticsRuntimeOptions _options;
    private readonly IKandoLogger _log;
    private readonly Channel<MenuInteractionEvent> _queue;
    private readonly object _sync = new();
    private Task? _dispatcher;
    private bool _started;
    private bool _stopped;

    public KandoHapticsRuntime(IHapticProvider provider, IKandoLogger log, KandoHapticsRuntimeOptions? options = null)
    {
        this._options = options ?? new KandoHapticsRuntimeOptions();
        this._log = log;
        this.Settings = new SettingsManager(this._options.SettingsPath, log);
        this.Engine = new HapticEngine(provider, log);
        this.Processor = new InteractionProcessor(() => this.Settings.Current, this.Engine, log, this._options.Time);

        // Старые события наведения бесполезны: при перегрузке выбрасываем самые старые.
        this._queue = Channel.CreateBounded<MenuInteractionEvent>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        this.Ipc = new KandoIpcClient(this._options.Ipc, e => this._queue.Writer.TryWrite(e), log);
    }

    public SettingsManager Settings { get; }

    public HapticEngine Engine { get; }

    public InteractionProcessor Processor { get; }

    public KandoIpcClient Ipc { get; }

    public void Start()
    {
        lock (this._sync)
        {
            if (this._started)
            {
                return;
            }

            this._started = true;
        }

        try
        {
            this.Settings.Initialize(this._options.WatchSettings);
        }
        catch (Exception ex)
        {
            // Нет доступа к %LOCALAPPDATA% — работаем на настройках по умолчанию.
            this._log.Error("Не удалось инициализировать settings.json; используются настройки по умолчанию.", ex);
        }

        this._dispatcher = Task.Run(this.DispatchLoopAsync);
        this.Ipc.Start();
    }

    /// <summary>Проверочный сигнал (команда «Test Haptic»).</summary>
    public bool PlayTest(string? waveform = null)
    {
        var effect = this.Settings.Current.For(KandoInteraction.SelectButton);
        return this.Engine.Play(new HapticRequest(KandoInteraction.SelectButton, waveform ?? effect.Waveform, []));
    }

    public async Task StopAsync()
    {
        lock (this._sync)
        {
            if (!this._started || this._stopped)
            {
                return;
            }

            this._stopped = true;
        }

        try
        {
            await this.Ipc.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this._log.Warning("Ошибка при остановке IPC-клиента.", ex);
        }

        this._queue.Writer.TryComplete();
        if (this._dispatcher is not null)
        {
            try
            {
                await this._dispatcher.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                this._log.Warning("Диспетчер событий не завершился вовремя.");
            }
        }

        this.Processor.Dispose();
        this.Settings.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync().ConfigureAwait(false);
        await this.Ipc.DisposeAsync().ConfigureAwait(false);
    }

    private async Task DispatchLoopAsync()
    {
        var reader = this._queue.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var e))
            {
                try
                {
                    this.Processor.Process(e);
                }
                catch (Exception ex)
                {
                    // Сбой одного события не останавливает обработку следующих.
                    this._log.Error($"Ошибка обработки события {e}.", ex);
                }
            }
        }
    }
}
