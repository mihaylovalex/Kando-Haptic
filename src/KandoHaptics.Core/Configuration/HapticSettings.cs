namespace KandoHaptics.Configuration;

using System.Text.Json;
using System.Text.Json.Serialization;
using KandoHaptics.Haptics;
using KandoHaptics.Ipc;
using KandoHaptics.Logging;

/// <summary>Настройки одного события Kando в settings.json.</summary>
public sealed class EventHapticSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("waveform")]
    public string? Waveform { get; set; }
}

/// <summary>Форма файла settings.json (то, что видит и редактирует пользователь).</summary>
public sealed class HapticSettings
{
    public const int DefaultNavigationCooldownMs = 70;
    public const int DefaultNavigationDebounceMs = 100;
    public const int DefaultTransitionGuardMs = 90;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Минимальный интервал между сигналами наведения.</summary>
    [JsonPropertyName("navigationCooldownMs")]
    public int NavigationCooldownMs { get; set; } = DefaultNavigationCooldownMs;

    /// <summary>Окно подавления «дрожания» на границе соседних секторов (A→B→A).</summary>
    [JsonPropertyName("navigationDebounceMs")]
    public int NavigationDebounceMs { get; set; } = DefaultNavigationDebounceMs;

    /// <summary>Сколько миллисекунд после перехода/выбора не играть менее важные эффекты.</summary>
    [JsonPropertyName("transitionGuardMs")]
    public int TransitionGuardMs { get; set; } = DefaultTransitionGuardMs;

    /// <summary>Отыграть сектор, на котором курсор остановился, если его сигнал был отсечён cooldown.</summary>
    [JsonPropertyName("trailingHoverFeedback")]
    public bool TrailingHoverFeedback { get; set; } = true;

    [JsonPropertyName("logLevel")]
    public string LogLevel { get; set; } = "Info";

    [JsonPropertyName("events")]
    public Dictionary<string, EventHapticSettings> Events { get; set; } = new(StringComparer.Ordinal);

    public static HapticSettings CreateDefault()
    {
        var settings = new HapticSettings();
        foreach (var (interaction, enabled, waveform) in EventDefaults.Table)
        {
            settings.Events[interaction.ToWireName()] = new EventHapticSettings { Enabled = enabled, Waveform = enabled ? waveform : null };
        }

        return settings;
    }
}

/// <summary>Значения по умолчанию для каждого события Kando.</summary>
public static class EventDefaults
{
    /// <summary>
    /// (событие, включено по умолчанию, waveform). Для выключенных событий waveform
    /// используется, если пользователь включит событие, не указав эффект.
    /// </summary>
    public static readonly (KandoInteraction Interaction, bool Enabled, string Waveform)[] Table =
    [
        (KandoInteraction.OpenMenu, true, HapticWaveform.DampStateChange),
        (KandoInteraction.HoverButton, true, HapticWaveform.SubtleCollision),
        (KandoInteraction.HoverSubmenu, true, HapticWaveform.SubtleCollision),
        (KandoInteraction.OpenSubmenu, true, HapticWaveform.SharpStateChange),
        (KandoInteraction.CloseSubmenu, true, HapticWaveform.DampCollision),
        (KandoInteraction.SelectButton, true, HapticWaveform.Completed),
        (KandoInteraction.HoverCenter, false, HapticWaveform.SubtleCollision),
        (KandoInteraction.HoverParent, false, HapticWaveform.SubtleCollision),
        (KandoInteraction.ActivateMenu, false, HapticWaveform.Completed),
        (KandoInteraction.CloseMenu, false, HapticWaveform.DampCollision),
        (KandoInteraction.ScrollUp, false, HapticWaveform.SubtleCollision),
        (KandoInteraction.ScrollDown, false, HapticWaveform.SubtleCollision),
    ];

    public static (bool Enabled, string Waveform) For(KandoInteraction interaction)
    {
        foreach (var entry in Table)
        {
            if (entry.Interaction == interaction)
            {
                return (entry.Enabled, entry.Waveform);
            }
        }

        return (false, HapticWaveform.SubtleCollision);
    }
}

/// <summary>Итоговая настройка события после проверки.</summary>
public readonly record struct EventEffect(bool Enabled, string Waveform);

/// <summary>Проверенные, неизменяемые настройки, которыми пользуется обработчик.</summary>
public sealed class EffectiveSettings
{
    private readonly Dictionary<KandoInteraction, EventEffect> _events;

    internal EffectiveSettings(
        bool enabled,
        int navigationCooldownMs,
        int navigationDebounceMs,
        int transitionGuardMs,
        bool trailingHoverFeedback,
        LogLevel logLevel,
        Dictionary<KandoInteraction, EventEffect> events)
    {
        this.Enabled = enabled;
        this.NavigationCooldownMs = navigationCooldownMs;
        this.NavigationDebounceMs = navigationDebounceMs;
        this.TransitionGuardMs = transitionGuardMs;
        this.TrailingHoverFeedback = trailingHoverFeedback;
        this.LogLevel = logLevel;
        this._events = events;
    }

    public static EffectiveSettings Default { get; } = SettingsValidator.Validate(HapticSettings.CreateDefault()).Settings;

    public bool Enabled { get; }

    public int NavigationCooldownMs { get; }

    public int NavigationDebounceMs { get; }

    public int TransitionGuardMs { get; }

    public bool TrailingHoverFeedback { get; }

    public LogLevel LogLevel { get; }

    public EventEffect For(KandoInteraction interaction) =>
        this._events.TryGetValue(interaction, out var effect) ? effect : new EventEffect(false, HapticWaveform.SubtleCollision);

    public bool AnyEventEnabled => this._events.Values.Any(effect => effect.Enabled);
}

public sealed record ValidationResult(EffectiveSettings Settings, IReadOnlyList<string> Warnings);

public static class SettingsValidator
{
    public const int MaxIntervalMs = 2000;
    public const int MaxGuardMs = 1000;

    public static ValidationResult Validate(HapticSettings? raw)
    {
        raw ??= HapticSettings.CreateDefault();
        var warnings = new List<string>();

        var cooldown = ClampMs(raw.NavigationCooldownMs, MaxIntervalMs, "navigationCooldownMs", warnings);
        var debounce = ClampMs(raw.NavigationDebounceMs, MaxIntervalMs, "navigationDebounceMs", warnings);
        var guard = ClampMs(raw.TransitionGuardMs, MaxGuardMs, "transitionGuardMs", warnings);

        if (!TryParseLogLevel(raw.LogLevel, out var logLevel))
        {
            warnings.Add($"logLevel '{raw.LogLevel}' не поддерживается (Debug, Info, Warning, Error); используется Info.");
            logLevel = LogLevel.Info;
        }

        var events = new Dictionary<KandoInteraction, EventEffect>();
        foreach (var (interaction, enabled, waveform) in EventDefaults.Table)
        {
            // События, которых нет в файле (например, добавленные в новой версии), получают значения по умолчанию.
            events[interaction] = new EventEffect(enabled, waveform);
        }

        foreach (var (name, eventSettings) in raw.Events ?? [])
        {
            var interaction = KandoInteractionNames.ParseIgnoreCase(name);
            if (interaction == KandoInteraction.Unknown)
            {
                warnings.Add($"Неизвестное событие '{name}' в settings.json проигнорировано.");
                continue;
            }

            var fallback = EventDefaults.For(interaction).Waveform;
            if (eventSettings is null)
            {
                warnings.Add($"Событие '{name}' задано как null; используется значение по умолчанию.");
                continue;
            }

            var waveform = eventSettings.Waveform;
            if (string.IsNullOrWhiteSpace(waveform))
            {
                waveform = fallback;
            }
            else if (!HapticWaveform.IsValid(waveform))
            {
                warnings.Add($"Событие '{name}': waveform '{waveform}' не поддерживается; используется '{fallback}'.");
                waveform = fallback;
            }

            events[interaction] = new EventEffect(eventSettings.Enabled, waveform);
        }

        var settings = new EffectiveSettings(raw.Enabled, cooldown, debounce, guard, raw.TrailingHoverFeedback, logLevel, events);
        return new ValidationResult(settings, warnings);
    }

    public static bool TryParseLogLevel(string? value, out LogLevel level)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "debug":
                level = LogLevel.Debug;
                return true;
            case "info":
            case "information":
                level = LogLevel.Info;
                return true;
            case "warning":
            case "warn":
                level = LogLevel.Warning;
                return true;
            case "error":
                level = LogLevel.Error;
                return true;
            default:
                level = LogLevel.Info;
                return false;
        }
    }

    private static int ClampMs(int value, int max, string name, List<string> warnings)
    {
        if (value < 0 || value > max)
        {
            var clamped = Math.Clamp(value, 0, max);
            warnings.Add($"{name}={value} вне диапазона 0..{max}; используется {clamped}.");
            return clamped;
        }

        return value;
    }
}

internal static class SettingsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16,
    };
}
