namespace KandoHaptics.Tests;

using KandoHaptics.Configuration;
using KandoHaptics.Haptics;
using KandoHaptics.Ipc;
using KandoHaptics.Processing;
using Microsoft.Extensions.Time.Testing;

public sealed class InteractionProcessorTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly MockHapticProvider _provider;
    private readonly TestLogger _log = new();
    private EffectiveSettings _settings = EffectiveSettings.Default;
    private readonly InteractionProcessor _processor;

    public InteractionProcessorTests()
    {
        this._provider = new MockHapticProvider(this._time);
        this._processor = new InteractionProcessor(() => this._settings, new HapticEngine(this._provider, this._log), this._log, this._time);
    }

    public void Dispose() => this._processor.Dispose();

    private InteractionOutcome Send(KandoInteraction interaction, params int[] path) =>
        this._processor.Process(MenuInteractionEvent.Create(interaction, path));

    private void Wait(int ms) => this._time.Advance(TimeSpan.FromMilliseconds(ms));

    private void UseSettings(Action<HapticSettings> change)
    {
        var raw = HapticSettings.CreateDefault();
        change(raw);
        this._settings = SettingsValidator.Validate(raw).Settings;
    }

    /// <summary>Открыть меню и выйти за окно transitionGuardMs.</summary>
    private void OpenMenu()
    {
        Assert.Equal(InteractionOutcome.Played, this.Send(KandoInteraction.OpenMenu));
        this.Wait(200);
        this._provider.Clear();
    }

    [Fact]
    public void OpenMenuPlaysOneSignal()
    {
        Assert.Equal(InteractionOutcome.Played, this.Send(KandoInteraction.OpenMenu));
        Assert.Equal([HapticWaveform.DampStateChange], this._provider.Waveforms);
        Assert.True(this._processor.IsMenuOpen);
    }

    [Fact]
    public void HoverOnNewSectorPlaysOneSignal()
    {
        this.OpenMenu();
        Assert.Equal(InteractionOutcome.Played, this.Send(KandoInteraction.HoverButton, 2));
        Assert.Equal([HapticWaveform.SubtleCollision], this._provider.Waveforms);
    }

    [Fact]
    public void RepeatedHoverWithSamePathIsIgnored()
    {
        this.OpenMenu();
        this.Send(KandoInteraction.HoverButton, 2);
        this.Wait(500);

        Assert.Equal(InteractionOutcome.Duplicate, this.Send(KandoInteraction.HoverButton, 2));
        this.Wait(500);
        Assert.Equal(InteractionOutcome.Duplicate, this.Send(KandoInteraction.HoverButton, 2));
        Assert.Equal(1, this._provider.Count);
    }

    [Fact]
    public void NewSectorAfterCooldownPlaysImmediately()
    {
        this.OpenMenu();
        this.Send(KandoInteraction.HoverButton, 0);
        this.Wait(HapticSettings.DefaultNavigationCooldownMs);

        Assert.Equal(InteractionOutcome.Played, this.Send(KandoInteraction.HoverButton, 1));
        Assert.Equal(2, this._provider.Count);
    }

    [Fact]
    public void NewSectorWithinCooldownIsDeferredAndPlayedWhenCursorStays()
    {
        this.OpenMenu();
        this.Send(KandoInteraction.HoverButton, 0);
        this.Wait(30);

        Assert.Equal(InteractionOutcome.Deferred, this.Send(KandoInteraction.HoverButton, 1));
        Assert.Equal(1, this._provider.Count);
        Assert.True(this._processor.HasPendingHover);

        this.Wait(39);
        Assert.Equal(1, this._provider.Count); // cooldown ещё не истёк
        this.Wait(1);
        Assert.Equal(2, this._provider.Count); // ровно через 70 мс после первого сигнала
        Assert.Equal(70, (this._provider.Calls[1].At - this._provider.Calls[0].At).TotalMilliseconds);
    }

    [Fact]
    public void DeferredHoverIsCancelledWhenCursorReturnsToFeltSector()
    {
        this.OpenMenu();
        this.Send(KandoInteraction.HoverButton, 0);
        this.Wait(20);
        this.Send(KandoInteraction.HoverButton, 1); // отложен
        this.Wait(20);

        Assert.Equal(InteractionOutcome.Debounced, this.Send(KandoInteraction.HoverButton, 0));
        this.Wait(500);
        Assert.Equal(1, this._provider.Count);
    }

    [Fact]
    public void FastSweepIsRateLimited()
    {
        this.OpenMenu();
        for (var sector = 0; sector < 8; sector++)
        {
            this.Send(KandoInteraction.HoverButton, sector);
            this.Wait(10);
        }

        this.Wait(500);

        // 8 секторов за 70 мс: вместо 8 сигналов — первый сразу, не чаще раза в 70 мс,
        // и обязательно последний сектор, на котором курсор остановился.
        var calls = this._provider.Calls;
        Assert.InRange(calls.Count, 2, 3);
        for (var i = 1; i < calls.Count; i++)
        {
            Assert.True(calls[i].At - calls[i - 1].At >= TimeSpan.FromMilliseconds(HapticSettings.DefaultNavigationCooldownMs));
        }
    }

    [Fact]
    public void BoundaryJitterBetweenNeighboursProducesSingleSignal()
    {
        this.OpenMenu();
        this.Send(KandoInteraction.HoverButton, 0);
        this.Wait(300);
        this.Send(KandoInteraction.HoverButton, 1); // реальный переход
        for (var i = 0; i < 6; i++)
        {
            this.Wait(15);
            this.Send(KandoInteraction.HoverButton, i % 2 == 0 ? 0 : 1);
        }

        this.Wait(500);
        Assert.Equal(2, this._provider.Count);
    }

    [Fact]
    public void ReturningToSectorAfterPauseIsFeltAgain()
    {
        this.OpenMenu();
        this.Send(KandoInteraction.HoverButton, 0);
        this.Wait(300);
        this.Send(KandoInteraction.HoverButton, 1);
        this.Wait(300);

        Assert.Equal(InteractionOutcome.Played, this.Send(KandoInteraction.HoverButton, 0));
        Assert.Equal(3, this._provider.Count);
    }

    [Fact]
    public void OpenSubmenuPlaysTransitionAndSuppressesSimultaneousHover()
    {
        this.OpenMenu();
        this.Send(KandoInteraction.HoverSubmenu, 2);
        this.Wait(300);
        this._provider.Clear();

        // Kando присылает openSubmenu и сразу hover дочернего элемента.
        Assert.Equal(InteractionOutcome.Played, this.Send(KandoInteraction.OpenSubmenu, 2));
        Assert.Equal(InteractionOutcome.Guarded, this.Send(KandoInteraction.HoverButton, 2, 0));
        this.Wait(500);

        Assert.Equal([HapticWaveform.SharpStateChange], this._provider.Waveforms);
        Assert.Equal([2], this._processor.CurrentPath);
    }

    [Fact]
    public void HoverInsideSubmenuAfterGuardPlays()
    {
        this.OpenMenu();
        this.Send(KandoInteraction.OpenSubmenu, 2);
        this.Send(KandoInteraction.HoverButton, 2, 0);
        this.Wait(300);
        this._provider.Clear();

        Assert.Equal(InteractionOutcome.Played, this.Send(KandoInteraction.HoverButton, 2, 1));
        Assert.Equal([HapticWaveform.SubtleCollision], this._provider.Waveforms);
    }

    [Fact]
    public void HoverSubmenuUsesItsOwnSetting()
    {
        this.UseSettings(s => s.Events["hoverSubmenu"] = new EventHapticSettings { Enabled = true, Waveform = HapticWaveform.DampCollision });
        this.OpenMenu();
        this.Send(KandoInteraction.HoverSubmenu, 1);
        Assert.Equal([HapticWaveform.DampCollision], this._provider.Waveforms);
    }

    [Fact]
    public void CloseSubmenuPlaysDampCollisionAndReturnsToParent()
    {
        this.OpenMenu();
        this.Send(KandoInteraction.OpenSubmenu, 1);
        this.Wait(100);
        this.Send(KandoInteraction.OpenSubmenu, 1, 3);
        this.Wait(300);
        this._provider.Clear();

        Assert.Equal(InteractionOutcome.Played, this.Send(KandoInteraction.CloseSubmenu, 1, 3));
        Assert.Equal([HapticWaveform.DampCollision], this._provider.Waveforms);
        Assert.Equal([1], this._processor.CurrentPath);
    }

    [Fact]
    public void SameSectorAfterLevelChangeIsFeltAgain()
    {
        this.OpenMenu();
        this.Send(KandoInteraction.HoverSubmenu, 1);
        this.Wait(200);
        this.Send(KandoInteraction.OpenSubmenu, 1);
        this.Wait(200);
        this.Send(KandoInteraction.HoverParent);
        this.Wait(200);
        this.Send(KandoInteraction.CloseSubmenu, 1);
        this.Wait(200);
        this._provider.Clear();

        Assert.Equal(InteractionOutcome.Played, this.Send(KandoInteraction.HoverSubmenu, 1));
    }

    [Fact]
    public void SelectButtonIsNotBlockedByHoverCooldown()
    {
        this.OpenMenu();
        this.Send(KandoInteraction.HoverButton, 4);
        this.Wait(5);

        Assert.Equal(InteractionOutcome.Played, this.Send(KandoInteraction.SelectButton, 4));
        Assert.Equal([HapticWaveform.SubtleCollision, HapticWaveform.Completed], this._provider.Waveforms);
    }

    [Fact]
    public void SelectButtonIsNotBlockedByTransitionGuard()
    {
        this.OpenMenu();
        this.Send(KandoInteraction.OpenSubmenu, 0);
        Assert.Equal(InteractionOutcome.Played, this.Send(KandoInteraction.SelectButton, 0, 1));
    }

    [Fact]
    public void CloseMenuRightAfterSelectionDoesNotCompete()
    {
        this.UseSettings(s => s.Events["closeMenu"] = new EventHapticSettings { Enabled = true, Waveform = HapticWaveform.DampCollision });
        this.OpenMenu();
        this.Send(KandoInteraction.SelectButton, 1);
        this.Wait(3);

        Assert.Equal(InteractionOutcome.Guarded, this.Send(KandoInteraction.CloseMenu));
        Assert.Equal([HapticWaveform.Completed], this._provider.Waveforms);
    }

    [Fact]
    public void CloseMenuResetsState()
    {
        this.OpenMenu();
        this.Send(KandoInteraction.OpenSubmenu, 3);
        this.Send(KandoInteraction.HoverButton, 3, 0); // будет отложен/поглощён
        this.Send(KandoInteraction.CloseMenu);

        Assert.False(this._processor.IsMenuOpen);
        Assert.Empty(this._processor.CurrentPath);
        Assert.False(this._processor.HasPendingHover);
    }

    [Fact]
    public void LateEventsAfterCloseMenuAreIgnoredUntilNextOpenMenu()
    {
        this.OpenMenu();
        this.Send(KandoInteraction.CloseMenu);

        Assert.Equal(InteractionOutcome.Ignored, this.Send(KandoInteraction.HoverButton, 2));
        Assert.Equal(InteractionOutcome.Ignored, this.Send(KandoInteraction.HoverSubmenu, 2));
        Assert.Equal(InteractionOutcome.Ignored, this.Send(KandoInteraction.SelectButton, 2));
        Assert.False(this._processor.IsMenuOpen);
        Assert.Equal(0, this._provider.Count);

        this.OpenMenu();
        Assert.Equal(InteractionOutcome.Played, this.Send(KandoInteraction.HoverButton, 2));
        Assert.Equal(1, this._provider.Count);
    }
    [Fact]
    public void PendingHoverDoesNotLeakIntoClosedMenu()
    {
        this.OpenMenu();
        this.Send(KandoInteraction.HoverButton, 0);
        this.Wait(10);
        this.Send(KandoInteraction.HoverButton, 1); // отложен
        this.Send(KandoInteraction.CloseMenu);
        this.Wait(500);

        Assert.Equal(1, this._provider.Count);
    }

    [Fact]
    public void NewMenuAllowsPreviousPathToVibrateAgain()
    {
        this.OpenMenu();
        this.Send(KandoInteraction.HoverButton, 2);
        this.Send(KandoInteraction.SelectButton, 2);
        this.Send(KandoInteraction.CloseMenu);
        this.Wait(500);

        this.OpenMenu();
        Assert.Equal(InteractionOutcome.Played, this.Send(KandoInteraction.HoverButton, 2));
    }

    [Fact]
    public void OpenMenuResetsHoverHistoryEvenWithoutCloseMenu()
    {
        this.OpenMenu();
        this.Send(KandoInteraction.HoverButton, 2);
        this.Wait(500);

        this.OpenMenu();
        Assert.Equal(InteractionOutcome.Played, this.Send(KandoInteraction.HoverButton, 2));
    }

    [Fact]
    public void DisabledEventDoesNotVibrate()
    {
        this.OpenMenu();
        Assert.Equal(InteractionOutcome.EventDisabled, this.Send(KandoInteraction.HoverCenter));
        Assert.Equal(InteractionOutcome.EventDisabled, this.Send(KandoInteraction.HoverParent));
        Assert.Equal(InteractionOutcome.EventDisabled, this.Send(KandoInteraction.ActivateMenu));
        Assert.Equal(InteractionOutcome.EventDisabled, this.Send(KandoInteraction.CloseMenu));
        Assert.Equal(0, this._provider.Count);
    }

    [Fact]
    public void GloballyDisabledHapticsNeverVibrate()
    {
        this.UseSettings(s => s.Enabled = false);
        Assert.Equal(InteractionOutcome.HapticsDisabled, this.Send(KandoInteraction.OpenMenu));
        Assert.Equal(InteractionOutcome.HapticsDisabled, this.Send(KandoInteraction.HoverButton, 0));
        Assert.Equal(InteractionOutcome.HapticsDisabled, this.Send(KandoInteraction.SelectButton, 0));
        Assert.Equal(0, this._provider.Count);
        Assert.True(this._processor.IsMenuOpen); // состояние всё равно отслеживается
    }

    [Fact]
    public void EnablingOptionalEventUsesFallbackWaveform()
    {
        this.UseSettings(s => s.Events["hoverCenter"] = new EventHapticSettings { Enabled = true });
        this.OpenMenu();
        Assert.Equal(InteractionOutcome.Played, this.Send(KandoInteraction.HoverCenter));
        Assert.Equal([HapticWaveform.SubtleCollision], this._provider.Waveforms);
    }

    [Fact]
    public void UnknownEventIsIgnoredWithoutException()
    {
        var outcome = this._processor.Process(new MenuInteractionEvent(KandoInteraction.Unknown, "teleport", [1]));
        Assert.Equal(InteractionOutcome.Ignored, outcome);
        Assert.Equal(0, this._provider.Count);
    }

    [Fact]
    public void HoverWithoutOpenMenuStartsImplicitSession()
    {
        Assert.Equal(InteractionOutcome.Played, this.Send(KandoInteraction.HoverButton, 1, 2));
        Assert.True(this._processor.IsMenuOpen);
        Assert.Equal([1], this._processor.CurrentPath);
    }

    [Fact]
    public void DepthIsNormalisedFromHoverPaths()
    {
        this.OpenMenu();
        this.Send(KandoInteraction.HoverButton, 4, 0, 2); // openSubmenu пропущены
        Assert.Equal([4, 0], this._processor.CurrentPath);
    }

    [Fact]
    public void DuplicateOpenSubmenuIsIgnored()
    {
        this.OpenMenu();
        Assert.Equal(InteractionOutcome.Played, this.Send(KandoInteraction.OpenSubmenu, 1));
        Assert.Equal(InteractionOutcome.Duplicate, this.Send(KandoInteraction.OpenSubmenu, 1));
        Assert.Equal(1, this._provider.Count);
    }

    [Fact]
    public void TrailingFeedbackCanBeDisabled()
    {
        this.UseSettings(s => s.TrailingHoverFeedback = false);
        this.OpenMenu();
        this.Send(KandoInteraction.HoverButton, 0);
        this.Wait(20);
        Assert.Equal(InteractionOutcome.RateLimited, this.Send(KandoInteraction.HoverButton, 1));
        this.Wait(500);
        Assert.Equal(1, this._provider.Count);
    }

    [Fact]
    public void CooldownIsConfigurable()
    {
        this.UseSettings(s => s.NavigationCooldownMs = 200);
        this.OpenMenu();
        this.Send(KandoInteraction.HoverButton, 0);
        this.Wait(150);
        Assert.Equal(InteractionOutcome.Deferred, this.Send(KandoInteraction.HoverButton, 1));
        this.Wait(49);
        Assert.Equal(1, this._provider.Count);
        this.Wait(1);
        Assert.Equal(2, this._provider.Count);
    }

    [Fact]
    public void ProviderFailureIsIsolated()
    {
        this._provider.FailWith = w => w == HapticWaveform.DampStateChange ? new InvalidOperationException("sdk") : null;

        Assert.Equal(InteractionOutcome.Failed, this.Send(KandoInteraction.OpenMenu));
        this.Wait(200);
        Assert.Equal(InteractionOutcome.Played, this.Send(KandoInteraction.HoverButton, 0));
        Assert.True(this._log.Contains(Logging.LogLevel.Error, "Logitech SDK"));
    }

    [Fact]
    public void ScrollEventsAreOptional()
    {
        this.OpenMenu();
        Assert.Equal(InteractionOutcome.EventDisabled, this.Send(KandoInteraction.ScrollUp, 1));

        this.UseSettings(s => s.Events["scrollUp"] = new EventHapticSettings { Enabled = true });
        Assert.Equal(InteractionOutcome.Played, this.Send(KandoInteraction.ScrollUp, 1));
        Assert.Equal(InteractionOutcome.RateLimited, this.Send(KandoInteraction.ScrollUp, 1));
    }

    /// <summary>
    /// Реальная последовательность из журнала Kando (подменю в marking mode, выбор, закрытие).
    /// </summary>
    [Fact]
    public void RealWorldSequenceProducesCalmFeedback()
    {
        var script = new (int AtMs, KandoInteraction Interaction, int[] Path)[]
        {
            (0, KandoInteraction.OpenMenu, []),
            (40, KandoInteraction.HoverCenter, []),
            (133, KandoInteraction.HoverButton, [3]),
            (261, KandoInteraction.HoverSubmenu, [2]),
            (566, KandoInteraction.OpenSubmenu, [2]),
            (566, KandoInteraction.HoverSubmenu, [2, 2]),
            (755, KandoInteraction.HoverSubmenu, [2, 3]),
            (845, KandoInteraction.HoverSubmenu, [2, 4]),
            (1151, KandoInteraction.OpenSubmenu, [2, 4]),
            (1151, KandoInteraction.HoverButton, [2, 4, 0]),
            (1425, KandoInteraction.HoverButton, [2, 4, 1]),
            (1766, KandoInteraction.SelectButton, [2, 4, 1]),
            (1769, KandoInteraction.CloseMenu, []),
        };

        var now = 0;
        foreach (var (at, interaction, path) in script)
        {
            this.Wait(at - now);
            now = at;
            this.Send(interaction, path);
        }

        this.Wait(500);
        Assert.Equal(
            [
                HapticWaveform.DampStateChange,   // openMenu
                HapticWaveform.SubtleCollision,   // [3]
                HapticWaveform.SubtleCollision,   // [2]
                HapticWaveform.SharpStateChange,  // openSubmenu [2]; hover [2,2] поглощён
                HapticWaveform.SubtleCollision,   // [2,3]
                HapticWaveform.SubtleCollision,   // [2,4]
                HapticWaveform.SharpStateChange,  // openSubmenu [2,4]; hover [2,4,0] поглощён
                HapticWaveform.SubtleCollision,   // [2,4,1]
                HapticWaveform.Completed,         // selectButton
            ],
            this._provider.Waveforms);
    }
}
