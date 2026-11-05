using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Vision.App.Infrastructure;
using Vision.Core;

namespace Vision.App.Model;

public enum MessageRole { User, Assistant, Notice, Error }

/// <summary>One row of the transcript. Messages, tool calls, todo snapshots, and compaction markers
/// share one ordered list so a tool call renders exactly where it happened — between the assistant
/// text that preceded it and the text that followed.</summary>
public abstract partial class ChatEntryVM : ObservableObject
{
    public string Id { get; }
    public DateTimeOffset At { get; }

    protected ChatEntryVM(string? id, DateTimeOffset? at)
    {
        Id = id ?? Guid.NewGuid().ToString("N");
        At = at ?? DateTimeOffset.Now;
    }
}

/// <summary>A chat message in the transcript.</summary>
public sealed partial class MessageEntryVM(MessageRole role, string text, string? id = null, DateTimeOffset? at = null)
    : ChatEntryVM(id, at)
{
    public MessageRole Role { get; } = role;
    [ObservableProperty] private string _text = text;
    /// <summary>On an assistant reply from a model that isn't private: what the shield masked before sending
    /// ("Masked 2 emails and 1 key before sending to Kimi Code") — counts, never values. Null: nothing was masked.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasShield))] private string? _shield;
    /// <summary>The categories behind <see cref="Shield"/>, for its tooltip.</summary>
    [ObservableProperty] private string? _shieldDetails;

    public bool HasShield => Shield is not null;

    /// <summary>What a user message carried (files, pictures, large pastes), shown as badges under it — names and summaries,
    /// never content.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasAttachments))] private IReadOnlyList<AttachmentBadge> _attachments = [];

    public bool HasAttachments => Attachments.Count > 0;

    // A long user message (a big paste in a chat from before pastes became attachments, or typed text) shows its first
    // lines and opens in a viewer on demand: laid out in full inside the transcript, a 10,000-line message is one row
    // hundreds of screens tall that the wheel crawls through 48 pixels at a time, re-wrapped on every resize.
    private (string Preview, int Lines)? _collapsed;
    private string? _collapsedFor;

    private (string Preview, int Lines)? CollapsedText
    {
        get
        {
            if (Role != MessageRole.User) return null;
            if (!ReferenceEquals(_collapsedFor, Text))
            {
                _collapsedFor = Text;
                _collapsed = PastedText.Collapsed(Text);
            }
            return _collapsed;
        }
    }

    /// <summary>Whether the transcript shows only the first lines of this message.</summary>
    public bool IsLong => CollapsedText is not null;

    /// <summary>What the transcript shows: the whole text, or the first lines of a long message.</summary>
    public string ShownText => CollapsedText?.Preview ?? Text;

    /// <summary>"Show all 10,214 lines" under a long message.</summary>
    public string ShowAllLabel => CollapsedText is { } c
        ? $"Show all {AttachmentFormat.Count(c.Lines, "line", "lines")} · {AttachmentFormat.Bytes(TextUtil.Utf8NoBom.GetByteCount(Text))}"
        : "";

    partial void OnTextChanged(string value)
    {
        if (Role != MessageRole.User) return;
        OnPropertyChanged(nameof(IsLong));
        OnPropertyChanged(nameof(ShownText));
        OnPropertyChanged(nameof(ShowAllLabel));
    }

    public bool IsUser => Role == MessageRole.User;
    public bool IsAssistant => Role == MessageRole.Assistant;
    public bool IsNotice => Role == MessageRole.Notice;
    public bool IsError => Role == MessageRole.Error;
}

/// <summary>One tool call, from "started" through to its result.</summary>
public sealed partial class ToolEntryVM(string name, string preview, string? id = null, DateTimeOffset? at = null)
    : ChatEntryVM(id, at)
{
    public string Name { get; } = name;
    /// <summary>The interesting argument — a path, a command, a pattern.</summary>
    public string Preview { get; } = preview;
    /// <summary>One-line headline of the result.</summary>
    [ObservableProperty] private string? _summary;
    /// <summary>The full result, revealed on demand.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasOutput))] private string? _output;
    /// <summary>null while the call is still running.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsFinished), nameof(IsRunning), nameof(Failed))] private bool? _isOk;
    [ObservableProperty] private bool _isExpanded;

    /// <summary>What the tool captured (screenshots), as thumbnails — kept for the live session only.</summary>
    public ObservableCollection<BitmapSource> Images { get; } = [];

    public bool IsFinished => IsOk is not null;
    public bool IsRunning => IsOk is null;
    public bool Failed => IsOk == false;
    public bool HasOutput => !string.IsNullOrEmpty(Output);
    /// <summary>The model loaded one of the user's skills: shown as the skill it is, not as a tool call.</summary>
    public bool IsSkill => Name == "use_skill";
    /// <summary>"Using the pdf skill" for a skill; the tool's label otherwise.</summary>
    public string Label => !IsSkill ? Icons.ToolLabel(Name) : Preview.Length > 0 && Preview != Name ? $"Using the {Preview} skill" : "Using a skill";
    public string Glyph => Icons.ForTool(Name);
}

/// <summary>The agent's task list.</summary>
public sealed partial class TodosEntryVM(IReadOnlyList<TodoItem> items) : ChatEntryVM(null, null)
{
    public IReadOnlyList<TodoItem> Items { get; } = items;
    public string Progress => $"{Items.Count(i => i.Status == TodoStatus.Completed)}/{Items.Count}";
    /// <summary>"Plan updated — 3 of 7 done": the one line the card shrinks to while the plan panel shows the plan.</summary>
    public string Summary => $"Plan updated — {Items.Count(i => i.Status == TodoStatus.Completed)} of {Items.Count} done";
    /// <summary>The plan panel is open, so the card is a single line (unless the user asked to see it here).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowCompact))] private bool _compact;
    /// <summary>The user pressed "Show" on the single line.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowCompact))] private bool _expanded;

    public bool ShowCompact => Compact && !Expanded;
}

/// <summary>A record that the older part of a conversation was summarized to fit the context
/// window. Rendered as a divider; expands to show the summary the model now carries.</summary>
public sealed partial class CompactionEntryVM(int removed, string summary, DateTimeOffset? at = null) : ChatEntryVM(null, at)
{
    public int Removed { get; } = removed;
    public string Summary { get; } = summary;
    [ObservableProperty] private bool _isExpanded;
}

/// <summary>A pending permission question surfaced by the engine.</summary>
public sealed record GateVM(string Id, string Name, string Detail)
{
    public string ToolLabel => Icons.ToolLabel(Name);
}

public sealed record GoalState(string Text, int Round, DateTimeOffset Started);

/// <summary>The model didn't answer; the engine is waiting to try again.</summary>
public sealed record RetryState(int Attempt, string Reason, DateTimeOffset NextAttempt);

/// <summary>Observable state for one agent session, driven by <see cref="AgentHost"/>. UI-thread only.</summary>
public sealed partial class SessionVM : ObservableObject
{
    public string Id { get; }
    [ObservableProperty] private string _title;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ProjectName), nameof(WorkspacePath))] private string? _cwd;
    [ObservableProperty] private DateTimeOffset _updatedAt = DateTimeOffset.Now;
    /// <summary>Pinned when the session is created, like the preset a chat runs under.</summary>
    [ObservableProperty] private PermissionPreset _preset;

    public ObservableCollection<ChatEntryVM> Entries { get; } = [];
    public ObservableCollection<GateVM> PendingGates { get; } = [];
    /// <summary>Files this session's tools touched, newest first — shown as chips.</summary>
    public ObservableCollection<FileChange> ChangedFiles { get; } = [];

    [ObservableProperty] private IReadOnlyList<TodoItem> _todos = [];
    [ObservableProperty] private LlmUsage? _lastUsage;
    /// <summary>Best-known size of the current context, in tokens.</summary>
    [ObservableProperty] private int? _contextUsed;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))] private bool _running;
    [ObservableProperty] private bool _stopping;
    /// <summary>This chat's thinking level; null = the provider's default.</summary>
    [ObservableProperty] private ThinkingLevel? _thinking;
    /// <summary>The route (<see cref="ProviderProfile.RouteId"/>) this chat talks to — its own, stored with the conversation, so
    /// two chats in two projects never share a model by accident. Null = not chosen yet (it takes the primary when it next
    /// sends, and keeps it). Set through <see cref="AgentHost.SetChatRoute"/>.</summary>
    [ObservableProperty] private string? _routeId;
    /// <summary>The route this chat stays on, because another app started it there (see <see cref="AgentHost.StartDispatch"/>): no backups,
    /// no other subagent servers, and it stops rather than moves if the route goes. Choosing a model in the chat clears it.</summary>
    [ObservableProperty] private string? _pinnedRoute;
    /// <summary>The chat is Sensitive: only private models, or others through the shield (see <see cref="SensitivityPolicy"/>);
    /// engines that read files themselves (Claude Code) are refused. Stored with the conversation; set with /sensitive.</summary>
    [ObservableProperty] private bool _sensitive;
    /// <summary>The model's live reasoning for the current step (not persisted).</summary>
    [ObservableProperty] private string _reasoning = "";
    [ObservableProperty] private DateTimeOffset? _reasoningStarted;
    /// <summary>A transient status line ("Compacting conversation…") shown while busy.</summary>
    [ObservableProperty] private string? _activity;
    /// <summary>The active /goal, while its loop runs.</summary>
    [ObservableProperty] private GoalState? _goal;
    /// <summary>Tool call in flight — the composer shows what is happening.</summary>
    [ObservableProperty] private ToolEntryVM? _runningTool;
    /// <summary>Set while a failed model call waits to be retried.</summary>
    [ObservableProperty] private RetryState? _retry;
    /// <summary>The last /goal of this chat, so a bare /goal picks it back up.</summary>
    [ObservableProperty] private string? _lastGoal;
    /// <summary>The goal a /goal run paused on because the agent needed the user. The user's next ordinary
    /// message is the answer, and the goal carries on with it — no second /goal needed.</summary>
    [ObservableProperty] private string? _blockedGoal;
    /// <summary>The plan the agent proposed with exit_plan_mode (Plan-only chats), shown in the plan panel.</summary>
    [ObservableProperty] private string? _proposedPlan;
    /// <summary>Steps read from a plan the agent wrote in a message ("Here's my plan: 1. … 2. …"), for when
    /// it planned in prose instead of with todo_write. Newest plan wins.</summary>
    [ObservableProperty] private IReadOnlyList<string> _outline = [];
    /// <summary>Every subagent this chat has started, waiting or working or finished (the plan panel's Agents section).</summary>
    public ObservableCollection<AgentRunInfo> Agents { get; } = [];
    /// <summary>Background subagents this chat launched (the bar above the composer).</summary>
    public ObservableCollection<BackgroundAgentJob> BackgroundJobs { get; } = [];
    public IReadOnlyList<BackgroundAgentJob> RunningBackgroundJobs =>
        BackgroundJobs.Where(j => j.Status == BackgroundAgentStatus.Running).ToList();

    // MARK: Turn phases and the Prompts timeline (driven by AgentHost.Runs)

    /// <summary>The current (or last) turn's phases: Planning → Execution → Validation → Results, with the live draft, the
    /// ETA and the evidence. A new turn replaces it; the last one's card stays until then. Mutated in place by the host,
    /// which then calls <see cref="NotifyPhaseChanged"/>.</summary>
    [ObservableProperty] private TurnPhaseState? _turnPhase;
    /// <summary>Whether the phases run for this chat at all.</summary>
    public bool PhasesEnabled { get; set; } = true;
    /// <summary>Raised when the turn's phase moves (a step, the estimate, a loop-back, the results).</summary>
    public event Action? PhaseChanged;
    public void NotifyPhaseChanged()
    {
        OnPropertyChanged(nameof(TurnPhase));
        PhaseChanged?.Invoke();
    }

    /// <summary>Every prompt this chat has sent and how it went, oldest first (the Prompts timeline). Records are mutable;
    /// <see cref="PromptsChanged"/> says when one did.</summary>
    public List<PromptRecord> PromptHistory { get; } = [];
    public bool PromptHistoryLoaded { get; set; }
    /// <summary>The record of the prompt running now.</summary>
    public string? CurrentPromptId { get; set; }
    public PromptRecord? CurrentPrompt => CurrentPromptId is { } id ? PromptHistory.LastOrDefault(r => r.Id == id) : null;
    public event Action? PromptsChanged;
    public void NotifyPromptsChanged() => PromptsChanged?.Invoke();

    /// <summary>Tokens the current prompt's model calls used so far.</summary>
    public long TurnPromptTokens { get; set; }
    public long TurnCompletionTokens { get; set; }
    /// <summary>What the current (or last) turn ran on: the model id and the route's name.</summary>
    public string? TurnModel { get; set; }
    public string? TurnRoute { get; set; }
    /// <summary>Files this turn's tools touched (the evidence counts them per turn, not per chat).</summary>
    public HashSet<string> TurnFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Id of the assistant entry currently receiving streamed deltas.</summary>
    public string? StreamingId { get; private set; }

    /// <summary>Raised whenever the transcript grows or the streaming message changes, so the view
    /// can keep the bottom in sight.</summary>
    public event Action? ContentChanged;

    public SessionVM(string id, string title, string? cwd, PermissionPreset preset = PermissionPreset.WorkspaceWrite)
    {
        Id = id;
        _title = title;
        _cwd = cwd;
        _preset = preset;
    }

    public bool IsIdle => !Running;
    public bool IsEmpty => Entries.Count == 0;
    /// <summary>The project folder this chat works in; null for a chat without a project (a regular chat). A scratch folder
    /// is never a project.</summary>
    public string? WorkspacePath => string.IsNullOrEmpty(Cwd) || PlainChat.IsScratch(Cwd) ? null : Cwd;
    /// <summary>A regular chat: no project folder.</summary>
    public bool IsPlain => WorkspacePath is null;
    /// <summary>Where the agent works — in this chat's turns and its task list alike: the project folder, or (a regular chat)
    /// the chat's own scratch folder (legacy: one recorded under the data folder of DSH, the app's former name, is where it lives now).</summary>
    public string WorkingFolder => WorkspacePath ?? (PlainChat.IsScratch(Cwd) ? PlainChat.InCurrentRoot(Cwd!) : PlainChat.Folder(Id));
    public string? ProjectName => WorkspacePath is { } p ? System.IO.Path.GetFileName(p.TrimEnd('\\', '/')) : null;
    public string UpdatedLabel => Formatting.Relative(UpdatedAt);

    partial void OnUpdatedAtChanged(DateTimeOffset value) => OnPropertyChanged(nameof(UpdatedLabel));

    /// <summary>Refresh the "5m ago" label.</summary>
    public void RefreshRelativeTime() => OnPropertyChanged(nameof(UpdatedLabel));

    // MARK: - Reasoning

    public void AppendReasoning(string chunk)
    {
        if (Reasoning.Length == 0) ReasoningStarted = DateTimeOffset.Now;
        var next = Reasoning + chunk;
        if (next.Length > 6_000) next = TextUtil.Suffix(next, 4_000);
        Reasoning = next;
    }

    public void ClearReasoning()
    {
        if (Reasoning.Length > 0) Reasoning = "";
        ReasoningStarted = null;
    }

    // MARK: - Mutation

    public string AppendMessage(MessageRole role, string text, DateTimeOffset? at = null, IReadOnlyList<AttachmentBadge>? attachments = null)
    {
        var entry = new MessageEntryVM(role, text, at: at) { Attachments = attachments ?? [] };
        Entries.Add(entry);
        UpdatedAt = DateTimeOffset.Now;
        ContentChanged?.Invoke();
        return entry.Id;
    }

    public void Note(string text, MessageRole role = MessageRole.Notice) => AppendMessage(role, text);

    /// <summary>Replace the badges under a user message (once its attachments are prepared and saved).</summary>
    public void SetAttachments(string entryId, IReadOnlyList<AttachmentBadge> badges)
    {
        if (Entries.LastOrDefault(e => e.Id == entryId) is MessageEntryVM message) message.Attachments = badges;
    }

    /// <summary>Append streamed assistant text, opening a new bubble if none is live.</summary>
    public void AppendDelta(string chunk)
    {
        if (StreamingId is not null && Entries.LastOrDefault(e => e.Id == StreamingId) is MessageEntryVM { IsAssistant: true } live)
        {
            live.Text += chunk;
            ContentChanged?.Invoke();
        }
        else
        {
            StreamingId = AppendMessage(MessageRole.Assistant, chunk);
        }
        UpdatedAt = DateTimeOffset.Now;
    }

    /// <summary>Drop the live bubble: a failed attempt's partial reply is void, the retry streams the
    /// whole reply again.</summary>
    public void DropStreaming()
    {
        if (StreamingId is not null && Entries.LastOrDefault(e => e.Id == StreamingId) is MessageEntryVM message)
            Entries.Remove(message);
        StreamingId = null;
        ContentChanged?.Invoke();
    }

    /// <summary>Add or refresh a background agent's row.</summary>
    public void UpsertBackgroundJob(BackgroundAgentJob job)
    {
        var index = -1;
        for (var i = 0; i < BackgroundJobs.Count; i++)
        {
            if (BackgroundJobs[i].Id == job.Id)
            {
                index = i;
                break;
            }
        }
        if (index >= 0) BackgroundJobs[index] = job;
        else BackgroundJobs.Add(job);
        OnPropertyChanged(nameof(RunningBackgroundJobs));
    }

    /// <summary>Add or refresh a subagent run's row.</summary>
    public void UpsertAgent(AgentRunInfo run)
    {
        for (var i = 0; i < Agents.Count; i++)
        {
            if (Agents[i].Id != run.Id) continue;
            Agents[i] = run;
            return;
        }
        Agents.Add(run);
        // Reports are kept in full: a long-lived chat that fans out a lot must not carry them all for ever.
        while (Agents.Count > MaxAgentRows && Agents.FirstOrDefault(a => !a.IsRunning) is { } oldest) Agents.Remove(oldest);
    }

    private const int MaxAgentRows = 40;

    /// <summary>Forget the subagent runs that are over.</summary>
    public void ClearFinishedAgents()
    {
        foreach (var done in Agents.Where(a => !a.IsRunning).ToList()) Agents.Remove(done);
    }

    /// <summary>Close the streaming bubble so the next text starts a fresh one (a tool call in
    /// between is what makes this matter). An empty bubble left by a tool-only turn is dropped.</summary>
    public void EndStreaming()
    {
        if (StreamingId is not null
            && Entries.LastOrDefault(e => e.Id == StreamingId) is MessageEntryVM message
            && message.Text.Trim().Length == 0)
        {
            Entries.Remove(message);
        }
        StreamingId = null;
    }

    public void StartTool(string id, string name, string preview, DateTimeOffset? at = null)
    {
        EndStreaming();
        var entry = new ToolEntryVM(name, preview, id, at);
        Entries.Add(entry);
        RunningTool = entry;
        UpdatedAt = DateTimeOffset.Now;
        ContentChanged?.Invoke();
    }

    /// <summary>"Waiting for the mouse…" on the card of the machine tool that waits (and in the plan panel's
    /// activity line); null takes it away again.</summary>
    public void ShowMachineWait(string? text)
    {
        ToolEntryVM? card = null;
        for (var i = Entries.Count - 1; i >= 0 && card is null; i--)
        {
            if (Entries[i] is ToolEntryVM { IsRunning: true, Name: "mouse" or "keyboard" or "focus_app" } tool) card = tool;
        }
        if (text is not null)
        {
            if (card is not null) card.Summary = text;
            if (Running) Activity = text;
            return;
        }
        if (card?.Summary?.StartsWith("Waiting for", StringComparison.Ordinal) == true) card.Summary = null;
        if (Activity?.StartsWith("Waiting for", StringComparison.Ordinal) == true) Activity = null;
    }

    public void FinishTool(string id, bool ok, string summary, string? output)
    {
        if (Entries.LastOrDefault(e => e.Id == id) is not ToolEntryVM tool) return;
        tool.Summary = summary;
        tool.Output = output;
        tool.IsOk = ok;
        if (ReferenceEquals(RunningTool, tool)) RunningTool = null;
        UpdatedAt = DateTimeOffset.Now;
        ContentChanged?.Invoke();
    }

    /// <summary>Show what a tool captured on its card. Only the newest cards keep their pictures.</summary>
    public void AttachImages(string id, IEnumerable<BitmapSource> images)
    {
        if (Entries.LastOrDefault(e => e.Id == id) is not ToolEntryVM tool) return;
        tool.Images.Clear();
        foreach (var image in images) tool.Images.Add(image);
        var kept = 0;
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            if (Entries[i] is not ToolEntryVM { Images.Count: > 0 } card) continue;
            if (++kept > 30) card.Images.Clear();
        }
        ContentChanged?.Invoke();
    }

    /// <summary>Record a completed tool in one step (replaying a stored log).</summary>
    public void AddFinishedTool(string id, string name, string preview, string? summary, string? output, bool ok, DateTimeOffset at)
    {
        Entries.Add(new ToolEntryVM(name, preview, id, at) { Summary = summary, Output = output, IsOk = ok });
    }

    public void SetTodos(IReadOnlyList<TodoItem> items)
    {
        Todos = items;
        // A live todo list supersedes a plan read out of a message.
        if (items.Count > 0 && Outline.Count > 0) Outline = [];
        // Keep one todo card, at the point the list last changed.
        foreach (var old in Entries.OfType<TodosEntryVM>().ToList()) Entries.Remove(old);
        if (items.Count == 0) return;
        EndStreaming();
        Entries.Add(new TodosEntryVM(items));
        UpdatedAt = DateTimeOffset.Now;
        ContentChanged?.Invoke();
    }

    public void RecordFileChanges(IEnumerable<FileChange> changes)
    {
        foreach (var change in changes)
        {
            foreach (var existing in ChangedFiles.Where(c => string.Equals(c.Path, change.Path, StringComparison.OrdinalIgnoreCase)).ToList())
                ChangedFiles.Remove(existing);
            ChangedFiles.Insert(0, change);
        }
        while (ChangedFiles.Count > 40) ChangedFiles.RemoveAt(ChangedFiles.Count - 1);
        ContentChanged?.Invoke();
    }

    public void NotifyContentChanged() => ContentChanged?.Invoke();
}
