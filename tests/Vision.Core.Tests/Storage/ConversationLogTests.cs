namespace Vision.Core.Tests;

/// <summary>New: the per-session JSON store, always pointed at a scratch folder.</summary>
public sealed class ConversationLogTests : IDisposable
{
    private readonly TempDirectory _dir = new("vision-log");

    public void Dispose() => _dir.Dispose();

    private ConversationLog NewLog() => new(_dir.Path);

    [Fact]
    public void ThePinnedRouteSurvivesAReloadAndClears()
    {
        var log = NewLog();
        log.Upsert("s1", null, "From another app", "workspaceWrite");
        log.SetChoice("s1", "Spark|m", null);
        log.SetPinnedRoute("s1", "Spark|m");
        Assert.Equal("Spark|m", NewLog().List().Single().PinnedRoute);
        log.SetPinnedRoute("s1", null);
        Assert.Null(NewLog().List().Single().PinnedRoute);
        Assert.Equal("Spark|m", NewLog().List().Single().Route);
    }

    [Fact]
    public void SessionsAndItemsSurviveAReload()
    {
        var log = NewLog();
        log.Upsert("s1", _dir["project"], "First chat", "workspaceWrite");
        log.RecordItem("s1", "user", "hello");
        log.RecordItem("s1", "assistant", "hi there");
        log.RecordItem("s1", "tool", null, toolName: "read_file", argSummary: "a.txt", output: "contents");
        log.Upsert("s2", null, "Second", null);
        log.RecordItem("s2", "error", "boom", isError: true);

        var reloaded = NewLog();
        Assert.Equal(["s1", "s2"], reloaded.List().Select(s => s.Id).Order(StringComparer.Ordinal));

        var first = reloaded.List().Single(s => s.Id == "s1");
        Assert.Equal("First chat", first.Title);
        Assert.Equal(_dir["project"], first.Cwd);
        Assert.Equal("workspaceWrite", first.Preset);

        var items = reloaded.LoadItems("s1");
        Assert.Equal([0, 1, 2], items.Select(i => i.Seq));
        Assert.Equal(["user", "assistant", "tool"], items.Select(i => i.Kind));
        Assert.Equal(log.LoadItems("s1"), items); // every field, timestamps included
        Assert.Equal("s1:2", items[2].Id);
        Assert.Equal("read_file", items[2].ToolName);
        Assert.Equal("a.txt", items[2].ArgSummary);
        Assert.Equal("contents", items[2].Output);
        Assert.Null(items[2].Text);

        var error = Assert.Single(reloaded.LoadItems("s2"));
        Assert.True(error.IsError);
        Assert.Null(reloaded.List().Single(s => s.Id == "s2").Cwd);
    }

    [Fact]
    public void UpsertUpdatesTitleButKeepsCwdAndPresetWhenNotGiven()
    {
        var log = NewLog();
        log.Upsert("s", "C1", "old", "plan");
        log.Upsert("s", "", "new", null);

        var row = Assert.Single(NewLog().List());
        Assert.Equal("new", row.Title);
        Assert.Equal("C1", row.Cwd);
        Assert.Equal("plan", row.Preset);
    }

    [Fact]
    public void ListIsNewestFirst()
    {
        var log = NewLog();
        log.Upsert("a", null, "A", null);
        log.Upsert("b", null, "B", null);
        log.Upsert("c", null, "C", null);
        Thread.Sleep(20); // a strictly later timestamp, whatever the clock's resolution
        log.Touch("a");   // a is now the most recent

        var list = NewLog().List();
        Assert.Equal("a", list[0].Id);
        Assert.True(list.Zip(list.Skip(1)).All(p => p.First.UpdatedAt >= p.Second.UpdatedAt));
    }

    [Fact]
    public void TouchRenamesAndIgnoresUnknownSessions()
    {
        var log = NewLog();
        log.Upsert("s", null, "Untitled", null);
        var before = log.List()[0].UpdatedAt;
        log.Touch("s", "Renamed");
        log.Touch("ghost", "nobody");

        var row = Assert.Single(NewLog().List());
        Assert.Equal("Renamed", row.Title);
        Assert.True(row.UpdatedAt >= before);
        Assert.False(File.Exists(Path.Combine(_dir.Path, "ghost.json")));
    }

    [Fact]
    public void ResyncReplacesTheStoredItems()
    {
        var log = NewLog();
        log.Upsert("s", null, "t", null);
        for (var i = 0; i < 5; i++) log.RecordItem("s", "user", $"m{i}");
        var summary = new LogItemRow("s", 0, "compaction", "summary of m0-m3", null, null, null, false, DateTimeOffset.Now);
        log.Resync("s", [summary, log.LoadItems("s")[4] with { Seq = 1 }]);

        var items = NewLog().LoadItems("s");
        Assert.Equal(["compaction", "user"], items.Select(i => i.Kind));
        Assert.Equal("m4", items[1].Text);
        Assert.Equal(1, items[1].Seq);
    }

    [Fact]
    public void DeleteRemovesTheSessionAndItsFile()
    {
        var log = NewLog();
        log.Upsert("keep", null, "k", null);
        log.Upsert("drop", null, "d", null);
        log.RecordItem("drop", "user", "x");
        Assert.True(File.Exists(Path.Combine(_dir.Path, "drop.json")));

        log.Delete("drop");

        Assert.False(File.Exists(Path.Combine(_dir.Path, "drop.json")));
        Assert.Empty(log.LoadItems("drop"));
        var reloaded = NewLog();
        Assert.Equal(["keep"], reloaded.List().Select(s => s.Id));
        Assert.Empty(reloaded.LoadItems("drop"));
    }

    [Fact]
    public void CorruptFilesAreSkipped()
    {
        var log = NewLog();
        log.Upsert("good", null, "fine", null);
        log.RecordItem("good", "user", "hello");
        File.WriteAllText(Path.Combine(_dir.Path, "bad.json"), "{ this is not json");
        File.WriteAllText(Path.Combine(_dir.Path, "empty.json"), "{}");
        File.WriteAllText(Path.Combine(_dir.Path, "notes.txt"), "ignored");

        var reloaded = NewLog();
        Assert.Equal("good", Assert.Single(reloaded.List()).Id);
        Assert.Equal("hello", Assert.Single(reloaded.LoadItems("good")).Text);
    }

    [Fact]
    public void ItemsForAnUnknownSessionAreEmptyAndNotPersistedWithoutASession()
    {
        var log = NewLog();
        Assert.Empty(log.LoadItems("nobody"));
        log.RecordItem("orphan", "user", "no session row yet");
        Assert.Single(log.LoadItems("orphan"));
        Assert.Empty(Directory.EnumerateFiles(_dir.Path, "*.json"));
    }

    [Fact]
    public void FilesArePlainUtf8JsonWithoutTempLeftovers()
    {
        var log = NewLog();
        log.Upsert("s", null, "Ünïcødé ✓", null);
        log.RecordItem("s", "user", "naïve 😀");

        Assert.Equal(["s.json"], Directory.EnumerateFiles(_dir.Path).Select(f => Path.GetFileName(f)));
        var bytes = File.ReadAllBytes(Path.Combine(_dir.Path, "s.json"));
        Assert.NotEqual(0xEF, bytes[0]); // no BOM
        var reloaded = NewLog();
        Assert.Equal("Ünïcødé ✓", reloaded.List()[0].Title);
        Assert.Equal("naïve 😀", reloaded.LoadItems("s")[0].Text);
        Assert.Equal(_dir.Path, reloaded.StorageDirectory);
    }

    /// <summary>Each chat's own model and thinking level are stored with it (session.route, session.thinking — the fields
    /// Vision for macOS writes); a chat from before they existed reads as "not chosen".</summary>
    [Fact]
    public void AChatsModelChoiceIsStoredWithIt()
    {
        var log = NewLog();
        log.Upsert("s1", _dir["one"], "One", null);
        log.SetChoice("s1", "Spark B|qwen3.8", "low");
        File.WriteAllText(Path.Combine(_dir.Path, "old.json"),
            """{"session":{"id":"old","title":"Old","updatedAt":"2026-10-01T10:00:00+00:00"},"items":[]}""");

        var reloaded = NewLog();
        var chat = reloaded.List().Single(s => s.Id == "s1");
        Assert.Equal("Spark B|qwen3.8", chat.Route);
        Assert.Equal("low", chat.Thinking);
        var old = reloaded.List().Single(s => s.Id == "old");
        Assert.Null(old.Route);
        Assert.Null(old.Thinking);
        Assert.Contains("\"route\":\"Spark B|qwen3.8\"", File.ReadAllText(Path.Combine(_dir.Path, "s1.json")));

        reloaded.SetChoice("s1", null, null);
        Assert.Null(NewLog().List().Single(s => s.Id == "s1").Route);
    }
}
