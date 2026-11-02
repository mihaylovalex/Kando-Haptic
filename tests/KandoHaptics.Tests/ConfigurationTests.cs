namespace KandoHaptics.Tests;

using System.Text.Json.Nodes;
using KandoHaptics.Configuration;
using KandoHaptics.Haptics;
using KandoHaptics.Ipc;
using KandoHaptics.Logging;

public class ConfigurationTests
{
    private static readonly (KandoInteraction Interaction, bool Enabled, string? Waveform)[] SpecDefaults =
    [
        (KandoInteraction.OpenMenu, true, "damp_state_change"),
        (KandoInteraction.HoverButton, true, "subtle_collision"),
        (KandoInteraction.HoverSubmenu, true, "subtle_collision"),
        (KandoInteraction.OpenSubmenu, true, "sharp_state_change"),
        (KandoInteraction.CloseSubmenu, true, "damp_collision"),
        (KandoInteraction.SelectButton, true, "completed"),
        (KandoInteraction.HoverCenter, false, null),
        (KandoInteraction.HoverParent, false, null),
        (KandoInteraction.ActivateMenu, false, null),
        (KandoInteraction.CloseMenu, false, null),
    ];

    private static SettingsManager CreateManager(TempDirectory dir, TestLogger? log = null) =>
        new(dir.File("settings.json"), log ?? new TestLogger(), TimeSpan.FromMilliseconds(50));

    [Fact]
    public void FirstRunCreatesDefaultSettingsFile()
    {
        using var dir = new TempDirectory();
        var log = new TestLogger();
        using var manager = CreateManager(dir, log);

        manager.Initialize(watch: false);

        Assert.True(File.Exists(dir.File("settings.json")));
        var settings = manager.Current;
        Assert.True(settings.Enabled);
        Assert.Equal(70, settings.NavigationCooldownMs);
        foreach (var (interaction, enabled, waveform) in SpecDefaults)
        {
            Assert.Equal(enabled, settings.For(interaction).Enabled);
            if (waveform is not null)
            {
                Assert.Equal(waveform, settings.For(interaction).Waveform);
            }
        }

        var json = JsonNode.Parse(File.ReadAllText(dir.File("settings.json")))!;
        Assert.Equal("damp_state_change", (string?)json["events"]!["openMenu"]!["waveform"]);
        Assert.Null(json["events"]!["hoverCenter"]!["waveform"]);
        Assert.True(log.Contains(LogLevel.Info, "Создан файл настроек"));
    }

    [Fact]
    public void LoadsUserSettings()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("settings.json"), """
            {
              // комментарии допустимы
              "enabled": true,
              "navigationCooldownMs": 120,
              "transitionGuardMs": 40,
              "logLevel": "Debug",
              "events": {
                "hoverButton": { "enabled": true, "waveform": "damp_collision" },
                "selectButton": { "enabled": false },
                "HoverCenter": { "enabled": true, "waveform": "knock" },
              }
            }
            """);
        using var manager = CreateManager(dir);

        manager.Initialize(watch: false);
        var settings = manager.Current;

        Assert.Equal(120, settings.NavigationCooldownMs);
        Assert.Equal(40, settings.TransitionGuardMs);
        Assert.Equal(LogLevel.Debug, settings.LogLevel);
        Assert.Equal(new EventEffect(true, "damp_collision"), settings.For(KandoInteraction.HoverButton));
        Assert.False(settings.For(KandoInteraction.SelectButton).Enabled);
        Assert.Equal(new EventEffect(true, "knock"), settings.For(KandoInteraction.HoverCenter));
        // Не указанные в файле события получают значения по умолчанию.
        Assert.Equal(new EventEffect(true, "sharp_state_change"), settings.For(KandoInteraction.OpenSubmenu));
    }

    [Fact]
    public void InvalidWaveformFallsBackToEventDefault()
    {
        var raw = HapticSettings.CreateDefault();
        raw.Events["hoverButton"] = new EventHapticSettings { Enabled = true, Waveform = "brrr" };

        var result = SettingsValidator.Validate(raw);

        Assert.Equal(new EventEffect(true, HapticWaveform.SubtleCollision), result.Settings.For(KandoInteraction.HoverButton));
        Assert.Contains(result.Warnings, w => w.Contains("brrr"));
    }

    [Fact]
    public void OutOfRangeValuesAreClamped()
    {
        var raw = HapticSettings.CreateDefault();
        raw.NavigationCooldownMs = -5;
        raw.NavigationDebounceMs = 99999;
        raw.TransitionGuardMs = 5000;
        raw.LogLevel = "Chatty";

        var result = SettingsValidator.Validate(raw);

        Assert.Equal(0, result.Settings.NavigationCooldownMs);
        Assert.Equal(SettingsValidator.MaxIntervalMs, result.Settings.NavigationDebounceMs);
        Assert.Equal(SettingsValidator.MaxGuardMs, result.Settings.TransitionGuardMs);
        Assert.Equal(LogLevel.Info, result.Settings.LogLevel);
        Assert.Equal(4, result.Warnings.Count);
    }

    [Fact]
    public void UnknownEventNamesAreIgnored()
    {
        var raw = HapticSettings.CreateDefault();
        raw.Events["wiggle"] = new EventHapticSettings { Enabled = true, Waveform = "wave" };

        var result = SettingsValidator.Validate(raw);

        Assert.Contains(result.Warnings, w => w.Contains("wiggle"));
    }

    [Fact]
    public void CorruptedJsonKeepsLastWorkingConfiguration()
    {
        using var dir = new TempDirectory();
        var log = new TestLogger();
        File.WriteAllText(dir.File("settings.json"), "{ \"navigationCooldownMs\": 150 }");
        using var manager = CreateManager(dir, log);
        manager.Initialize(watch: false);
        Assert.Equal(150, manager.Current.NavigationCooldownMs);

        File.WriteAllText(dir.File("settings.json"), "{ \"navigationCooldownMs\": 10, ");
        Assert.False(manager.Reload());
        Assert.Equal(150, manager.Current.NavigationCooldownMs);

        File.WriteAllText(dir.File("settings.json"), "{ \"enabled\": \"yes\" }");
        Assert.False(manager.Reload());
        Assert.Equal(150, manager.Current.NavigationCooldownMs);
        Assert.True(log.Contains(LogLevel.Warning, "последняя рабочая конфигурация"));
    }

    [Fact]
    public void CorruptedFileOnFirstLoadUsesDefaultsWithoutOverwriting()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("settings.json"), "not json at all");
        using var manager = CreateManager(dir);

        manager.Initialize(watch: false);

        Assert.Equal(HapticSettings.DefaultNavigationCooldownMs, manager.Current.NavigationCooldownMs);
        Assert.Equal("not json at all", File.ReadAllText(dir.File("settings.json")));
    }

    [Fact]
    public async Task SettingsAreReloadedWithoutRestart()
    {
        using var dir = new TempDirectory();
        using var manager = CreateManager(dir);
        manager.Initialize(watch: true);
        var changed = new TaskCompletionSource<EffectiveSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Changed += s =>
        {
            if (s.NavigationCooldownMs == 95)
            {
                changed.TrySetResult(s);
            }
        };

        var json = JsonNode.Parse(File.ReadAllText(dir.File("settings.json")))!;
        json["navigationCooldownMs"] = 95;
        json["events"]!["openMenu"]!["waveform"] = "wave";
        File.WriteAllText(dir.File("settings.json"), json.ToJsonString());

        var reloaded = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("wave", reloaded.For(KandoInteraction.OpenMenu).Waveform);
        Assert.Same(reloaded, manager.Current);
    }

    [Fact]
    public async Task ReloadAfterAtomicReplaceIsDetected()
    {
        // Многие редакторы сохраняют через временный файл и переименование.
        using var dir = new TempDirectory();
        using var manager = CreateManager(dir);
        manager.Initialize(watch: true);
        var loads = manager.LoadCount;

        var temp = dir.File("settings.json.new");
        File.WriteAllText(temp, "{ \"enabled\": false }");
        File.Move(temp, dir.File("settings.json"), overwrite: true);

        await Eventually.TrueAsync(() => manager.LoadCount > loads && !manager.Current.Enabled, "файл перечитан после замены");
    }

    [Fact]
    public void AllEffectsCanBeDisabled()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("settings.json"), "{ \"enabled\": false }");
        using var manager = CreateManager(dir);
        manager.Initialize(watch: false);
        Assert.False(manager.Current.Enabled);

        var raw = HapticSettings.CreateDefault();
        foreach (var settings in raw.Events.Values)
        {
            settings.Enabled = false;
        }

        Assert.False(SettingsValidator.Validate(raw).Settings.AnyEventEnabled);
    }

    [Fact]
    public void SetEnabledPreservesOtherUserValues()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("settings.json"), "{ \"enabled\": true, \"navigationCooldownMs\": 111, \"events\": { \"hoverButton\": { \"enabled\": true, \"waveform\": \"knock\" } } }");
        using var manager = CreateManager(dir);
        manager.Initialize(watch: false);

        manager.SetEnabled(false);

        Assert.False(manager.Current.Enabled);
        var json = JsonNode.Parse(File.ReadAllText(dir.File("settings.json")))!;
        Assert.False((bool)json["enabled"]!);
        Assert.Equal(111, (int)json["navigationCooldownMs"]!);
        Assert.Equal("knock", (string?)json["events"]!["hoverButton"]!["waveform"]);
    }

    [Fact]
    public void ExistingSettingsAreNotOverwrittenOnUpgrade()
    {
        // Файл старой версии без новых ключей: недостающее берётся из умолчаний, файл не трогаем.
        using var dir = new TempDirectory();
        const string legacy = "{ \"enabled\": true, \"navigationCooldownMs\": 80, \"events\": { \"openMenu\": { \"enabled\": false } } }";
        File.WriteAllText(dir.File("settings.json"), legacy);
        using var manager = CreateManager(dir);

        manager.Initialize(watch: false);

        Assert.Equal(legacy, File.ReadAllText(dir.File("settings.json")));
        Assert.False(manager.Current.For(KandoInteraction.OpenMenu).Enabled);
        Assert.Equal(HapticSettings.DefaultNavigationDebounceMs, manager.Current.NavigationDebounceMs);
        Assert.True(manager.Current.For(KandoInteraction.SelectButton).Enabled);
    }

    [Fact]
    public void RepositorySettingsTemplateMatchesBuiltInDefaults()
    {
        var path = Path.Combine(RepoPaths.Root, "config", "settings.json");
        var raw = System.Text.Json.JsonSerializer.Deserialize<HapticSettings>(File.ReadAllText(path), new System.Text.Json.JsonSerializerOptions { ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip });
        var result = SettingsValidator.Validate(raw);

        Assert.Empty(result.Warnings);
        foreach (var (interaction, enabled, waveform) in EventDefaults.Table)
        {
            Assert.Equal(new EventEffect(enabled, waveform), result.Settings.For(interaction));
        }

        Assert.Equal(SettingsManager.SerializeDefault().ReplaceLineEndings(), File.ReadAllText(path).Trim().ReplaceLineEndings());
    }
}

public class FileLoggerTests
{
    [Fact]
    public async Task RotatesAndLimitsLogFiles()
    {
        using var dir = new TempDirectory();
        var path = dir.File("KandoHaptics.log");
        var logger = new FileLogger(path, LogLevel.Debug, maxFileBytes: 16 * 1024, maxArchivedFiles: 2);

        for (var batch = 0; batch < 12; batch++)
        {
            for (var i = 0; i < 100; i++)
            {
                logger.Info($"строка {batch}-{i} " + new string('x', 60));
            }

            await Task.Delay(30); // даём фоновой задаче записать и сменить файл
        }

        await logger.DisposeAsync();

        var files = Directory.GetFiles(dir.Path, "KandoHaptics*.log").Select(f => Path.GetFileName(f)).Order().ToArray();
        Assert.Equal(["KandoHaptics.1.log", "KandoHaptics.2.log", "KandoHaptics.log"], files);
        Assert.All(files, f => Assert.True(new FileInfo(dir.File(f)).Length < 64 * 1024));
    }

    [Fact]
    public async Task RespectsMinimumLevel()
    {
        using var dir = new TempDirectory();
        var path = dir.File("log.txt");
        var logger = new FileLogger(path, LogLevel.Info);

        logger.Debug("скрыто");
        logger.Info("видно");
        logger.MinimumLevel = LogLevel.Debug;
        logger.Debug("теперь видно");
        await logger.DisposeAsync();

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("скрыто", text);
        Assert.Contains("[Info] видно", text);
        Assert.Contains("[Debug] теперь видно", text);
    }
}
