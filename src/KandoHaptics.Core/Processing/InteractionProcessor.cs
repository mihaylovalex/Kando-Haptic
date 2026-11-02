namespace KandoHaptics.Processing;

using KandoHaptics.Configuration;
using KandoHaptics.Haptics;
using KandoHaptics.Ipc;
using KandoHaptics.Logging;

/// <summary>Результат обработки одного события (для журнала и тестов).</summary>
public enum InteractionOutcome
{
    /// <summary>Эффект воспроизведён.</summary>
    Played,

    /// <summary>Наведение отсечено cooldown, но будет отыграно, если курсор останется на элементе.</summary>
    Deferred,

    /// <summary>Повтор того же элемента — сигнал не нужен.</summary>
    Duplicate,

    /// <summary>Кратковременное «дрожание» на границе соседних секторов.</summary>
    Debounced,

    /// <summary>Отсечено cooldown без отложенного воспроизведения.</summary>
    RateLimited,

    /// <summary>Подавлено более важным эффектом, только что сыгранным (переход, выбор).</summary>
    Guarded,

    /// <summary>Событие выключено в настройках.</summary>
    EventDisabled,

    /// <summary>Вибрация выключена полностью (enabled=false).</summary>
    HapticsDisabled,

    /// <summary>Неизвестное событие — пропущено.</summary>
    Ignored,

    /// <summary>SDK вернул ошибку.</summary>
    Failed,
}

/// <summary>
/// Превращает поток событий Kando в сдержанную тактильную отдачу:
/// отслеживает сеанс меню, глубину и путь, отбрасывает дубликаты, ограничивает частоту
/// наведения (cooldown) с отложенным сигналом для сектора, где курсор остановился,
/// гасит дрожание A→B→A на границе секторов и не даёт конкурирующим эффектам
/// звучать одновременно (приоритеты Navigation &lt; Transition &lt; Selection).
/// Потокобезопасен: вызовы из диспетчера и таймера сериализуются блокировкой.
/// </summary>
public sealed class InteractionProcessor : IDisposable
{
    private readonly Func<EffectiveSettings> _settings;
    private readonly HapticEngine _engine;
    private readonly IKandoLogger _log;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly MenuInteractionState _state = new();
    private readonly ITimer _trailingTimer;
    private PendingHover? _pending;
    private bool _disposed;

    public InteractionProcessor(Func<EffectiveSettings> settings, HapticEngine engine, IKandoLogger log, TimeProvider? time = null)
    {
        this._settings = settings;
        this._engine = engine;
        this._log = log;
        this._time = time ?? TimeProvider.System;
        this._trailingTimer = this._time.CreateTimer(_ => this.OnTrailingTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Срабатывает после каждого обработанного события (для диагностики и тестов).</summary>
    public event Action<MenuInteractionEvent, InteractionOutcome>? Processed;

    public bool IsMenuOpen
    {
        get
        {
            lock (this._sync)
            {
                return this._state.IsOpen;
            }
        }
    }

    public IReadOnlyList<int> CurrentPath
    {
        get
        {
            lock (this._sync)
            {
                return this._state.CurrentPath;
            }
        }
    }

    public bool HasPendingHover
    {
        get
        {
            lock (this._sync)
            {
                return this._pending is not null;
            }
        }
    }

    public InteractionOutcome Process(MenuInteractionEvent e)
    {
        InteractionOutcome outcome;
        lock (this._sync)
        {
            if (this._disposed)
            {
                return InteractionOutcome.Ignored;
            }

            outcome = this.ProcessLocked(e, this._settings());
        }

        this._log.Debug(static s => $"Kando: {s.e.RawInteraction} {s.e.PathText} → {s.outcome}", (e, outcome));
        this.RaiseProcessed(e, outcome);
        return outcome;
    }

    public void Reset()
    {
        lock (this._sync)
        {
            this.CancelPending();
            this._state.Clear();
        }
    }

    public void Dispose()
    {
        lock (this._sync)
        {
            this._disposed = true;
            this._pending = null;
        }

        this._trailingTimer.Dispose();
    }

    internal static string KeyOf(MenuInteractionEvent e) =>
        ((int)e.Interaction).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + string.Join(",", e.Path);

    private static EffectPriority PriorityOf(KandoInteraction interaction) => interaction switch
    {
        KandoInteraction.SelectButton or KandoInteraction.ActivateMenu => EffectPriority.Selection,
        KandoInteraction.OpenMenu or KandoInteraction.CloseMenu or
        KandoInteraction.OpenSubmenu or KandoInteraction.CloseSubmenu => EffectPriority.Transition,
        _ => EffectPriority.Navigation,
    };

    private static IReadOnlyList<int> ParentOf(IReadOnlyList<int> path) =>
        path.Count == 0 ? [] : path.Take(path.Count - 1).ToArray();

    private static bool PathEquals(IReadOnlyList<int> a, IReadOnlyList<int> b) => a.SequenceEqual(b);

    private InteractionOutcome ProcessLocked(MenuInteractionEvent e, EffectiveSettings settings)
    {
        switch (e.Interaction)
        {
            case KandoInteraction.OpenMenu:
                // Новый сеанс: история наведения предыдущего меню забывается.
                this.CancelPending();
                this._state.BeginSession();
                return this.Emit(e, settings, bypassGuard: true);

            case KandoInteraction.CloseMenu:
            {
                this.CancelPending();
                var outcome = this._state.IsOpen ? this.Emit(e, settings) : InteractionOutcome.Duplicate;
                this._state.Clear();
                return outcome;
            }

            case KandoInteraction.OpenSubmenu:
                this.EnsureSession(e);
                this.CancelPending();
                if (PathEquals(this._state.CurrentPath, e.Path) && this._state.LastEffectKey == KeyOf(e))
                {
                    return InteractionOutcome.Duplicate;
                }

                this._state.CurrentPath = e.Path.ToArray();
                this._state.ResetHover();
                return this.Emit(e, settings);

            case KandoInteraction.CloseSubmenu:
                // path — это закрываемое подменю; новым центром становится его родитель.
                this.EnsureSession(e);
                this.CancelPending();
                this._state.CurrentPath = ParentOf(e.Path);
                this._state.ResetHover();
                return this.Emit(e, settings);

            case KandoInteraction.SelectButton:
            case KandoInteraction.ActivateMenu:
                this.EnsureSession(e);
                this.CancelPending();
                if (this._state.LastEffectKey == KeyOf(e) && this.ElapsedMs(this._state.LastEffectTimestamp) < settings.TransitionGuardMs)
                {
                    return InteractionOutcome.Duplicate;
                }

                return this.Emit(e, settings);

            case KandoInteraction.HoverButton:
            case KandoInteraction.HoverSubmenu:
            case KandoInteraction.HoverCenter:
            case KandoInteraction.HoverParent:
                return this.ProcessHover(e, settings);

            case KandoInteraction.ScrollUp:
            case KandoInteraction.ScrollDown:
                this.EnsureSession(e);
                if (this.ElapsedMs(this._state.LastNavigationTimestamp) < settings.NavigationCooldownMs)
                {
                    return InteractionOutcome.RateLimited;
                }

                var scroll = this.Emit(e, settings);
                if (scroll == InteractionOutcome.Played)
                {
                    this._state.LastNavigationTimestamp = this._time.GetTimestamp();
                }

                return scroll;

            default:
                this._log.Debug(static raw => $"Неподдерживаемое взаимодействие Kando '{raw}' пропущено.", e.RawInteraction);
                return InteractionOutcome.Ignored;
        }
    }

    private InteractionOutcome ProcessHover(MenuInteractionEvent e, EffectiveSettings settings)
    {
        var key = KeyOf(e);
        if (this._state.IsOpen && key == this._state.LastHoverKey)
        {
            return InteractionOutcome.Duplicate;
        }

        this.EnsureSession(e);
        this.NormalizeDepth(e);
        this._state.LastHoverKey = key;
        this.CancelPending();

        if (!settings.Enabled)
        {
            return InteractionOutcome.HapticsDisabled;
        }

        var effect = settings.For(e.Interaction);
        if (!effect.Enabled)
        {
            return InteractionOutcome.EventDisabled;
        }

        if (this.IsGuarded(EffectPriority.Navigation, settings))
        {
            // Элемент «озвучен» только что сыгранным переходом: считаем его прочувствованным.
            this.MarkHoverFelt(key);
            return InteractionOutcome.Guarded;
        }

        var sinceNavigation = this.ElapsedMs(this._state.LastNavigationTimestamp);
        if (sinceNavigation < settings.NavigationDebounceMs &&
            (key == this._state.LastEmittedHoverKey || key == this._state.PreviousEmittedHoverKey))
        {
            return InteractionOutcome.Debounced;
        }

        if (sinceNavigation < settings.NavigationCooldownMs)
        {
            if (!settings.TrailingHoverFeedback)
            {
                return InteractionOutcome.RateLimited;
            }

            this._pending = new PendingHover(e, key, this._state.SessionId);
            this._trailingTimer.Change(TimeSpan.FromMilliseconds(settings.NavigationCooldownMs - sinceNavigation), Timeout.InfiniteTimeSpan);
            return InteractionOutcome.Deferred;
        }

        return this.PlayHover(e, key, effect.Waveform, trailing: false);
    }

    private void OnTrailingTimer()
    {
        MenuInteractionEvent? fired = null;
        var outcome = InteractionOutcome.Ignored;
        lock (this._sync)
        {
            var pending = this._pending;
            if (this._disposed || pending is null)
            {
                return;
            }

            this._pending = null;
            if (!this._state.IsOpen || pending.SessionId != this._state.SessionId || pending.Key != this._state.LastHoverKey)
            {
                return; // курсор уже ушёл или меню закрыто
            }

            var settings = this._settings();
            var effect = settings.For(pending.Event.Interaction);
            if (!settings.Enabled || !effect.Enabled || pending.Key == this._state.LastEmittedHoverKey || this.IsGuarded(EffectPriority.Navigation, settings))
            {
                return;
            }

            var remaining = settings.NavigationCooldownMs - this.ElapsedMs(this._state.LastNavigationTimestamp);
            if (remaining > 1)
            {
                this._pending = pending;
                this._trailingTimer.Change(TimeSpan.FromMilliseconds(remaining), Timeout.InfiniteTimeSpan);
                return;
            }

            fired = pending.Event;
            outcome = this.PlayHover(pending.Event, pending.Key, effect.Waveform, trailing: true);
        }

        this._log.Debug(static e => $"Kando: отложенный сигнал для {e.RawInteraction} {e.PathText}", fired);
        this.RaiseProcessed(fired, outcome);
    }

    private InteractionOutcome PlayHover(MenuInteractionEvent e, string key, string waveform, bool trailing)
    {
        var played = this._engine.Play(new HapticRequest(e.Interaction, waveform, e.Path, trailing));
        var now = this._time.GetTimestamp();
        this.MarkHoverFelt(key, now);
        this._state.LastEffectPriority = EffectPriority.Navigation;
        this._state.LastEffectTimestamp = now;
        this._state.LastEffectKey = key;
        return played ? InteractionOutcome.Played : InteractionOutcome.Failed;
    }

    private void MarkHoverFelt(string key, long? timestamp = null)
    {
        this._state.PreviousEmittedHoverKey = this._state.LastEmittedHoverKey;
        this._state.LastEmittedHoverKey = key;
        this._state.LastNavigationTimestamp = timestamp ?? this._time.GetTimestamp();
    }

    private InteractionOutcome Emit(MenuInteractionEvent e, EffectiveSettings settings, bool bypassGuard = false)
    {
        if (!settings.Enabled)
        {
            return InteractionOutcome.HapticsDisabled;
        }

        var effect = settings.For(e.Interaction);
        if (!effect.Enabled)
        {
            return InteractionOutcome.EventDisabled;
        }

        var priority = PriorityOf(e.Interaction);
        if (!bypassGuard && this.IsGuarded(priority, settings))
        {
            return InteractionOutcome.Guarded;
        }

        var played = this._engine.Play(new HapticRequest(e.Interaction, effect.Waveform, e.Path));
        this._state.LastEffectPriority = priority;
        this._state.LastEffectTimestamp = this._time.GetTimestamp();
        this._state.LastEffectKey = KeyOf(e);
        return played ? InteractionOutcome.Played : InteractionOutcome.Failed;
    }

    private bool IsGuarded(EffectPriority priority, EffectiveSettings settings) =>
        this._state.LastEffectTimestamp is not null &&
        priority < this._state.LastEffectPriority &&
        this.ElapsedMs(this._state.LastEffectTimestamp) < settings.TransitionGuardMs;

    /// <summary>Событие пришло без openMenu (например, плагин подключился при открытом меню).</summary>
    private void EnsureSession(MenuInteractionEvent e)
    {
        if (this._state.IsOpen)
        {
            return;
        }

        this._state.BeginSession();
        this._state.CurrentPath = e.Interaction switch
        {
            KandoInteraction.HoverButton or KandoInteraction.HoverSubmenu => ParentOf(e.Path),
            KandoInteraction.HoverCenter => e.Path.ToArray(),
            _ => [],
        };
        this._log.Debug(static raw => $"Событие {raw} без openMenu: начат неявный сеанс меню.", e.RawInteraction);
    }

    /// <summary>Если подменю сменилось без openSubmenu/closeSubmenu, выравниваем путь по наведению.</summary>
    private void NormalizeDepth(MenuInteractionEvent e)
    {
        IReadOnlyList<int>? expected = e.Interaction switch
        {
            KandoInteraction.HoverButton or KandoInteraction.HoverSubmenu => ParentOf(e.Path),
            KandoInteraction.HoverCenter => e.Path,
            _ => null, // hoverParent указывает на родителя; глубину не меняет
        };

        if (expected is not null && !PathEquals(expected, this._state.CurrentPath))
        {
            this._log.Debug(
                static s => $"Путь меню выровнен по наведению: {MenuInteractionEvent.FormatPath(s.Item1)} → {MenuInteractionEvent.FormatPath(s.Item2)}.",
                (this._state.CurrentPath, expected));
            this._state.CurrentPath = expected.ToArray();
            this._state.LastEmittedHoverKey = null;
            this._state.PreviousEmittedHoverKey = null;
        }
    }

    private void CancelPending()
    {
        if (this._pending is not null)
        {
            this._pending = null;
            this._trailingTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private double ElapsedMs(long? timestamp) =>
        timestamp is { } value ? this._time.GetElapsedTime(value).TotalMilliseconds : double.MaxValue;

    private void RaiseProcessed(MenuInteractionEvent? e, InteractionOutcome outcome)
    {
        if (e is null)
        {
            return;
        }

        try
        {
            this.Processed?.Invoke(e, outcome);
        }
        catch (Exception ex)
        {
            this._log.Warning("Ошибка подписчика Processed.", ex);
        }
    }

    private sealed record PendingHover(MenuInteractionEvent Event, string Key, int SessionId);
}
