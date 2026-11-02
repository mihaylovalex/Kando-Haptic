namespace KandoHaptics.Haptics;

/// <summary>
/// Каталог официальных waveform Logitech. Для каждого waveform в пакете плагина
/// заранее зарегистрировано событие <c>waveform_&lt;имя&gt;</c> (DefaultEventSource.yaml),
/// сопоставленное этому waveform в eventMapping.yaml. Благодаря этому эффект для события
/// Kando меняется через settings.json без пересборки и без перезагрузки eventMapping.yaml,
/// который Logi Plugin Service читает только при загрузке плагина.
/// </summary>
public static class HapticWaveform
{
    public const string EventPrefix = "waveform_";

    public const string SharpStateChange = "sharp_state_change";
    public const string DampStateChange = "damp_state_change";
    public const string SharpCollision = "sharp_collision";
    public const string DampCollision = "damp_collision";
    public const string SubtleCollision = "subtle_collision";
    public const string HappyAlert = "happy_alert";
    public const string AngryAlert = "angry_alert";
    public const string Completed = "completed";
    public const string Square = "square";
    public const string Wave = "wave";
    public const string Firework = "firework";
    public const string Mad = "mad";
    public const string Knock = "knock";
    public const string Jingle = "jingle";
    public const string Ringing = "ringing";

    private static readonly (string Name, string DisplayName, string Description)[] Catalog =
    [
        (SharpStateChange, "Sharp State Change", "Short, high-intensity pulse for discrete state transitions"),
        (DampStateChange, "Damp State Change", "Gradual intensity change for smooth state transitions"),
        (SharpCollision, "Sharp Collision", "High-intensity impact"),
        (DampCollision, "Damp Collision", "Medium-intensity impact with gradual decay"),
        (SubtleCollision, "Subtle Collision", "Low-intensity light contact"),
        (HappyAlert, "Happy Alert", "Positive feedback pattern"),
        (AngryAlert, "Angry Alert", "Attention-grabbing pattern"),
        (Completed, "Completed", "Confirmation pattern for task completion"),
        (Square, "Square", "Sharp-edged waveform"),
        (Wave, "Wave", "Smooth sinusoidal pattern"),
        (Firework, "Firework", "Multi-burst pattern"),
        (Mad, "Mad", "High-frequency chaotic pattern"),
        (Knock, "Knock", "Repetitive impact pattern"),
        (Jingle, "Jingle", "Musical-style pattern"),
        (Ringing, "Ringing", "Continuous oscillating pattern"),
    ];

    private static readonly HashSet<string> Names = new(Catalog.Select(entry => entry.Name), StringComparer.Ordinal);

    public static IReadOnlyList<string> All { get; } = Catalog.Select(entry => entry.Name).ToArray();

    public static bool IsValid(string? waveform) => waveform is not null && Names.Contains(waveform);

    /// <summary>Имя зарегистрированного события Logi для waveform.</summary>
    public static string ToEventName(string waveform) => EventPrefix + waveform;

    public static string DisplayName(string waveform) =>
        Catalog.FirstOrDefault(entry => entry.Name == waveform).DisplayName ?? waveform;

    public static string Description(string waveform) =>
        Catalog.FirstOrDefault(entry => entry.Name == waveform).Description ?? string.Empty;
}
