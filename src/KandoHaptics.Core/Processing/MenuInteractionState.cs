namespace KandoHaptics.Processing;

/// <summary>Приоритет эффекта: более важный эффект не подавляется менее важным.</summary>
public enum EffectPriority
{
    None = 0,
    Navigation = 1,
    Transition = 2,
    Selection = 3,
}

/// <summary>
/// Состояние текущего сеанса меню Kando. Используется только внутри
/// <see cref="InteractionProcessor"/> под его блокировкой.
/// </summary>
public sealed class MenuInteractionState
{
    public bool IsOpen { get; private set; }

    /// <summary>Номер сеанса (увеличивается при каждом openMenu); отменяет устаревшие отложенные сигналы.</summary>
    public int SessionId { get; private set; }

    /// <summary>Путь текущего центра меню: [] — корень, [2] — подменю третьего элемента и т.д.</summary>
    public IReadOnlyList<int> CurrentPath { get; set; } = [];

    public int Depth => this.CurrentPath.Count;

    /// <summary>Ключ последнего наведённого элемента (тип + путь), независимо от того, был ли сигнал.</summary>
    public string? LastHoverKey { get; set; }

    /// <summary>Ключ элемента, для которого последним реально прозвучал сигнал наведения.</summary>
    public string? LastEmittedHoverKey { get; set; }

    /// <summary>Ключ предыдущего «прозвучавшего» элемента — для подавления дрожания A→B→A.</summary>
    public string? PreviousEmittedHoverKey { get; set; }

    /// <summary>Отметка времени (TimeProvider) последнего сигнала навигации.</summary>
    public long? LastNavigationTimestamp { get; set; }

    public EffectPriority LastEffectPriority { get; set; }

    public long? LastEffectTimestamp { get; set; }

    public string? LastEffectKey { get; set; }

    public void BeginSession()
    {
        this.Clear();
        this.IsOpen = true;
        this.SessionId++;
    }

    /// <summary>Очистить состояние активного меню (closeMenu).</summary>
    public void Clear()
    {
        this.IsOpen = false;
        this.CurrentPath = [];
        this.ResetHover();
        this.LastNavigationTimestamp = null;
        this.LastEffectPriority = EffectPriority.None;
        this.LastEffectTimestamp = null;
        this.LastEffectKey = null;
    }

    /// <summary>Сбросить историю наведения (новое меню или смена уровня).</summary>
    public void ResetHover()
    {
        this.LastHoverKey = null;
        this.LastEmittedHoverKey = null;
        this.PreviousEmittedHoverKey = null;
    }
}
