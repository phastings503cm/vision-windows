using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Vision.App.Model;
using Vision.Core;
using MessageRole = Vision.App.Model.MessageRole;

namespace Vision.App.Tests;

/// <summary>A chat another app starts: on the model it chose, staying there, and never with full access. The same cases as Vision for
/// macOS (DispatchHostTests.swift), plus the ways out of a model that exist here: backups, other subagent servers, and a chat's
/// fallback to the primary when its route has gone.</summary>
public sealed class DispatchHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vision-dispatch-{Guid.NewGuid():N}");
    private readonly FakeModelServer _server = new();
    private readonly string _project;

    private static readonly ProviderProfile Main = new(ProviderKind.OpenAICompat, "main", "http://main.test/v1", "main-model");
    private static readonly ProviderProfile Chosen = new(ProviderKind.OpenAICompat, "chosen", "http://chosen.test/v1", "chosen-model");

    public DispatchHostTests()
    {
        // Every chat here has a project, because a chat without one makes its scratch folder in the real data folder as soon as it
        // starts. The self-test runs one all the way, in its own data folder (SelfTest.Dispatch.cs).
        _project = Path.Combine(_dir, "project");
        Directory.CreateDirectory(_project);
        _server.Reset((_, _) => new FakeReply.Text("On it."));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private AppConfig NewConfig()
    {
        var config = new AppConfig(Path.Combine(_dir, "settings.json"));
        config.Store(Chosen);
        config.Activate(Main);
        config.Preset = PermissionPreset.WorkspaceWrite.RawValue();
        config.ComputerToolsEnabled = false;
        return config;
    }

    private AgentHost MakeHost(AppConfig config) =>
        new(config, new ConversationLog(Path.Combine(_dir, "log")), Dispatcher.CurrentDispatcher,
            queueFile: Path.Combine(_dir, "task-queue.json"),
            skillLocations: new SkillLocations(_dir, Path.Combine(_dir, "support")),
            vault: new CredentialVault(Path.Combine(_dir, "vault"), new MemoryBlobStore()),
            memory: new MemoryStore(Path.Combine(_dir, "memory")))
        {
            HttpHandlerForTesting = _server,
            RetryPolicy = new RetryPolicy { Delay = _ => TimeSpan.FromMilliseconds(20) },
            GoalErrorBackoff = _ => TimeSpan.FromMilliseconds(20),
        };

    private void Run(Func<AgentHost, AppConfig, Task> body) => UiThread.Run(async () =>
    {
        var config = NewConfig();
        var host = MakeHost(config);
        try
        {
            await body(host, config);
        }
        finally
        {
            host.StopAll();
            await Task.Delay(50);
        }
    });

    private DispatchRequest Request(string? model = null, string? folder = null, string? id = null) => new()
    {
        Version = 1,
        Id = id ?? Guid.NewGuid().ToString(),
        Source = "UltronMail",
        Title = "Weekly sync",
        Instructions = "Fix the bug",
        Context = "The note",
        Model = model,
        Folder = folder ?? _project,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task WaitUntil(string what, Func<bool> condition, double seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    private static List<string> Replies(SessionVM vm) =>
        vm.Entries.OfType<MessageEntryVM>().Where(m => m.Role == MessageRole.Assistant).Select(m => m.Text).ToList();

    private static List<string> Notes(SessionVM vm) =>
        vm.Entries.OfType<MessageEntryVM>().Where(m => m.Role is MessageRole.Notice or MessageRole.Error).Select(m => m.Text).ToList();

    private static Task Replied(SessionVM vm, int count = 1) => WaitUntil($"{count} replies", () => Replies(vm).Count >= count && !vm.Running);

    private static SessionVM Session(AgentHost host) => host.Sessions.Single(s => s.Title == "Weekly sync");

    private HashSet<string> Hosts() => _server.Seen.Select(r => r.Host).ToHashSet();

    [Fact]
    public void RunsOnTheChosenModelAndStaysThere() => Run(async (host, config) =>
    {
        var other = host.NewSession(_project);
        var result = host.StartDispatch(Request(Chosen.RouteId));
        Assert.True(result.Started, result.Message);
        Assert.Contains("chosen-model", result.Message);
        var vm = Session(host);
        Assert.Equal(other.Id, host.SelectedId);
        Assert.Equal(Chosen.RouteId, vm.PinnedRoute);
        await Replied(vm);
        Assert.Equal(["chosen.test"], Hosts());
        Assert.Contains("<material>\nThe note\n</material>", _server.Seen[^1].AllUserText);
        Assert.StartsWith("Sent from UltronMail: Fix the bug", _server.Seen[^1].LastUser);

        // The primary stays what it was, the project's next chats don't start on it, and the next turn is still on the chosen one.
        Assert.Equal(Main.RouteId, config.ActiveRoute);
        Assert.Null(config.LastChoice(_project));
        host.Send("And another thing", vm.Id);
        await Replied(vm, 2);
        Assert.Equal(["chosen.test"], Hosts());
    });

    [Fact]
    public void OtherChatsAreUntouched() => Run(async (host, _) =>
    {
        host.StartDispatch(Request(Chosen.RouteId));
        await Replied(Session(host));
        var other = host.NewSession(_project);
        Assert.Equal(Main.RouteId, other.RouteId);
        Assert.Null(other.PinnedRoute);
        host.Send("hi", other.Id);
        await Replied(other);
        Assert.Equal("main.test", _server.Seen[^1].Host);
    });

    [Fact]
    public void ABackupNeverTakesOver() => Run(async (host, config) =>
    {
        config.Store(new ProviderProfile(ProviderKind.OpenAICompat, "backup", "http://backup.test/v1", "backup-model") { Backup = true });
        _server.Reset((request, _) => request.Host == "chosen.test" ? new FakeReply.Transport() : new FakeReply.Text("from elsewhere"));
        host.StartDispatch(Request(Chosen.RouteId));
        var vm = Session(host);
        await WaitUntil("a few tries", () => _server.Seen.Count >= 3 || !vm.Running);
        host.StopSession(vm.Id);
        await WaitUntil("it to stop", () => !vm.Running);
        Assert.Equal(["chosen.test"], Hosts());
        Assert.DoesNotContain(Replies(vm), r => r.Contains("elsewhere"));
    });

    /// <summary>Without a pin, the same setup does move to the backup, so the test above is testing something.</summary>
    [Fact]
    public void AnOrdinaryChatStillFailsOver() => Run(async (host, config) =>
    {
        config.Store(new ProviderProfile(ProviderKind.OpenAICompat, "backup", "http://backup.test/v1", "backup-model") { Backup = true });
        _server.Reset((request, _) => request.Host == "chosen.test" ? new FakeReply.Transport() : new FakeReply.Text("from the backup"));
        var vm = host.NewSession(_project);
        host.SetChatRoute(vm, Chosen.RouteId);
        host.Send("hi", vm.Id);
        await Replied(vm);
        Assert.Contains("backup.test", Hosts());
    });

    [Fact]
    public void SubagentsStayOnTheChosenModel() => Run(async (host, config) =>
    {
        config.Store(new ProviderProfile(ProviderKind.OpenAICompat, "worker", "http://worker.test/v1", "worker-model") { SubagentWorker = true });
        host.RefreshFleet();
        _server.Reset((request, index) => request.AllUserText.Contains("SUBTASK-7") || index > 1
            ? new FakeReply.Text("Done.")
            : new FakeReply.ToolCall("agent", """{"description":"check","prompt":"SUBTASK-7: count the files"}"""));
        host.StartDispatch(Request(Chosen.RouteId));
        var vm = Session(host);
        var pool = host.SubagentPoolFor(vm);
        Assert.NotNull(pool);
        Assert.False(pool.HasWorkers);
        await Replied(vm);
        Assert.Contains(_server.Seen, r => r.LastUser.Contains("SUBTASK-7"));
        Assert.Equal(["chosen.test"], Hosts());
    });

    [Fact]
    public void CompactingStaysOnTheChosenModel() => Run(async (host, _) =>
    {
        host.StartDispatch(Request(Chosen.RouteId));
        var vm = Session(host);
        await Replied(vm);
        var count = 1;
        foreach (var text in new[] { "two", "three", "four" })
        {
            host.Send(text, vm.Id);
            await Replied(vm, ++count);
        }

        var before = _server.Seen.Count;
        host.Send("/compact", vm.Id);
        await WaitUntil("a summary to be asked for", () => _server.Seen.Count > before);
        Assert.Equal(["chosen.test"], Hosts());
    });

    [Fact]
    public void NoModelUsesThePrimaryAndIsntPinned() => Run(async (host, _) =>
    {
        Assert.True(host.StartDispatch(Request()).Started);
        var vm = Session(host);
        Assert.Null(vm.PinnedRoute);
        await Replied(vm);
        Assert.Equal(["main.test"], Hosts());
    });

    [Fact]
    public void PickingAModelMovesThePinnedChatOffIt() => Run(async (host, _) =>
    {
        host.StartDispatch(Request(Chosen.RouteId));
        var vm = Session(host);
        await Replied(vm);
        host.SetChatRoute(vm, Main.RouteId);
        Assert.Null(vm.PinnedRoute);
        host.Send("next", vm.Id);
        await Replied(vm, 2);
        Assert.Equal("main.test", _server.Seen[^1].Host);
    });

    [Fact]
    public void ARemovedModelStopsTheChatInsteadOfMovingIt() => Run(async (host, config) =>
    {
        host.StartDispatch(Request(Chosen.RouteId));
        var vm = Session(host);
        await Replied(vm);
        var sent = _server.Seen.Count;
        config.RemoveProvider(config.Providers.First(p => p.RouteId == Chosen.RouteId));
        Assert.Null(host.ChatRoute(vm));
        host.Send("next", vm.Id);
        await WaitUntil("it to stop", () => !vm.Running && Notes(vm).Contains(AgentHost.PinnedRouteGone));
        Assert.Equal(sent, _server.Seen.Count);
        Assert.Equal(Chosen.RouteId, vm.PinnedRoute);
    });

    [Fact]
    public void ASwitchedOffModelStopsTheChatToo() => Run(async (host, config) =>
    {
        host.StartDispatch(Request(Chosen.RouteId));
        var vm = Session(host);
        await Replied(vm);
        var sent = _server.Seen.Count;
        config.Providers.First(p => p.RouteId == Chosen.RouteId).Enabled = false;
        host.Send("next", vm.Id);
        await WaitUntil("it to stop", () => !vm.Running && Notes(vm).Contains(AgentHost.PinnedRouteGone));
        Assert.Equal(sent, _server.Seen.Count);
    });

    [Fact]
    public void NeverStartsWithFullAccess() => Run((host, config) =>
    {
        config.Preset = PermissionPreset.FullAccess.RawValue();
        Assert.True(host.StartDispatch(Request()).Started);
        Assert.Equal(PermissionPreset.WorkspaceWrite, Session(host).Preset);
        Assert.Equal(PermissionPreset.FullAccess, config.AsPreset);
        return Task.CompletedTask;
    });

    [Fact]
    public void KeepsAStricterDefault() => Run((host, config) =>
    {
        config.Preset = PermissionPreset.Plan.RawValue();
        Assert.True(host.StartDispatch(Request()).Started);
        Assert.Equal(PermissionPreset.Plan, Session(host).Preset);
        return Task.CompletedTask;
    });

    [Fact]
    public void RefusesWhatItCantDo() => Run(async (host, config) =>
    {
        config.Store(new ProviderProfile(ProviderKind.OpenAICompat, "plan", "http://plan.test/v1", "plan-model") { InteractiveOnly = true });
        foreach (var request in new[]
                 {
                     Request("nobody|nothing"),
                     Request("plan|plan-model"),
                     Request(folder: Path.Combine(_dir, "nope")),
                     Request(folder: "relative"),
                 })
        {
            var result = host.StartDispatch(request);
            Assert.False(result.Started, request.Id);
            Assert.Equal(request.Id, result.Id);
            Assert.NotEmpty(result.Message);
        }

        await Task.Delay(50);
        Assert.DoesNotContain(host.Sessions, s => s.Title == "Weekly sync");
        Assert.Empty(_server.Seen);
    });

    /// <summary>What's sent while the model server switches isn't run, so this says so instead of leaving an empty chat.</summary>
    [Fact]
    public void RefusesWhileTheServerIsSwitching() => Run((host, _) =>
    {
        host.IsRouteSwitching = _ => true;
        var result = host.StartDispatch(Request(Chosen.RouteId));
        Assert.False(result.Started);
        Assert.Contains("switching", result.Message);
        Assert.DoesNotContain(host.Sessions, s => s.Title == "Weekly sync");
        return Task.CompletedTask;
    });

    [Fact]
    public void WorksInTheChosenFolder() => Run(async (host, _) =>
    {
        var folder = Path.Combine(_dir, "chosen-folder");
        Directory.CreateDirectory(folder);
        Assert.True(host.StartDispatch(Request(folder: folder)).Started);
        var vm = Session(host);
        Assert.Equal(folder, vm.WorkspacePath);
        await Replied(vm);
    });

    [Fact]
    public void AnEmptyTitleLetsTheChatNameItself() => Run(async (host, _) =>
    {
        var result = host.StartDispatch(Request() with { Title = "   " });
        Assert.True(result.Started);
        await WaitUntil("a title", () => host.Sessions.Any(s => s.Title.StartsWith("Sent from UltronMail", StringComparison.Ordinal)));
    });

    [Fact]
    public void ThePinSurvivesARestart() => Run(async (host, config) =>
    {
        host.StartDispatch(Request(Chosen.RouteId));
        var vm = Session(host);
        await Replied(vm);
        var again = MakeHost(config);
        again.Reload();
        var restored = again.Sessions.Single(s => s.Id == vm.Id);
        Assert.Equal(Chosen.RouteId, restored.PinnedRoute);
        again.Hydrate(restored);
        again.Send("after the restart", restored.Id);
        await Replied(restored, 2);
        Assert.Equal("chosen.test", _server.Seen[^1].Host);
        again.StopAll();
    });

    [Fact]
    public void ARenamedRouteKeepsItsPinnedChats() => Run(async (host, config) =>
    {
        host.StartDispatch(Request(Chosen.RouteId));
        var vm = Session(host);
        await Replied(vm);
        config.NoteRouteRenamed(Chosen.RouteId, "renamed|chosen-model");
        Assert.Equal("renamed|chosen-model", vm.PinnedRoute);
        Assert.Equal("renamed|chosen-model", vm.RouteId);
    });

    [Fact]
    public void TheCatalogListsWhatsOnAndNotWhatsOff() => Run((host, config) =>
    {
        config.Store(new ProviderProfile(ProviderKind.OpenAICompat, "off", "http://off.test/v1", "off-model") { Enabled = false });
        var catalog = host.BuildDispatchCatalog();
        Assert.Contains(catalog.Models, m => m.Id == Chosen.RouteId && !m.IsCurrent);
        Assert.Contains(catalog.Models, m => m.Id == Main.RouteId && m.IsCurrent);
        Assert.DoesNotContain(catalog.Models, m => m.Id == "off|off-model");
        return Task.CompletedTask;
    });

    /// <summary>The whole round through real files: the catalog goes up, a request is started and answered, and a bad one is answered too.</summary>
    [Fact]
    public void AFullRoundThroughTheFolder() => Run(async (host, _) =>
    {
        var inbox = new DispatchInbox(Path.Combine(_dir, "data"));
        inbox.Prepare();
        var good = Request(Chosen.RouteId);
        await File.WriteAllTextAsync(Path.Combine(inbox.Inbox, good.Id + ".json"), DispatchInbox.Encode(good));
        await File.WriteAllTextAsync(Path.Combine(inbox.Inbox, "broken.json"), "nope");

        await host.PumpDispatchAsync(inbox);

        using var catalog = JsonDocument.Parse(await File.ReadAllTextAsync(inbox.CatalogFile));
        Assert.Equal(1, catalog.RootElement.GetProperty("version").GetInt32());
        Assert.Empty(Directory.GetFiles(inbox.Inbox));
        using var started = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(inbox.Results, good.Id + ".json")));
        Assert.True(started.RootElement.GetProperty("started").GetBoolean());
        using var refused = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(inbox.Results, "broken.json")));
        Assert.False(refused.RootElement.GetProperty("started").GetBoolean());
        await Replied(Session(host));

        await host.PumpDispatchAsync(inbox);
        Assert.Single(host.Sessions, s => s.Title == "Weekly sync");
    });
}
