namespace KandoHaptics.Tests;

using System.Collections.Concurrent;
using KandoHaptics.Haptics;
using KandoHaptics.Ipc;

public class IpcInfoTests
{
    [Fact]
    public void ReadsValidIpcInfo()
    {
        using var dir = new TempDirectory();
        var path = dir.File("ipc-info.json");
        File.WriteAllText(path, "{ \"port\": 12345, \"apiVersion\": 2 }");

        var result = KandoIpcInfoReader.Read(path);

        Assert.Equal(IpcInfoStatus.Ok, result.Status);
        Assert.Equal(12345, result.Info!.Port);
        Assert.Equal(2, result.Info.ApiVersion);
        Assert.Equal(new Uri("ws://127.0.0.1:12345"), result.Info.WebSocketUri);
    }

    [Fact]
    public void MissingFileIsReportedAsNotFound()
    {
        using var dir = new TempDirectory();
        Assert.Equal(IpcInfoStatus.NotFound, KandoIpcInfoReader.Read(dir.File("ipc-info.json")).Status);
        Assert.Equal(IpcInfoStatus.NotFound, KandoIpcInfoReader.Read(Path.Combine(dir.Path, "missing-dir", "ipc-info.json")).Status);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{ \"port\": 1")]
    [InlineData("[1, 2]")]
    [InlineData("")]
    public void InvalidJsonIsRejected(string content)
    {
        Assert.Equal(IpcInfoStatus.InvalidJson, KandoIpcInfoReader.Parse(System.Text.Encoding.UTF8.GetBytes(content)).Status);
    }

    [Theory]
    [InlineData("{ \"apiVersion\": 2 }")]
    [InlineData("{ \"port\": 0, \"apiVersion\": 2 }")]
    [InlineData("{ \"port\": 70000, \"apiVersion\": 2 }")]
    [InlineData("{ \"port\": \"12345\", \"apiVersion\": 2 }")]
    public void InvalidPortIsRejected(string content)
    {
        Assert.Equal(IpcInfoStatus.InvalidPort, KandoIpcInfoReader.Parse(System.Text.Encoding.UTF8.GetBytes(content)).Status);
    }

    [Fact]
    public void UnsupportedApiVersionIsReportedWithInfo()
    {
        var result = KandoIpcInfoReader.Parse("{ \"port\": 4000, \"apiVersion\": 3 }"u8);

        Assert.Equal(IpcInfoStatus.UnsupportedApiVersion, result.Status);
        Assert.Equal(3, result.Info!.ApiVersion);
        Assert.False(result.IsOk);
    }

    [Fact]
    public void MissingApiVersionIsRejected()
    {
        Assert.Equal(IpcInfoStatus.MissingApiVersion, KandoIpcInfoReader.Parse("{ \"port\": 4000 }"u8).Status);
    }

    [Fact]
    public void Utf8BomIsAccepted()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat("{\"port\":5000,\"apiVersion\":2}"u8.ToArray()).ToArray();
        Assert.True(KandoIpcInfoReader.Parse(bytes).IsOk);
    }

    [Fact]
    public void DefaultPathUsesRoamingAppData()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "kando", "ipc-info.json");
        Assert.Equal(expected, KandoIpcInfo.DefaultPath);
    }
}

public class KandoMessageParserTests
{
    [Fact]
    public void ParsesMenuInteraction()
    {
        var message = KandoMessageParser.Parse("{\"type\":\"menu-interaction\",\"interaction\":\"hoverButton\",\"path\":[2,1]}");

        Assert.Equal(KandoMessageKind.MenuInteraction, message.Kind);
        Assert.Equal(KandoInteraction.HoverButton, message.Interaction!.Interaction);
        Assert.Equal([2, 1], message.Interaction.Path);
    }

    [Theory]
    [InlineData("openMenu", KandoInteraction.OpenMenu)]
    [InlineData("hoverButton", KandoInteraction.HoverButton)]
    [InlineData("hoverSubmenu", KandoInteraction.HoverSubmenu)]
    [InlineData("hoverCenter", KandoInteraction.HoverCenter)]
    [InlineData("hoverParent", KandoInteraction.HoverParent)]
    [InlineData("openSubmenu", KandoInteraction.OpenSubmenu)]
    [InlineData("closeSubmenu", KandoInteraction.CloseSubmenu)]
    [InlineData("selectButton", KandoInteraction.SelectButton)]
    [InlineData("activateMenu", KandoInteraction.ActivateMenu)]
    [InlineData("closeMenu", KandoInteraction.CloseMenu)]
    public void RecognisesAllDocumentedInteractions(string wireName, KandoInteraction expected)
    {
        var message = KandoMessageParser.Parse($"{{\"type\":\"menu-interaction\",\"interaction\":\"{wireName}\",\"path\":[]}}");
        Assert.Equal(expected, message.Interaction!.Interaction);
        Assert.Equal(wireName, expected.ToWireName());
    }

    [Fact]
    public void UnknownInteractionIsKeptAsUnknown()
    {
        var message = KandoMessageParser.Parse("{\"type\":\"menu-interaction\",\"interaction\":\"teleport\",\"path\":[1]}");
        Assert.Equal(KandoMessageKind.MenuInteraction, message.Kind);
        Assert.Equal(KandoInteraction.Unknown, message.Interaction!.Interaction);
        Assert.Equal("teleport", message.Interaction.RawInteraction);
    }

    [Fact]
    public void MissingPathMeansRoot()
    {
        var message = KandoMessageParser.Parse("{\"type\":\"menu-interaction\",\"interaction\":\"closeMenu\"}");
        Assert.Empty(message.Interaction!.Path);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("nonsense")]
    [InlineData("{\"type\":5}")]
    [InlineData("{\"type\":\"menu-interaction\"}")]
    [InlineData("{\"type\":\"menu-interaction\",\"interaction\":\"hoverButton\",\"path\":[-1]}")]
    [InlineData("{\"type\":\"menu-interaction\",\"interaction\":\"hoverButton\",\"path\":[\"x\"]}")]
    [InlineData("{\"type\":\"menu-interaction\",\"interaction\":\"hoverButton\",\"path\":5}")]
    [InlineData("{\"type\":\"menu-interaction\",\"interaction\":\"hoverButton\",\"path\":[1.5]}")]
    public void InvalidMessagesAreRejectedWithoutExceptions(string json)
    {
        Assert.Equal(KandoMessageKind.Invalid, KandoMessageParser.Parse(json).Kind);
    }

    [Fact]
    public void ErrorMessageExposesReason()
    {
        var message = KandoMessageParser.Parse("{\"type\":\"error\",\"reason\":\"bad\",\"description\":\"details\"}");
        Assert.Equal(KandoMessageKind.Error, message.Kind);
        Assert.Equal("bad", message.ErrorReason);
        Assert.Equal("details", message.ErrorDescription);
    }

    [Fact]
    public void OtherTypesAreUnsupported()
    {
        Assert.Equal(KandoMessageKind.Unsupported, KandoMessageParser.Parse("{\"type\":\"hover-item\",\"path\":\"/0\"}").Kind);
    }

    [Fact]
    public void OverlyDeepPathIsRejected()
    {
        var path = string.Join(",", Enumerable.Range(0, KandoMessageParser.MaxPathLength + 1));
        Assert.Equal(KandoMessageKind.Invalid, KandoMessageParser.Parse($"{{\"type\":\"menu-interaction\",\"interaction\":\"hoverButton\",\"path\":[{path}]}}").Kind);
    }
}

public class KandoIpcClientTests
{
    private static KandoIpcClientOptions FastOptions(string ipcInfoPath) => new()
    {
        IpcInfoPath = ipcInfoPath,
        MinReconnectDelay = TimeSpan.FromMilliseconds(50),
        MaxReconnectDelay = TimeSpan.FromMilliseconds(200),
        ConnectTimeout = TimeSpan.FromSeconds(2),
    };

    [Fact]
    public void BackoffIsExponentialAndBounded()
    {
        var min = TimeSpan.FromSeconds(1);
        var max = TimeSpan.FromSeconds(10);
        Assert.Equal(TimeSpan.FromSeconds(1), KandoIpcClient.ComputeBackoff(0, min, max));
        Assert.Equal(TimeSpan.FromSeconds(2), KandoIpcClient.ComputeBackoff(1, min, max));
        Assert.Equal(TimeSpan.FromSeconds(4), KandoIpcClient.ComputeBackoff(2, min, max));
        Assert.Equal(TimeSpan.FromSeconds(8), KandoIpcClient.ComputeBackoff(3, min, max));
        Assert.Equal(TimeSpan.FromSeconds(10), KandoIpcClient.ComputeBackoff(4, min, max));
        Assert.Equal(TimeSpan.FromSeconds(10), KandoIpcClient.ComputeBackoff(1000, min, max));
    }

    [Fact]
    public async Task ConnectsSendsStartObservingAndReceivesEvents()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeKandoServer();
        server.WriteIpcInfo(dir.File("ipc-info.json"));
        var events = new ConcurrentQueue<MenuInteractionEvent>();
        await using var client = new KandoIpcClient(FastOptions(dir.File("ipc-info.json")), events.Enqueue, new TestLogger());

        client.Start();
        await server.WaitForStartObservingAsync();
        Assert.Equal("{\"type\":\"start-observing\"}", server.Received[0]);
        await Eventually.TrueAsync(() => client.State == KandoConnectionState.Connected, "клиент подключён");
        Assert.Equal(server.Port, client.ConnectedPort);

        await server.SendInteractionAsync("openMenu");
        await server.SendInteractionAsync("hoverButton", 2, 1);
        await server.SendAsync("{\"type\":\"something-new\"}");
        await server.SendAsync("garbage");
        await server.SendInteractionAsync("selectButton", 2, 1);

        await Eventually.TrueAsync(() => events.Count == 3, "получены три события");
        Assert.Equal([KandoInteraction.OpenMenu, KandoInteraction.HoverButton, KandoInteraction.SelectButton], events.Select(e => e.Interaction));
        Assert.Equal([2, 1], events.ElementAt(1).Path);
    }

    [Fact]
    public async Task ReassemblesFragmentedMessages()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeKandoServer();
        server.WriteIpcInfo(dir.File("ipc-info.json"));
        var events = new ConcurrentQueue<MenuInteractionEvent>();
        await using var client = new KandoIpcClient(FastOptions(dir.File("ipc-info.json")), events.Enqueue, new TestLogger());
        client.Start();
        await server.WaitForStartObservingAsync();

        await server.SendFragmentedAsync("{\"type\":\"menu-interaction\",\"interaction\":\"openSubmenu\",\"path\":[3,0,1]}", chunkSize: 7);

        await Eventually.TrueAsync(() => events.Count == 1, "фрагменты собраны в одно событие");
        Assert.Equal(KandoInteraction.OpenSubmenu, events.Single().Interaction);
        Assert.Equal([3, 0, 1], events.Single().Path);
    }

    [Fact]
    public async Task SkipsOversizedMessageAndKeepsConnection()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeKandoServer();
        server.WriteIpcInfo(dir.File("ipc-info.json"));
        var events = new ConcurrentQueue<MenuInteractionEvent>();
        var options = new KandoIpcClientOptions { IpcInfoPath = dir.File("ipc-info.json"), MaxMessageBytes = 1024, MinReconnectDelay = TimeSpan.FromMilliseconds(50) };
        var log = new TestLogger();
        await using var client = new KandoIpcClient(options, events.Enqueue, log);
        client.Start();
        await server.WaitForStartObservingAsync();

        await server.SendFragmentedAsync("{\"type\":\"menu-interaction\",\"interaction\":\"openMenu\",\"pad\":\"" + new string('x', 5000) + "\"}", 900);
        await server.SendInteractionAsync("closeMenu");

        await Eventually.TrueAsync(() => events.Count == 1, "после большого сообщения поток продолжается");
        Assert.Equal(KandoInteraction.CloseMenu, events.Single().Interaction);
        Assert.Equal(1, server.AcceptedConnections);
        Assert.True(log.Contains(Logging.LogLevel.Warning, "пропущено"));
    }

    [Fact]
    public async Task ReconnectsAfterServerDropsConnection()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeKandoServer();
        server.WriteIpcInfo(dir.File("ipc-info.json"));
        var events = new ConcurrentQueue<MenuInteractionEvent>();
        await using var client = new KandoIpcClient(FastOptions(dir.File("ipc-info.json")), events.Enqueue, new TestLogger());
        client.Start();
        await server.WaitForStartObservingAsync();

        server.DropConnection();
        await Eventually.TrueAsync(() => server.AcceptedConnections == 2, "клиент переподключился");
        await server.WaitForStartObservingAsync();
        await Eventually.TrueAsync(() => client.State == KandoConnectionState.Connected, "состояние Connected");

        await server.SendInteractionAsync("openMenu");
        await Eventually.TrueAsync(() => events.Count == 1, "события после переподключения");
        Assert.Equal(2, server.Received.Count(m => m.Contains("start-observing")));
        Assert.Equal(2, client.ConnectionCount);
    }

    [Fact]
    public async Task ReconnectsAfterGracefulServerClose()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeKandoServer();
        server.WriteIpcInfo(dir.File("ipc-info.json"));
        await using var client = new KandoIpcClient(FastOptions(dir.File("ipc-info.json")), _ => { }, new TestLogger());
        client.Start();
        await server.WaitForStartObservingAsync();

        await server.CloseConnectionAsync();

        await Eventually.TrueAsync(() => server.AcceptedConnections == 2, "клиент переподключился после close frame");
    }

    [Fact]
    public async Task FollowsKandoToNewPortAfterRestart()
    {
        using var dir = new TempDirectory();
        var ipcInfo = dir.File("ipc-info.json");
        var first = new FakeKandoServer();
        first.WriteIpcInfo(ipcInfo);
        var events = new ConcurrentQueue<MenuInteractionEvent>();
        var log = new TestLogger();
        await using var client = new KandoIpcClient(FastOptions(ipcInfo), events.Enqueue, log);
        client.Start();
        await first.WaitForStartObservingAsync();
        var firstPort = first.Port;

        // «Kando закрылся», затем запустился заново на другом порту.
        await first.DisposeAsync();
        await using var second = new FakeKandoServer();
        Assert.NotEqual(firstPort, second.Port);
        second.WriteIpcInfo(ipcInfo);

        await second.WaitForStartObservingAsync();
        await Eventually.TrueAsync(() => client.ConnectedPort == second.Port, "клиент подключён к новому порту");
        await second.SendInteractionAsync("openMenu");
        await Eventually.TrueAsync(() => events.Count == 1, "события с нового порта");
        Assert.True(log.Contains(Logging.LogLevel.Info, $"{firstPort} → {second.Port}"));
    }

    [Fact]
    public async Task WaitsWhileKandoIsNotRunningAndConnectsWhenItStarts()
    {
        using var dir = new TempDirectory();
        var ipcInfo = dir.File("ipc-info.json");
        var log = new TestLogger();
        await using var client = new KandoIpcClient(FastOptions(ipcInfo), _ => { }, log);
        client.Start();

        await Eventually.TrueAsync(() => client.State == KandoConnectionState.WaitingForKando, "ожидание Kando");
        await Task.Delay(400);
        Assert.Equal(1, log.Entries.Count(e => e.Level == Logging.LogLevel.Info && e.Message.Contains("не обнаружен")));

        await using var server = new FakeKandoServer();
        server.WriteIpcInfo(ipcInfo);
        await server.WaitForStartObservingAsync();
    }

    [Fact]
    public async Task UnsupportedApiVersionIsLoggedOnceAndNeverConnects()
    {
        using var dir = new TempDirectory();
        var ipcInfo = dir.File("ipc-info.json");
        await using var server = new FakeKandoServer();
        server.WriteIpcInfo(ipcInfo, apiVersion: 3);
        var log = new TestLogger();
        await using var client = new KandoIpcClient(FastOptions(ipcInfo), _ => { }, log);
        client.Start();

        await Task.Delay(800);
        Assert.Equal(0, server.AcceptedConnections);
        Assert.Equal(1, log.Entries.Count(e => e.Level == Logging.LogLevel.Warning && e.Message.Contains("версия")));
        Assert.Equal(KandoConnectionState.WaitingForKando, client.State);

        server.WriteIpcInfo(ipcInfo, apiVersion: 2);
        await server.WaitForStartObservingAsync();
    }

    [Fact]
    public async Task StopClosesConnectionGracefullyAndQuickly()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeKandoServer();
        server.WriteIpcInfo(dir.File("ipc-info.json"));
        var client = new KandoIpcClient(FastOptions(dir.File("ipc-info.json")), _ => { }, new TestLogger());
        client.Start();
        await server.WaitForStartObservingAsync();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await client.DisposeAsync();
        stopwatch.Stop();

        Assert.Equal(KandoConnectionState.Stopped, client.State);
        Assert.True(stopwatch.ElapsedMilliseconds < 3000, $"остановка заняла {stopwatch.ElapsedMilliseconds} мс");
        await Eventually.TrueAsync(() => server.Received.Contains("<close>"), "сервер получил close frame");
        Assert.Contains(server.Received, m => m.Contains("stop-observing"));
    }

    [Fact]
    public async Task HandlerExceptionDoesNotBreakConnection()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeKandoServer();
        server.WriteIpcInfo(dir.File("ipc-info.json"));
        var received = 0;
        await using var client = new KandoIpcClient(
            FastOptions(dir.File("ipc-info.json")),
            e =>
            {
                if (Interlocked.Increment(ref received) == 1)
                {
                    throw new InvalidOperationException("boom");
                }
            },
            new TestLogger());
        client.Start();
        await server.WaitForStartObservingAsync();

        await server.SendInteractionAsync("openMenu");
        await server.SendInteractionAsync("closeMenu");

        await Eventually.TrueAsync(() => Volatile.Read(ref received) == 2, "второе событие обработано");
        Assert.Equal(1, server.AcceptedConnections);
    }
}

public class RuntimeIntegrationTests
{
    [Fact]
    public async Task KandoEventsBecomeHapticEffectsEndToEnd()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeKandoServer();
        server.WriteIpcInfo(dir.File("ipc-info.json"));
        var provider = new MockHapticProvider();
        var options = new KandoHapticsRuntimeOptions
        {
            SettingsPath = dir.File("settings.json"),
            Ipc = new KandoIpcClientOptions { IpcInfoPath = dir.File("ipc-info.json"), MinReconnectDelay = TimeSpan.FromMilliseconds(50) },
        };
        await using var runtime = new KandoHapticsRuntime(provider, new TestLogger(), options);
        runtime.Start();
        await server.WaitForStartObservingAsync();

        await server.SendInteractionAsync("openMenu");
        await Task.Delay(150); // за пределами transitionGuardMs
        await server.SendInteractionAsync("hoverButton", 0);
        await Task.Delay(150);
        await server.SendInteractionAsync("selectButton", 0);
        await server.SendInteractionAsync("closeMenu");

        await Eventually.TrueAsync(() => provider.Count == 3, "три эффекта");
        await Task.Delay(100);
        Assert.Equal([HapticWaveform.DampStateChange, HapticWaveform.SubtleCollision, HapticWaveform.Completed], provider.Waveforms);
        Assert.True(File.Exists(dir.File("settings.json")));
    }
}
