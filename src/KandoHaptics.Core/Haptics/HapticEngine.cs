namespace KandoHaptics.Haptics;

using KandoHaptics.Ipc;
using KandoHaptics.Logging;

/// <summary>Запрос на тактильный эффект, сформированный обработчиком взаимодействий.</summary>
public sealed record HapticRequest(KandoInteraction Interaction, string Waveform, IReadOnlyList<int> Path, bool Trailing = false);

/// <summary>
/// Последнее звено перед SDK: проверяет waveform, вызывает провайдер и изолирует его
/// ошибки, чтобы сбой Logitech SDK не останавливал обработку следующих событий.
/// </summary>
public sealed class HapticEngine
{
    private const int ErrorLogBurst = 5;

    private readonly IHapticProvider _provider;
    private readonly IKandoLogger _log;
    private long _played;
    private long _failed;
    private int _consecutiveFailures;

    public HapticEngine(IHapticProvider provider, IKandoLogger log)
    {
        this._provider = provider;
        this._log = log;
    }

    public long PlayedCount => Interlocked.Read(ref this._played);

    public long FailedCount => Interlocked.Read(ref this._failed);

    public bool Play(HapticRequest request)
    {
        if (!HapticWaveform.IsValid(request.Waveform))
        {
            this._log.Warning($"Неизвестный waveform '{request.Waveform}' для {request.Interaction.ToWireName()}; эффект пропущен.");
            return false;
        }

        try
        {
            this._provider.Play(request.Waveform);
            Interlocked.Increment(ref this._played);
            Interlocked.Exchange(ref this._consecutiveFailures, 0);
            this._log.Debug(
                static r => $"Haptic: {r.Interaction.ToWireName()} {MenuInteractionEvent.FormatPath(r.Path)} -> {r.Waveform}{(r.Trailing ? " (отложенный)" : string.Empty)}",
                request);
            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref this._failed);
            var failures = Interlocked.Increment(ref this._consecutiveFailures);
            this._log.Log(
                failures <= ErrorLogBurst ? LogLevel.Error : LogLevel.Debug,
                $"Logitech SDK не смог воспроизвести '{request.Waveform}' ({request.Interaction.ToWireName()}).",
                ex);
            return false;
        }
    }
}
