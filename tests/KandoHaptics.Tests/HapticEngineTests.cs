namespace KandoHaptics.Tests;

using System.Text.RegularExpressions;
using KandoHaptics.Haptics;
using KandoHaptics.Ipc;

public class HapticEngineTests
{
    private static HapticRequest Request(string waveform) => new(KandoInteraction.SelectButton, waveform, [0]);

    [Fact]
    public void PlaysValidWaveformThroughProvider()
    {
        var provider = new MockHapticProvider();
        var engine = new HapticEngine(provider, new TestLogger());

        Assert.True(engine.Play(Request(HapticWaveform.Completed)));
        Assert.True(engine.Play(Request(HapticWaveform.SubtleCollision)));

        Assert.Equal([HapticWaveform.Completed, HapticWaveform.SubtleCollision], provider.Waveforms);
        Assert.Equal([1, 2], provider.Calls.Select(c => c.Sequence));
        Assert.Equal(2, engine.PlayedCount);
    }

    [Fact]
    public void RejectsUnknownWaveform()
    {
        var provider = new MockHapticProvider();
        var log = new TestLogger();
        var engine = new HapticEngine(provider, log);

        Assert.False(engine.Play(Request("earthquake")));
        Assert.Equal(0, provider.Count);
        Assert.True(log.Contains(Logging.LogLevel.Warning, "earthquake"));
    }

    [Fact]
    public void ProviderExceptionIsContainedAndCounted()
    {
        var provider = new MockHapticProvider { FailWith = _ => new InvalidOperationException("device gone") };
        var log = new TestLogger();
        var engine = new HapticEngine(provider, log);

        for (var i = 0; i < 20; i++)
        {
            Assert.False(engine.Play(Request(HapticWaveform.Completed)));
        }

        Assert.Equal(20, engine.FailedCount);
        // Ошибки не заливают журнал: только первые пять — уровня Error.
        Assert.Equal(5, log.Entries.Count(e => e.Level == Logging.LogLevel.Error));

        provider.FailWith = null;
        Assert.True(engine.Play(Request(HapticWaveform.Completed)));
    }

    [Fact]
    public void CatalogContainsAllOfficialWaveforms()
    {
        string[] official =
        [
            "sharp_state_change", "damp_state_change", "sharp_collision", "damp_collision", "subtle_collision",
            "happy_alert", "angry_alert", "completed", "square", "wave", "firework", "mad", "knock", "jingle", "ringing",
        ];

        Assert.Equal(official, HapticWaveform.All);
        Assert.All(official, w => Assert.True(HapticWaveform.IsValid(w)));
        Assert.False(HapticWaveform.IsValid("Completed")); // имена чувствительны к регистру, как в SDK
        Assert.Equal("waveform_subtle_collision", HapticWaveform.ToEventName(HapticWaveform.SubtleCollision));
    }

    [Fact]
    public void EventNamesFollowSdkNamingRules()
    {
        // Первая буква — латинская или "_", далее буквы, цифры, "_" (документация Haptics SDK).
        var rule = new Regex("^[A-Za-z_][A-Za-z0-9_]*$");
        Assert.All(HapticWaveform.All, w => Assert.Matches(rule, HapticWaveform.ToEventName(w)));
    }

    [Fact]
    public void DefaultEventSourceDeclaresEveryCatalogEvent()
    {
        var yaml = File.ReadAllText(Path.Combine(RepoPaths.PluginPackage, "events", "DefaultEventSource.yaml"));
        var declared = Regex.Matches(yaml, @"^\s*-\s*name:\s*(\S+)\s*$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToArray();

        Assert.Equal(HapticWaveform.All.Select(HapticWaveform.ToEventName), declared);
        Assert.Contains("displayName:", yaml);
    }

    [Fact]
    public void EventMappingMapsEveryEventToItsWaveform()
    {
        var yaml = File.ReadAllText(Path.Combine(RepoPaths.PluginPackage, "events", "extra", "eventMapping.yaml"));
        Assert.StartsWith("haptics:", yaml.Split('\n').First(line => !line.StartsWith('#')).Trim());

        foreach (var waveform in HapticWaveform.All)
        {
            var block = Regex.Match(yaml, $@"^\s{{4}}{HapticWaveform.ToEventName(waveform)}:\s*\n\s+DEFAULT:\s*(\S+)", RegexOptions.Multiline);
            Assert.True(block.Success, $"нет сопоставления для {waveform}");
            Assert.Equal(waveform, block.Groups[1].Value);
        }
    }

    [Theory]
    [InlineData(@"C:\ProgramData\KandoHaptics\", true)]
    [InlineData(@"C:\Program Files\Logi\LogiPluginService\Plugins\KandoHaptics", true)]
    [InlineData(@"C:\Users\Иван\AppData\Local\Logi\LogiPluginService\Plugins\KandoHaptics", false)]
    [InlineData(@"C:\Users\José\AppData\Local\KandoHaptics", false)]
    public void DetectsInstallPathsOptionsPlusCannotRead(string path, bool ascii)
    {
        Assert.Equal(ascii, InstallPath.IsAscii(path));
    }

    [Fact]
    public void PluginAssemblyDefinesClientApplication()
    {
        // Logi Plugin Service 6.4 отказывается загружать сборку плагина без наследника
        // ClientApplication ("Cannot load plugin from ...") — проверено на живом сервисе.
        var sources = Directory.GetFiles(Path.Combine(RepoPaths.Root, "src", "KandoHaptics.Plugin"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText);
        Assert.Contains(sources, s => Regex.IsMatch(s, @"public\s+class\s+\w+\s*:\s*ClientApplication\b"));
    }

    [Fact]
    public void PackageMetadataDeclaresHapticCapability()
    {
        var yaml = File.ReadAllText(Path.Combine(RepoPaths.PluginPackage, "metadata", "LoupedeckPackage.yaml"));

        Assert.Matches(new Regex(@"^type:\s*plugin4\s*$", RegexOptions.Multiline), yaml);
        Assert.Matches(new Regex(@"^name:\s*KandoHaptics\s*$", RegexOptions.Multiline), yaml);
        Assert.Matches(new Regex(@"^pluginFileName:\s*KandoHapticsPlugin\.dll\s*$", RegexOptions.Multiline), yaml);
        Assert.Matches(new Regex(@"^pluginFolderWin:\s*bin\s*$", RegexOptions.Multiline), yaml);
        Assert.Matches(new Regex(@"^\s*-\s*HasHapticMapping\s*$", RegexOptions.Multiline), yaml);
        Assert.True(File.Exists(Path.Combine(RepoPaths.PluginPackage, "metadata", "Icon256x256.png")));
    }
}
