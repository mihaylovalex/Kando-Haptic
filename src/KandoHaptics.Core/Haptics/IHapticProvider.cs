namespace KandoHaptics.Haptics;

/// <summary>
/// Источник физической тактильной отдачи. В плагине это LogitechHapticProvider
/// (PluginEvents.RaiseEvent), в тестах — MockHapticProvider.
/// </summary>
public interface IHapticProvider
{
    /// <summary>Воспроизвести waveform из <see cref="HapticWaveform.All"/>. Может бросать исключения.</summary>
    void Play(string waveform);
}
