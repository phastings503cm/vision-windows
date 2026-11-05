using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vision.Core;

/// <summary>One stored transcript row. Kind is user | assistant | tool | notice | error | compaction.</summary>
public sealed record LogItemRow(string SessionId, int Seq, string Kind, string? Text, string? ToolName,
                                string? ArgSummary, string? Output, bool IsError, DateTimeOffset At)
{
    public string Id => $"{SessionId}:{Seq}";
    /// <summary>A user row's attachments (names and summaries, not content); null for none.</summary>
    public IReadOnlyList<AttachmentBadge>? Attachments { get; init; }
}

/// <summary>Stored session metadata.</summary>
public sealed record LogSession
{
    public required string Id { get; init; }
    public string Title { get; set; } = "New chat";
    public string? Cwd { get; set; }
    public string? Preset { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>The route (<see cref="ProviderProfile.RouteId"/>) this chat talks to, and its thinking level (a
    /// <see cref="ThinkingLevel"/> raw value) — each chat's own, so two chats in two projects never share a model by accident.
    /// Null = not chosen yet (an older chat: it takes the primary when it next sends). The same fields as Vision for macOS.</summary>
    public string? Route { get; set; }
    public string? Thinking { get; set; }
    /// <summary>The chat is Sensitive (see <see cref="SensitivityPolicy"/>). Null = no (an older chat). The same field as Vision for macOS.</summary>
    public bool? Sensitive { get; set; }
    /// <summary>The route the chat stays on, when another app started it on a chosen one (see <see cref="DispatchInbox"/>): no backups, no
    /// other servers, and it doesn't move if that route goes. Null = an ordinary chat. The same field as Vision for macOS.</summary>
    public string? PinnedRoute { get; set; }
    /// <summary>The Claude Code session this chat's turns carry on (--resume), when it has talked to Claude Code.</summary>
    public string? ClaudeSession { get; set; }
    /// <summary>What Claude Code last said that session had cost so far (its estimate adds up across turns), so a turn's
    /// own share can be shown.</summary>
    public double? ClaudeCost { get; set; }
    /// <summary>When a turn last reached Claude Code in that session: turns other models answered after it go to Claude Code
    /// as a recap with the next message. The same fields as Vision for macOS.</summary>
    public DateTimeOffset? ClaudeSyncedAt { get; set; }
}

/// <summary>Per-session conversation store: one JSON file per session under
/// %APPDATA%\Vision\conversations. Plain files, so a search tool (or grep) works on them directly.
/// Not thread-safe: use it from the UI thread.</summary>
public sealed class ConversationLog
{
    private readonly string _dir;
    private readonly Dictionary<string, LogSession> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<LogItemRow>> _items = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public ConversationLog(string? directory = null)
    {
        _dir = directory ?? AppPaths.Conversations;
        try
        {
            Directory.CreateDirectory(_dir);
            foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
            {
                try
                {
                    var stored = JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(file), Json);
                    if (stored?.Session is null) continue;
                    _rows[stored.Session.Id] = stored.Session;
                    _items[stored.Session.Id] = (stored.Items ?? [])
                        .Select(i => new LogItemRow(stored.Session.Id, i.Seq, i.Kind, i.Text, i.ToolName, i.ArgSummary,
                            i.Output, i.IsError, i.At) { Attachments = i.Attachments is { Count: > 0 } badges ? badges : null })
                        .ToList();
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    // A damaged file is skipped, not fatal.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No store: the app still runs, it just can't remember.
        }
    }

    public string StorageDirectory => _dir;

    private string FilePath(string id) => Path.Combine(_dir, $"{id}.json");

    public void Upsert(string id, string? cwd, string title, string? preset)
    {
        if (!_rows.TryGetValue(id, out var row))
        {
            row = new LogSession { Id = id, Title = title, Cwd = cwd, Preset = preset, UpdatedAt = DateTimeOffset.Now };
            _rows[id] = row;
        }
        row.Title = title;
        row.UpdatedAt = DateTimeOffset.Now;
        if (!string.IsNullOrEmpty(cwd)) row.Cwd = cwd;
        if (preset is not null) row.Preset = preset;
        Persist(id);
    }

    /// <summary>Store the chat's own model choice.</summary>
    public void SetChoice(string id, string? route, string? thinking)
    {
        if (!_rows.TryGetValue(id, out var row) || (row.Route == route && row.Thinking == thinking)) return;
        row.Route = route;
        row.Thinking = thinking;
        Persist(id);
    }

    /// <summary>Store the route the chat stays on (null: an ordinary chat).</summary>
    public void SetPinnedRoute(string id, string? route)
    {
        if (!_rows.TryGetValue(id, out var row) || row.PinnedRoute == route) return;
        row.PinnedRoute = route;
        Persist(id);
    }

    /// <summary>Store whether the chat is Sensitive.</summary>
    public void SetSensitive(string id, bool sensitive)
    {
        bool? stored = sensitive ? true : null;
        if (!_rows.TryGetValue(id, out var row) || row.Sensitive == stored) return;
        row.Sensitive = stored;
        Persist(id);
    }

    /// <summary>Store the chat's Claude Code session, the cost it had reached, and — when a turn reached it — when
    /// (<paramref name="syncedAt"/> null keeps the last one for the same session).</summary>
    public void SetClaudeSession(string id, string? session, double? cost, DateTimeOffset? syncedAt = null)
    {
        if (!_rows.TryGetValue(id, out var row)) return;
        var synced = syncedAt ?? (session == row.ClaudeSession ? row.ClaudeSyncedAt : null);
        if (row.ClaudeSession == session && row.ClaudeCost == cost && row.ClaudeSyncedAt == synced) return;
        row.ClaudeSession = session;
        row.ClaudeCost = cost;
        row.ClaudeSyncedAt = synced;
        Persist(id);
    }

    /// <summary>One chat's stored metadata (null when there is no such chat).</summary>
    public LogSession? Row(string id) => _rows.GetValueOrDefault(id);

    public IReadOnlyList<LogSession> List() => _rows.Values.OrderByDescending(r => r.UpdatedAt).ToList();

    public IReadOnlyList<LogItemRow> LoadItems(string id) => _items.TryGetValue(id, out var list) ? list : [];

    public void RecordItem(string id, string kind, string? text, string? toolName = null, string? argSummary = null,
                           string? output = null, bool isError = false, IReadOnlyList<AttachmentBadge>? attachments = null)
    {
        if (!_items.TryGetValue(id, out var list)) _items[id] = list = [];
        list.Add(new LogItemRow(id, list.Count, kind, text, toolName, argSummary, output, isError, DateTimeOffset.Now)
        {
            Attachments = attachments is { Count: > 0 } ? attachments : null,
        });
        Persist(id);
    }

    /// <summary>Set the attachments of a session's last user row (they are known for sure once the message is prepared).</summary>
    public void SetAttachments(string id, IReadOnlyList<AttachmentBadge> badges)
    {
        if (!_items.TryGetValue(id, out var list)) return;
        var index = list.FindLastIndex(i => i.Kind == "user");
        if (index < 0) return;
        list[index] = list[index] with { Attachments = badges.Count > 0 ? badges : null };
        Persist(id);
    }

    public void Delete(string id)
    {
        _rows.Remove(id);
        _items.Remove(id);
        try { File.Delete(FilePath(id)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Replace a session's stored items wholesale (after compaction, so the persisted log
    /// tracks the compacted transcript exactly).</summary>
    public void Resync(string id, IReadOnlyList<LogItemRow> rows)
    {
        _items[id] = rows.ToList();
        Persist(id);
    }

    public void Touch(string id, string? title = null)
    {
        if (!_rows.TryGetValue(id, out var row)) return;
        if (title is not null) row.Title = title;
        row.UpdatedAt = DateTimeOffset.Now;
        Persist(id);
    }

    private void Persist(string id)
    {
        if (!_rows.TryGetValue(id, out var row)) return;
        var file = new SessionFile
        {
            Session = row,
            Items = (_items.GetValueOrDefault(id) ?? []).Select(i => new StoredItem
            {
                Seq = i.Seq,
                Kind = i.Kind,
                Text = i.Text,
                ToolName = i.ToolName,
                ArgSummary = i.ArgSummary,
                Output = i.Output,
                IsError = i.IsError,
                At = i.At,
                Attachments = i.Attachments is { Count: > 0 } badges ? badges.ToList() : null,
            }).ToList(),
        };
        try
        {
            var path = FilePath(id);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, Json), TextUtil.Utf8NoBom);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a full disk must not take the chat down.
        }
    }

    private sealed class SessionFile
    {
        public LogSession? Session { get; set; }
        public List<StoredItem>? Items { get; set; }
    }

    private sealed class StoredItem
    {
        public int Seq { get; set; }
        public string Kind { get; set; } = "";
        public string? Text { get; set; }
        public string? ToolName { get; set; }
        public string? ArgSummary { get; set; }
        public string? Output { get; set; }
        public bool IsError { get; set; }
        public DateTimeOffset At { get; set; }
        /// <summary>A user row's attachments: <c>[{"name", "label", "image", "stored"}]</c>, as Vision for macOS writes them.</summary>
        public List<AttachmentBadge>? Attachments { get; set; }
    }
}
