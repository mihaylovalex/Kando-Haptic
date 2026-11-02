namespace Loupedeck.KandoHapticsPlugin
{
    using System;

    using KandoHaptics.Haptics;
    using KandoHaptics.Logging;

    /// <summary>
    /// Воспроизводит waveform через официальный механизм событий Logi Actions SDK:
    /// <c>PluginEvents.RaiseEvent("waveform_&lt;имя&gt;")</c>. Сопоставление события и
    /// waveform задано в package/events/extra/eventMapping.yaml (HasHapticMapping).
    /// Амплитуду SDK программно не регулирует — интенсивность задаётся в Logi Options+.
    /// </summary>
    internal sealed class LogitechHapticProvider : IHapticProvider
    {
        private readonly PluginEventSender _events;
        private readonly IKandoLogger _log;

        public LogitechHapticProvider(PluginEventSender events, IKandoLogger log)
        {
            this._events = events ?? throw new ArgumentNullException(nameof(events));
            this._log = log;
        }

        /// <summary>
        /// Регистрирует каталог событий в коде. Нужен только если в пакете нет
        /// events/DefaultEventSource.yaml: Logi Plugin Service 6.4 не объединяет одноимённые
        /// события из YAML и AddEvent и показывает их дважды (30 вместо 15).
        /// </summary>
        public void RegisterEvents(Boolean declaredInYaml)
        {
            if (declaredInYaml)
            {
                this._log.Info($"Haptic-события ({HapticWaveform.All.Count}) объявлены в DefaultEventSource.yaml.");
                return;
            }

            foreach (var waveform in HapticWaveform.All)
            {
                try
                {
                    this._events.AddEvent(
                        HapticWaveform.ToEventName(waveform),
                        HapticWaveform.DisplayName(waveform),
                        HapticWaveform.Description(waveform));
                }
                catch (Exception ex)
                {
                    this._log.Warning($"Не удалось зарегистрировать haptic-событие для '{waveform}'.", ex);
                }
            }

            this._log.Info($"Зарегистрировано haptic-событий в коде: {HapticWaveform.All.Count}.");
        }

        public void Play(String waveform) => this._events.RaiseEvent(HapticWaveform.ToEventName(waveform));
    }
}
