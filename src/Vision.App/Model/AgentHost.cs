using System.Net.Http;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Vision.App.Infrastructure;
using Vision.Core;

namespace Vision.App.Model;

/// <summary>Drives agent sessions in-process: one Engine per conversation, its event stream folded
/// into the session's timeline, items mirrored to the ConversationLog. This app IS the harness — no
/// external process. Everything here runs on the UI thread; engines run on the thread pool and their
/// events are marshalled back.</summary>
public sealed partial class AgentHost : ObservableObject
{
    public AppConfig Config { get; }
    public ConversationLog Log { get; }
    private readonly Dispatcher _dispatcher;

    public ObservableCollection<SessionVM> Sessions { get; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Selected))] private string? _selectedId;
    [ObservableProperty] private string? _banner;
    /// <summary>While the banner says an AWS sign-in ran out: the AWS CLI profile to sign in again.</summary>
    [ObservableProperty] private string? _awsSignInProfile;

    partial void OnBannerChanged(string? value) => AwsSignInProfile = null;
    /// <summary>Plugin manifests loaded for the current project, and any that failed.</summary>
    [ObservableProperty] private IReadOnlyList<PluginManifest> _plugins = [];
    [ObservableProperty] private IReadOnlyList<string> _pluginErrors = [];
    /// <summary>Instruction files and skills the active project contributes.</summary>
    [ObservableProperty] private ProjectContext? _projectContext;
    /// <summary>AI-generated, agent-proposed and imported skills waiting for approval.</summary>
    public ObservableCollection<SkillDraft> PendingDrafts { get; } = [];
    /// <summary>Bumped whenever skills change on disk through the app, so open views reload.</summary>
    [ObservableProperty] private int _skillsRevision;
    public SkillLocations SkillLocations { get; }

    private readonly Dictionary<string, Engine> _engines = new();
    /// <summary>What each session's engine was built with; a mismatch rebuilds it.</summary>
    private readonly Dictionary<string, EngineKey> _engineKeys = new();
    private readonly Dictionary<string, List<LlmMessage>> _transcripts = new();
    private readonly Dictionary<string, (TaskCompletionSource<bool> Tcs, string SessionId)> _gates = new();
    private readonly Dictionary<string, CancellationTokenSource> _runs = new();
    private readonly Dictionary<string, ComputerGrants> _grants = new();
    /// <summary>Each chat as the machine tools see it (its name on the glow, its turn at the real mouse).</summary>
    private readonly Dictionary<string, Vision.Windows.MachineChat> _machineChats = new();
    private readonly string _basePrompt;
    /// <summary>A test gave its own system prompt: every chat uses it, with or without a project.</summary>
    private readonly bool _customPrompt;
    /// <summary>Route id → context window learned from a server probe.</summary>
    private readonly Dictionary<string, int> _probedContext = new();
    /// <summary>Route id → what the server said it serves, and when we last asked.</summary>
    private readonly Dictionary<string, RouteInfo> _routeInfo = new();
    private readonly HashSet<string> _probingContext = new();
    /// <summary>Session id → the fully-built system prompt, cached so the context gauge reads a
    /// string instead of re-loading project files on every keystroke.</summary>
    private readonly Dictionary<string, string> _systemPrompts = new();
    /// <summary>Routes whose server rejected an image (learned at runtime).</summary>
    private readonly HashSet<string> _noVisionRoutes = new();

    /// <summary>Called when tools touch files, so code mode's editor can reload.</summary>
    public Action<IReadOnlyList<FileChange>>? OnFilesChanged { get; set; }
    /// <summary>True while the model server is switching models (Spark swapper).</summary>
    public Func<bool>? IsServerSwitching { get; set; }
    /// <summary>Handles /swap [model].</summary>
    public Action<string?, SessionVM>? OnSwapCommand { get; set; }
    /// <summary>The context window or served model changed (a probe landed, the route changed).</summary>
    public event Action? ContextInfoChanged;

    private sealed record RouteInfo(string ServedModel, IReadOnlyList<string> ServedModels, int? Context, DateTimeOffset At);

    private sealed record EngineKey(string Profile, int Window, ThinkingLevel? Thinking, PermissionPreset Preset,
                                    int Skills, bool ComputerTools, bool Vision, string Shell, long Vault,
                                    bool GoalMode, int Memory, string Agents, string Routing, bool Unattended);

    /// <summary>Everything about skills one turn needs: what exists, what's on, and the prompt text.</summary>
    private sealed record SkillState(IReadOnlyList<Skill> All, IReadOnlyList<Skill> Active, SkillPromptResult Result, int Signature);

    /// <param name="queueFile">Where every chat's task list is saved (default %APPDATA%\Vision\task-queue.json).</param>
    /// <param name="skillLocations">Where skills live (tests point it at a temp folder).</param>
    /// <param name="vault">The credentials vault (default: DPAPI-encrypted, under the data folder).</param>
    /// <param name="memory">The long-term memory (default: under the data folder).</param>
    public AgentHost(AppConfig config, ConversationLog log, Dispatcher dispatcher, string? systemPrompt = null,
                     string? queueFile = null, SkillLocations? skillLocations = null, CredentialVault? vault = null,
                     MemoryStore? memory = null)
    {
        Config = config;
        Log = log;
        _dispatcher = dispatcher;
        _basePrompt = systemPrompt ?? DefaultSystemPrompt;
        _customPrompt = systemPrompt is not null;
        SkillLocations = skillLocations ?? SkillLocations.Standard;
        Vault = vault ?? new CredentialVault();
        _queueFile = queueFile ?? TaskQueue.DefaultFilePath;
        Sessions.CollectionChanged += (_, e) =>
        {
            foreach (SessionVM vm in e.NewItems ?? Array.Empty<SessionVM>()) vm.PropertyChanged += OnSessionChanged;
            foreach (SessionVM vm in e.OldItems ?? Array.Empty<SessionVM>()) vm.PropertyChanged -= OnSessionChanged;
            OnPropertyChanged(nameof(AnyRunning));
            OnPropertyChanged(nameof(RunningCount));
        };
        InitChatRoutes();
        // Claude Code chats run the claude program Settings › Claude Code names (or the one found on PATH).
        ClaudeCodeClient.ConfiguredPath = config.ClaudeCodePath;
        Reload();
        // Skills that ship with the app (godot-debugging) are kept current on every launch.
        try
        {
            BuiltinSkills.Install(SkillLocations.BuiltinSkills);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the app works without them.
        }
        RefreshDrafts();
        InitQueue();
        InitAgents(memory);
        Vault.Changed += (_, _) => _dispatcher.BeginInvoke(() => VaultRevision = Vault.Revision);
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionVM.Running))
        {
            OnPropertyChanged(nameof(AnyRunning));
            OnPropertyChanged(nameof(RunningCount));
        }
    }

    public SessionVM? Selected => SelectedId is null ? null : Sessions.FirstOrDefault(s => s.Id == SelectedId);
    public SessionVM? Session(string id) => Sessions.FirstOrDefault(s => s.Id == id);
    public IReadOnlyList<SessionVM> RunningSessions => Sessions.Where(s => s.Running).ToList();
    public bool AnyRunning => Sessions.Any(s => s.Running);
    public int RunningCount => Sessions.Count(s => s.Running);
    /// <summary>Anything working — a turn, a queue task, or a background agent.</summary>
    public bool AnythingRunning => AnyRunning || Sessions.Any(s => s.RunningBackgroundJobs.Count > 0);

    public const string DefaultSystemPrompt =
        "You are a capable coding agent running inside a native Windows app, working in the user's project folder.\n\n" +
        "Use the tools to read, write, and search files and to run shell commands. Prefer small, verifiable steps:\n" +
        "read before you edit, and check your work after you change something. When a task needs more than a\n" +
        "couple of steps, first lay the plan out with `todo_write` (short, imperative steps), mark the step you are\n" +
        "on in_progress and each one completed the moment it is done — the user follows that list in the plan panel.\n\n" +
        "Keep going until the job is done. Don't stop to ask whether you should continue, or for permission to take\n" +
        "the obvious next step. Stop only when the work is finished, when you need something only the user can give,\n" +
        "or when a decision is genuinely theirs.\n\n" +
        "Parallel and background work: for a well-scoped side task, launch a subagent with `agent` — pick an\n" +
        "`agent_type` (explore: read-only research; plan; review; worker: makes changes). To fan several independent\n" +
        "tasks out at once use `delegate`; with several model servers configured they run on different machines.\n" +
        "With run_in_background: true a subagent works while you continue (several can run at once); you're told\n" +
        "when each finishes, and agent_status / agent_stop manage them. Long-running programs (dev servers, game\n" +
        "engines, REPLs) go in `process_start`. Follow-up work for later goes on this chat's task list with\n" +
        "`queue_task`; the chat works through the list, task by task, unattended.\n\n" +
        "Rules that matter:\n" +
        "- Never claim a command succeeded unless you ran it and saw the output.\n" +
        "- Prefer `edit` over `write_file` for changes to an existing file; rewriting a whole file loses work.\n" +
        "- Paths are resolved against the project folder. Stay inside it unless the user asks otherwise.\n" +
        "- This is Windows: `run_shell_command` runs in the shell named in the Environment section. Write commands\n" +
        "  for that shell (PowerShell unless it says otherwise), not for bash, and use Windows paths.\n" +
        "- When you finish, summarize what changed in a few lines. Reference files as `path:line`.";

    /// <summary>Post a notice into the chat the user is looking at.</summary>
    public void Broadcast(string text, bool error = false)
    {
        if (Selected is not { } vm)
        {
            Banner = text;
            return;
        }
        vm.Note(text, error ? MessageRole.Error : MessageRole.Notice);
        Log.RecordItem(vm.Id, error ? "error" : "notice", text, isError: error);
    }

    // MARK: - Project

    /// <summary>Adopt a project folder: reload its plugins, instructions, and skills.</summary>
    public void AdoptProject(string? path)
    {
        if (path is null)
        {
            ProjectContext = null;
            Plugins = [];
            PluginErrors = [];
            return;
        }
        ProjectContext = Vision.Core.ProjectContext.Load(path, SkillLocations, Config.SkillSources);
        var loaded = PluginLoader.Load(path);
        Plugins = loaded.Plugins;
        PluginErrors = loaded.Errors;
        // Sessions pick the new context up on their next turn.
        _engines.Clear();
        _systemPrompts.Clear();
    }

    /// <summary>Re-read instructions, skills, and plugins from disk.</summary>
    public void RefreshProjectContext()
    {
        _userPlugins = null;
        if (ProjectContext?.Root is { } root) AdoptProject(root);
        else
        {
            var loaded = PluginLoader.Load(null);
            Plugins = loaded.Plugins;
            PluginErrors = loaded.Errors;
            _engines.Clear();
        }
    }

    // MARK: - Sessions

    public SessionVM NewSession(string? cwd, PermissionPreset? preset = null, bool select = true)
    {
        var id = Guid.NewGuid().ToString();
        var resolved = preset ?? Config.AsPreset;
        var vm = new SessionVM(id, "New chat", cwd, resolved);
        Sessions.Insert(0, vm);
        Log.Upsert(id, cwd, "New chat", resolved.RawValue());
        AssignInitialChoice(vm);
        if (select) SelectedId = id;
        return vm;
    }

    public void DeleteSession(string id)
    {
        ResolveAllGates(id, false);
        if (_runs.Remove(id, out var cts)) cts.Cancel();
        _engines.Remove(id);
        _engineKeys.Remove(id);
        _transcripts.Remove(id);
        _systemPrompts.Remove(id);
        _grants.Remove(id);
        _machineChats.Remove(id);
        ChatProcesses.Forget(id);
        _vaultGrants.Remove(id);
        _autoContinuations.Remove(id);
        _agentQueuedCount.Remove(id);
        ForgetChatState(id);
        if (_backgroundPools.Remove(id, out var pool)) pool.StopAll();
        // Its task list goes with it.
        if (_chatQueues.TryGetValue(id, out var queueRun)) queueRun.Running = false;
        _queueSessions.Remove(id);
        Queue.RemoveChat(id);
        Config.SetQueueResume(id, false);
        Config.SetSkillsFor(id, null);
        Log.Delete(id);
        // Its Prompts timeline and the text of its attachments go with it.
        PromptStore.Delete(id);
        _chatTasksPaused.Remove(id);
        _pendingPromptSource.Remove(id);
        AttachmentStoreFor(id).RemoveAll();
        _pendingShield.Remove(id);
        if (Session(id) is { } vm) Sessions.Remove(vm);
        // A chat without a project takes its scratch folder with it, to the Recycle Bin: its task list, which worked in the
        // chat (and so in that folder), has just gone too.
        ForgetScratch(id);
        if (SelectedId == id) SelectedId = Sessions.FirstOrDefault()?.Id;
    }

    /// <summary>Move chat <paramref name="chatId"/>'s scratch folder to the Recycle Bin — never while another chat still works in
    /// it (one made for tasks from before task lists were per chat may have been given it).</summary>
    private void ForgetScratch(string chatId)
    {
        var scratch = PlainChat.Folder(chatId).TrimEnd('\\', '/');
        if (Sessions.Any(s => s.Cwd is { } cwd && string.Equals(PlainChat.InCurrentRoot(cwd).TrimEnd('\\', '/'), scratch, StringComparison.OrdinalIgnoreCase))) return;
        PlainChat.Delete(chatId);
    }

    public void RenameSession(string id, string title)
    {
        if (Session(id) is not { } vm) return;
        vm.Title = title;
        Log.Touch(id, title);
    }

    public void Reload()
    {
        foreach (var row in Log.List())
        {
            if (Sessions.Any(s => s.Id == row.Id)) continue;
            var cwd = string.IsNullOrEmpty(row.Cwd) ? null : row.Cwd;
            var vm = new SessionVM(row.Id, row.Title, cwd, PermissionPresets.FromRaw(row.Preset) ?? PermissionPreset.WorkspaceWrite)
            {
                UpdatedAt = row.UpdatedAt,
                // The chat's own model and thinking level (null in a chat from before they were stored).
                RouteId = row.Route,
                Thinking = ThinkingLevels.FromRaw(row.Thinking),
                Sensitive = row.Sensitive == true,
                PinnedRoute = row.PinnedRoute,
            };
            Sessions.Add(vm);
        }
        SortSessions();
        SelectedId ??= Sessions.FirstOrDefault()?.Id;
        if (Selected is { } selected) Hydrate(selected);
    }

    /// <summary>Newest first (the sidebar additionally floats running chats to the top).</summary>
    private void SortSessions()
    {
        var ordered = Sessions.OrderByDescending(s => s.UpdatedAt).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = Sessions.IndexOf(ordered[i]);
            if (current != i) Sessions.Move(current, i);
        }
    }

    /// <summary>Replay a stored transcript into a session's timeline. Called lazily, so a long
    /// session list stays cheap to open.</summary>
    public void Hydrate(SessionVM vm)
    {
        if (vm.Entries.Count > 0) return;
        foreach (var row in Log.LoadItems(vm.Id))
        {
            switch (row.Kind)
            {
                case "user": vm.AppendMessage(MessageRole.User, row.Text ?? "", row.At, row.Attachments); break;
                case "assistant":
                    vm.AppendMessage(MessageRole.Assistant, row.Text ?? "", row.At);
                    // The shield's line rides in ArgSummary (an assistant row has no arguments).
                    if (row.ArgSummary is { Length: > 0 } shield && vm.Entries[^1] is MessageEntryVM reply)
                    {
                        reply.Shield = shield;
                        reply.ShieldDetails = shield;
                    }
                    break;
                case "notice": vm.AppendMessage(MessageRole.Notice, row.Text ?? "", row.At); break;
                case "error": vm.AppendMessage(MessageRole.Error, row.Text ?? "", row.At); break;
                case "tool":
                    vm.AddFinishedTool($"log-{row.Seq}", row.ToolName ?? "tool", row.ArgSummary ?? "", row.Text, row.Output, !row.IsError, row.At);
                    break;
                case "compaction":
                    vm.Entries.Add(new CompactionEntryVM(int.TryParse(row.ArgSummary, out var removed) ? removed : 0, row.Text ?? "", row.At));
                    break;
            }
        }
        // A goal that paused for an answer before the app closed is still waiting for it.
        if (vm.BlockedGoal is null && PausedGoalIn(vm.Entries) is { } paused) vm.BlockedGoal = paused;
        // The Prompts timeline: read from its file, or backfilled from what was just replayed.
        LoadPromptHistory(vm);
        // A hydrated session has no live engine; rebuild the model transcript so a follow-up turn
        // keeps the conversation rather than starting over.
        if (!_transcripts.ContainsKey(vm.Id)) _transcripts[vm.Id] = ReplayMessages(vm.Entries);
    }

    /// <summary>Reconstruct a model-facing transcript from a rendered one. Tool calls are folded into
    /// the assistant text: replaying their exact call ids is not worth persisting, and the model only
    /// needs to know what happened.</summary>
    public static List<LlmMessage> ReplayMessages(IEnumerable<ChatEntryVM> entries)
    {
        var output = new List<LlmMessage>();
        var pendingTools = new List<string>();

        void FlushTools()
        {
            if (pendingTools.Count == 0) return;
            output.Add(LlmMessage.Assistant("[earlier tool activity]\n" + string.Join("\n", pendingTools)));
            pendingTools.Clear();
        }

        foreach (var entry in entries)
        {
            switch (entry)
            {
                case MessageEntryVM { Role: MessageRole.User } m:
                    FlushTools();
                    // What it carried: the files' content isn't in the display rows, but where it is saved is.
                    output.Add(LlmMessage.User(string.Join("\n\n", new[] { m.Text }.Concat(m.Attachments.Select(a => a.ReplayNote)).Where(t => t.Length > 0))));
                    break;
                case MessageEntryVM { Role: MessageRole.Assistant } m:
                    FlushTools();
                    output.Add(LlmMessage.Assistant(m.Text));
                    break;
                case ToolEntryVM tool:
                    pendingTools.Add($"- {tool.Name}({tool.Preview}) → {tool.Summary ?? (tool.IsOk == false ? "failed" : "ok")}");
                    break;
                case CompactionEntryVM note:
                    // A compacted transcript starts from its summary: everything above the divider
                    // is already folded into it.
                    pendingTools.Clear();
                    output.Clear();
                    output.Add(LlmMessage.SystemText(Compaction.SummaryHeader + note.Summary));
                    break;
            }
        }
        FlushTools();
        return output;
    }

    // MARK: - Engine

    private Engine BuildEngine(SessionVM vm, IProviderClient client, int window, ThinkingLevel? thinking, SkillState skillState, SubagentPool pool)
    {
        var sessionId = vm.Id;
        // A chat without a project is a regular conversation: it works in a scratch folder of its own, made on first use.
        var plain = vm.WorkspacePath is null;
        var workspace = plain ? PlainChat.Ensure(vm.WorkingFolder) : vm.WorkingFolder;
        var policy = new PermissionPolicy(vm.Preset, workspace);
        var profile = client.Profile;
        var shell = Config.ResolvedAgentShell;

        // Project instructions + skills + plugin tools are what make this a harness rather than a
        // chat window.
        var context = plain
            ? Vision.Core.ProjectContext.UserOnly(SkillLocations) // no project: still your own instructions
            : ProjectContext is { } pc && SamePath(pc.Root, workspace)
                ? pc
                : Vision.Core.ProjectContext.Load(workspace, SkillLocations, Config.SkillSources);
        // A big MEMORY.md would take the window over: keep its opening and make the rest searchable.
        if (context is not null && Config.MemoryEnabled) context = MemoryInstructions.Slim(context, Memory);
        var environment = Vision.Core.ProjectContext.EnvironmentBlock(workspace, profile.Model, vm.Preset, shell, scratch: plain);
        var basePrompt = _customPrompt ? _basePrompt : PlainChat.BasePrompt(vm.WorkspacePath, workspace, _basePrompt);
        var prompt = basePrompt + "\n\n" + (context?.PromptSupplement(environment, includeSkills: false) ?? environment);
        // Skills: always-on rules, the ones the user selected for this chat, and a catalog the
        // model can load from with use_skill.
        if (skillState.Result.Text.Length > 0) prompt += "\n\n" + skillState.Result.Text;
        // Long-term memory: how to use it, and the few pinned notes (searched notes ride with each message). A regular chat
        // has no project, so its notes are the ones for every chat.
        if (MemoryPromptFor(vm.WorkspacePath, vm.Preset != PermissionPreset.Plan) is { Length: > 0 } memorySection) prompt += "\n\n" + memorySection;
        // The credentials the agent may use, by name — never a value.
        if (VaultPrompt.Section(Vault.All, shell.Kind) is { Length: > 0 } vaultSection) prompt += "\n\n" + vaultSection;
        if (vm.Preset == PermissionPreset.Plan)
            prompt += "\n\n--- Plan mode ---\nDo not modify anything. Research and produce a plan, then call `exit_plan_mode` with it and stop.";
        // The chat's subagent servers are listed once, in the agent tool's description (cached with the tools), not here.
        _systemPrompts[sessionId] = prompt;

        // Background processes: the model's own long-running programs (game engines, dev servers,
        // REPLs) that keep running between tool calls. Seeing and steering the machine — screenshots,
        // windows, UI trees, clicks, keystrokes — is off with the computer-tools switch; each first use
        // in a chat still asks (the engine's gate). Both count as built-ins, so a plugin can't take
        // over their names.
        var agents = CatalogFor(vm);
        var builtins = ToolRegistry.Standard(0, shell, agents, pool).Adding(ExtraTools.Processes());
        if (Config.ComputerToolsEnabled) builtins = builtins.Adding(ExtraTools.Machine(MachineChatFor(vm)));
        // A regular chat has no project, so no project plugins: only the user's own.
        var extra = new List<IToolExecutor>(PluginLoader.Tools(plain ? UserPlugins() : Plugins, builtins.Names));
        if (skillState.Active.Count > 0) extra.Add(new UseSkillTool(skillState.Active));
        extra.Add(new VaultSearchTool(Vault));
        // Long attachments are read in parts; any attachment again after a restart.
        extra.Add(new ReadAttachmentTool(AttachmentStoreFor(sessionId)));
        extra.AddRange(ToolRegistry.BackgroundAgentTools());
        if (Config.MemoryEnabled)
        {
            // Plan only mode changes nothing — including what is remembered — so there the agent can only look things up.
            if (vm.Preset == PermissionPreset.Plan) extra.Add(new MemorySearchTool(Memory));
            else extra.AddRange(MemoryTools.All(Memory));
        }
        // While a /goal runs, the model tells the harness it is done (or stuck) with a tool call.
        if (vm.Goal is not null)
        {
            extra.Add(new GoalCompleteTool());
            extra.Add(new GoalBlockedTool());
        }
        if (vm.Preset != PermissionPreset.Plan)
        {
            extra.Add(new QueueAddTool((title, details, front, start) =>
                _dispatcher.InvokeAsync(() => AgentQueueTask(title, details, front, start, sessionId)).Task));
            extra.Add(new ProposeSkillTool(vm.WorkspacePath, SkillLocations));
        }
        var registry = builtins.Adding(extra);

        if (!_grants.TryGetValue(sessionId, out var grants)) _grants[sessionId] = grants = new ComputerGrants();
        var engine = new Engine(client, registry, prompt,
            new EngineConfig(profile.Model)
            {
                Temperature = profile.Temperature,
                MaxOutputTokens = profile.MaxOutputTokens,
                ContextWindow = window,
                Thinking = thinking,
                VisionEnabled = VisionOn(profile),
                Shell = shell,
                Retry = RetryPolicy,
                // The step limit is a checkpoint, not a stop: nobody has to type "continue". Twelve of them (360 steps in one
                // turn) is the backstop for a run that explores for ever with ever-different calls.
                ContinueAfterLimit = true,
                MaxCheckpoints = 12,
            },
            workspace, policy,
            (id, name, detail) => _dispatcher.InvokeAsync(() => AskGateAsync(sessionId, id, name, detail)).Task.Unwrap())
        {
            OnTodos = todos => _dispatcher.BeginInvoke(() => Session(sessionId)?.SetTodos(todos)),
            Compactor = (used, messages, ct) =>
                _dispatcher.InvokeAsync(() => CompactTranscriptAsync(sessionId, used, messages, ct)).Task.Unwrap(),
            ComputerGrants = grants,
            Reroute = ct => _dispatcher.InvokeAsync(() => RerouteForRetryAsync(sessionId)).Task.Unwrap(),
            Vault = Vault,
            VaultGrants = VaultGrantsFor(sessionId),
            BackgroundAgents = BackgroundPool(sessionId),
            Fleet = Fleet,
            // This chat's subagents fall back to this chat's own model, never to another chat's (the workers are shared), and go
            // only to the servers in its pool.
            FleetHome = FleetHome(vm, profile),
            FleetPool = pool,
            SensitiveChat = vm.Sensitive,
            Unattended = IsUnattended(sessionId),
            Roster = RosterFor(sessionId),
            AgentTypes = agents,
            Cache = CacheFor(sessionId),
        };
        _engines[sessionId] = engine;
        return engine;
    }

    /// <summary>The chat as the machine tools see it: made once per chat, so its place in line for the real
    /// mouse and the field it last clicked survive engine rebuilds.</summary>
    private Vision.Windows.MachineChat MachineChatFor(SessionVM vm)
    {
        if (_machineChats.TryGetValue(vm.Id, out var chat)) return chat;
        var id = vm.Id;
        chat = new Vision.Windows.MachineChat(id, () => vm.Title, () => Config.MachineOwnAppsOnly)
        {
            // "Waiting for the mouse…" in the chat's status line while it waits its turn.
            Status = text => _dispatcher.BeginInvoke(() => Session(id)?.ShowMachineWait(text)),
        };
        _machineChats[id] = chat;
        return chat;
    }

    /// <summary>Ctrl+Alt+. (from any app): stop every chat that is using the machine.</summary>
    public int StopMachineChats()
    {
        var stopped = 0;
        foreach (var session in Vision.Windows.MachineControl.Activity.Sessions())
        {
            if (Session(session.ChatId) is not { } vm) continue;
            if (vm.Running || _runs.ContainsKey(vm.Id) || _backgroundPools.ContainsKey(vm.Id))
            {
                StopSession(vm.Id);
                vm.Note("Stopped with Ctrl+Alt+. — the stop key works from any app while Vision uses the computer.");
                stopped++;
            }
        }
        Vision.Windows.MachineControl.Activity.Clear();
        return stopped;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Bridge the engine's permission gate to the UI: publish the gate on the session and
    /// await the user's answer.
    ///
    /// Queue tasks run unattended — a gate nobody answers would stall the whole queue. So for queue
    /// sessions the decision times out (auto-deny) after a few minutes, and the stall is surfaced in
    /// the chat.</summary>
    private async Task<bool> AskGateAsync(string sessionId, string callId, string name, string detail, CancellationToken cancel = default)
    {
        if (Session(sessionId) is not { } vm) return false;
        if (cancel.IsCancellationRequested) return false;
        // Tool-call ids repeat ("call-0" every turn, in every chat): key the question by a token of its
        // own, so two chats can't overwrite each other's answer and an old timer can't answer a newer
        // question.
        var gateId = $"{callId}#{Guid.NewGuid():N}";
        vm.PendingGates.Add(new GateVM(gateId, name, detail));
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gates[gateId] = (tcs, sessionId);
        vm.NotifyContentChanged();
        if (_queueSessions.ContainsKey(sessionId)) _ = AutoDenyAsync();
        // The asker can't use the answer any more (a Claude Code turn whose process ended): no, and the card comes down.
        using var withdrawn = cancel.Register(() => _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (_gates.Remove(gateId, out var entry)) entry.Tcs.TrySetResult(false);
        }));
        var decision = await tcs.Task;
        _gates.Remove(gateId);
        foreach (var answered in vm.PendingGates.Where(g => g.Id == gateId).ToList()) vm.PendingGates.Remove(answered);
        return decision;

        async Task AutoDenyAsync()
        {
            await Task.Delay(QueueGateTimeout);
            if (!_gates.Remove(gateId, out var entry)) return; // answered or cancelled
            foreach (var stale in vm.PendingGates.Where(g => g.Id == gateId).ToList()) vm.PendingGates.Remove(stale);
            var text = $"Permission for {Icons.ToolLabel(name)} auto-denied after {(int)QueueGateTimeout.TotalMinutes} minutes — queue tasks run unattended. " +
                       "To allow it, change the permission preset in Settings, then Resume the task.";
            vm.Note(text, MessageRole.Error);
            Log.RecordItem(sessionId, "error", text, isError: true);
            entry.Tcs.TrySetResult(false);
        }
    }

    /// <summary>How long a queue task's permission question waits before it is answered "no".</summary>
    public static TimeSpan QueueGateTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Answer a pending gate from the UI.</summary>
    public void AnswerGate(string sessionId, string gateId, bool allow)
    {
        if (!_gates.Remove(gateId, out var entry)) return;
        entry.Tcs.TrySetResult(allow);
        if (Session(sessionId) is { } vm)
            foreach (var answered in vm.PendingGates.Where(g => g.Id == gateId).ToList()) vm.PendingGates.Remove(answered);
    }

    private void ResolveAllGates(string sessionId, bool allow)
    {
        foreach (var (gateId, entry) in _gates.Where(g => g.Value.SessionId == sessionId).ToList())
        {
            _gates.Remove(gateId);
            entry.Tcs.TrySetResult(allow);
        }
    }

    // MARK: - Sending

    public void Send(string text, string sessionId, IReadOnlyList<MessageAttachment>? attachments = null)
    {
        attachments ??= [];
        if (Session(sessionId) is not { } vm) return;
        if (vm.Running || _runs.ContainsKey(sessionId))
        {
            vm.Note("The agent is still working; send again when it is done — or press Add to tasks (Enter while it works) to queue it.");
            return;
        }
        Dispatch(vm, text, attachments);
    }

    /// <summary>Run what was sent — or what a task added with "Add to tasks" sends when its turn comes: a command, a skill by
    /// name, or a turn. The work it started (null when it was over at once, or refused).</summary>
    private Task? Dispatch(SessionVM vm, string text, IReadOnlyList<MessageAttachment> attachments)
    {
        var sessionId = vm.Id;
        if (attachments.Count == 0 && SlashCommand.Parse(text) is { } command)
        {
            return RunCommand(command, vm);
        }
        // "/deploy staging": a skill or command invoked by name.
        if (attachments.Count == 0 && MatchSkillCommand(text, vm) is var (skill, args) && InvocationText(skill, args) is { } expanded)
        {
            if (ChatServerSwitching(vm))
            {
                RefuseWhileSwitching(vm, "The Spark is switching models right now — send again once it says it's ready.");
                return null;
            }
            var display = "/" + skill.Slug + (args.Length == 0 ? "" : " " + args);
            UserSpoke(sessionId);
            return StartRun(vm, ct => RunTurnAsync(vm, display, [], goal: null, modelText: expanded, ct));
        }
        if (ChatServerSwitching(vm))
        {
            RefuseWhileSwitching(vm, "The Spark is switching models right now — send again once it says it's ready (usually a few minutes).");
            return null;
        }
        // A goal that paused because the agent needed the user: this message is the answer, so the goal
        // carries on with it — no second /goal to type.
        UserSpoke(sessionId);
        if (vm.BlockedGoal is { } blocked)
            return StartRun(vm, ct => RunTurnAsync(vm, text, attachments, goal: blocked, modelText: null, ct, resumingGoal: true, reply: text));
        return StartRun(vm, ct => RunTurnAsync(vm, text, attachments, goal: null, modelText: null, ct));
    }

    /// <summary>Chats whose last Dispatch was refused because their model server is switching models (a queued prompt task
    /// reads it: refused is not run, so the task goes back in line instead of being marked complete).</summary>
    private readonly HashSet<string> _refusedWhileSwitching = new(StringComparer.Ordinal);

    /// <summary>Chats whose prompt task is being dispatched right now (their refusal note speaks of the task, not of
    /// sending again).</summary>
    private readonly HashSet<string> _dispatchingTask = new(StringComparer.Ordinal);

    /// <summary>The chat's model server is switching: what was sent is not run. A person is told to send again; a queued task
    /// goes back in line and runs once the server is ready.</summary>
    private void RefuseWhileSwitching(SessionVM vm, string forPerson)
    {
        _refusedWhileSwitching.Add(vm.Id);
        vm.Note(_dispatchingTask.Contains(vm.Id) ? QueuedWhileSwitchingNotice : forPerson);
    }

    /// <summary>What a chat says when its next queued task meets a model switch (the task waits; nothing is lost).</summary>
    public const string QueuedWhileSwitchingNotice =
        "The Spark is switching models right now — this task goes back in line and starts once it's ready.";

    /// <summary>The user sent something the chat took up: the automatic continuations start counting again, and a Stop no
    /// longer holds the chat back. (A message that was refused — the Spark is switching models — changes neither.)</summary>
    private void UserSpoke(string sessionId)
    {
        _autoContinuations[sessionId] = 0;
        _stoppedChats.Remove(sessionId);
    }

    /// <summary>Start background work for a session, cancellable with Stop.</summary>
    private Task StartRun(SessionVM vm, Func<CancellationToken, Task> work)
    {
        if (_runs.Remove(vm.Id, out var previous)) previous.Cancel();
        var cts = new CancellationTokenSource();
        _runs[vm.Id] = cts;
        return RunGuardedAsync(vm.Id, cts, work);
    }

    private async Task RunGuardedAsync(string sessionId, CancellationTokenSource cts, Func<CancellationToken, Task> work)
    {
        var stopped = false;
        _failedRuns.Remove(sessionId);
        try
        {
            await work(cts.Token);
        }
        finally
        {
            stopped = cts.IsCancellationRequested;
            if (_runs.TryGetValue(sessionId, out var current) && ReferenceEquals(current, cts)) _runs.Remove(sessionId);
            cts.Dispose();
            // The run is over, so is its use of the machine (the glow lingers a moment).
            Vision.Windows.MachineControl.Activity.EndRun(sessionId);
        }
        // A background agent that finished while this run was still going had nobody to report to (its event was ignored
        // because the chat was busy): now that the chat is idle, hand it over. Not after a Stop (which never wakes the chat
        // back up) nor a run that just reported a failure (the next request would most likely fail the same way).
        var failed = _failedRuns.Remove(sessionId);
        if (!stopped && !failed && Session(sessionId) is { } vm) ContinueAfterBackgroundAgents(vm);
    }

    /// <summary>The Spark finished switching models: chats whose background agents reported while it was busy carry on now.</summary>
    public void ResumeAfterServerSwitch()
    {
        foreach (var vm in Sessions.ToList()) ContinueAfterBackgroundAgents(vm);
    }

    // MARK: - Slash commands

    /// <summary>A slash command: the work it started, if it started any.</summary>
    private Task? RunCommand(SlashCommand command, SessionVM vm)
    {
        RunCommandNow(command, vm, out var work);
        return work;
    }

    private void RunCommandNow(SlashCommand command, SessionVM vm, out Task? work)
    {
        work = null;
        switch (command)
        {
            case SlashCommand.Help:
                vm.Note("Commands:\n" + string.Join("\n", SlashCommand.Catalog.Select(c => $"`{c.Usage}` — {c.Summary}")));
                break;

            case SlashCommand.Think think:
            {
                var fallback = ChatRoute(vm)?.Thinking;
                var rules = RequestRules(vm);
                if (think.Argument is null)
                {
                    var current = vm.Thinking ?? fallback;
                    vm.Note($"Thinking: **{current?.Label() ?? "server default"}**"
                            + (vm.Thinking is null ? " (from the provider setting)" : " (this chat)")
                            + ". Change it with `/think off|low|medium|high|max|default`."
                            + (current is { } shown && rules is not null ? rules.Caveat(shown) : ""));
                    return;
                }
                var arg = think.Argument.ToLowerInvariant();
                if (arg is "default" or "reset" or "auto")
                {
                    SetChatThinking(vm, null);
                    vm.Note($"Thinking back to the provider default ({fallback?.Label() ?? "server default"}).");
                }
                else if (ThinkingLevels.ParseUserInput(think.Argument) is { } level)
                {
                    SetChatThinking(vm, level);
                    vm.Note($"Thinking set to **{level.Label()}** for this chat — {level.Blurb().ToLowerInvariant()}."
                            + (rules?.Caveat(level) ?? ""));
                }
                else
                {
                    vm.Note($"Unknown level “{think.Argument}”. Use off, low, medium, high, max, or default.", MessageRole.Error);
                }
                break;
            }

            case SlashCommand.Context:
                work = StartRun(vm, async _ =>
                {
                    await ResolveRouteAsync(vm, force: true);
                    var limit = ContextLimit(vm);
                    var used = ContextUsed(vm.Id, "");
                    var model = ModelIdFor(vm) ?? "?";
                    vm.Note($"Context: {Fmt.N(used)} of {Fmt.N(limit)} tokens used ({(int)(used / (double)Math.Max(limit, 1) * 100)}%). " +
                            $"Model `{model}`; window {ContextSource(vm)}. Auto-compaction starts at {(int)(Compaction.TriggerFraction * 100)}%.");
                });
                break;

            case SlashCommand.Swap swap:
                if (OnSwapCommand is not null) OnSwapCommand(swap.Argument, vm);
                else vm.Note("Model switching isn't available.", MessageRole.Error);
                break;

            case SlashCommand.Skills:
                vm.Note(SkillsSummary(vm));
                break;

            case SlashCommand.Skill skill:
                work = HandleSkillCommand(skill.Argument, vm);
                break;

            case SlashCommand.Compact compact:
                work = StartRun(vm, ct => CompactNowAsync(vm, compact.Focus, ct));
                break;

            case SlashCommand.Remember remember:
                RememberCommand(remember.Text, vm);
                break;

            case SlashCommand.Memory memory:
                MemoryCommand(memory.Query, vm);
                break;

            case SlashCommand.Agents:
                AgentsCommand(vm);
                break;

            case SlashCommand.Sensitive sensitive:
                SensitiveCommand(sensitive.Argument, vm);
                break;

            case SlashCommand.Goal goal:
                if (goal.Text.Trim().ToLowerInvariant() is "stop" or "cancel" or "off" or "clear" or "drop" or "forget")
                {
                    // Give a goal up for good: one that paused for an answer, or one Stop left unfinished. (Needs no server, so it
                    // works while the Spark is switching too.)
                    var dropped = vm.BlockedGoal ?? vm.LastGoal ?? LastGoalIn(vm.Entries);
                    vm.BlockedGoal = null;
                    vm.LastGoal = null;
                    var text = dropped is null
                        ? "There is no goal to drop."
                        : $"Dropped the goal “{TextUtil.Prefix(dropped, 80)}”. What you send next is an ordinary message.";
                    vm.Note(text);
                    if (dropped is not null) Log.RecordItem(vm.Id, "notice", text);
                    UserSpoke(vm.Id);
                    return;
                }
                if (ChatServerSwitching(vm))
                {
                    RefuseWhileSwitching(vm, "The Spark is switching models right now — send again once it says it's ready.");
                    return;
                }
                if (goal.Text.Length == 0)
                {
                    // A bare /goal picks the chat's unfinished goal back up (after a Stop, a block the
                    // user has since answered, or a failure).
                    if ((vm.LastGoal ?? LastGoalIn(vm.Entries)) is not { } previous)
                    {
                        vm.Note("Usage: `/goal <what you want done>` — the agent keeps working, round after round, until it " +
                                "declares the goal complete (or needs you). Stop it any time with Ctrl+.");
                        return;
                    }
                    UserSpoke(vm.Id);
                    work = StartRun(vm, ct => RunTurnAsync(vm, previous, [], goal: previous, modelText: null, ct, resumingGoal: true));
                    return;
                }
                vm.BlockedGoal = null;
                UserSpoke(vm.Id);
                work = StartRun(vm, ct => RunTurnAsync(vm, goal.Text, [], goal: goal.Text, modelText: null, ct));
                break;

            case SlashCommand.Queue:
            {
                var stats = Queue.Stats(vm.Id);
                if (IsQueueRunning(vm.Id))
                    vm.Note($"This chat's task list is running — {stats.Queued} waiting, {stats.Completed} done so far. Ctrl+Shift+Q shows it; Stop there (or Ctrl+.) halts it.");
                else if (stats.Queued > 0)
                {
                    vm.Note($"Working this chat's task list: {stats.Queued} task{(stats.Queued == 1 ? "" : "s")} to go, one at a time, unattended.");
                    StartQueue(vm.Id);
                }
                else
                    vm.Note("This chat's task list is empty. Add tasks in the Task List panel (Ctrl+Shift+Q), then press Start — or /queue again.");
                break;
            }
        }
    }

    /// <summary>/compact: fold the conversation into a summary now, whatever its size.</summary>
    private async Task CompactNowAsync(SessionVM vm, string? focus, CancellationToken ct)
    {
        vm.Running = true;
        try
        {
            await CompactSessionAsync(vm, focus, ct);
        }
        catch (OperationCanceledException)
        {
            vm.Note("Stopped.");
        }
        finally
        {
            vm.Running = false;
            vm.Stopping = false;
            vm.Activity = null;
        }
    }

    /// <summary>Fold the conversation into a summary now (the /compact work, and a goal round that
    /// overflowed). Leaves the session's running state alone.</summary>
    private async Task CompactSessionAsync(SessionVM vm, string? focus, CancellationToken ct)
    {
        var sessionId = vm.Id;
        vm.Activity = "Compacting conversation…";
        try
        {
            if (!_transcripts.ContainsKey(sessionId)) Hydrate(vm);
            var messages = _transcripts.GetValueOrDefault(sessionId) ?? [];
            if (!messages.Any(m => m.Role == Vision.Core.MessageRole.User))
            {
                vm.Note("Nothing to compact yet.");
                return;
            }
            if (await ResolveRouteAsync(vm) is not { } profile)
            {
                vm.Note(LlmException.NoModel().Message, MessageRole.Error);
                return;
            }
            // Claude Code keeps its own session: it compacts that.
            if (ClaudeCodeRoutes.IsClaudeCode(profile))
            {
                await ClaudeCodeCompactAsync(vm, profile, focus, ct);
                return;
            }
            var prompt = _systemPrompts.GetValueOrDefault(sessionId) ?? _basePrompt;
            var before = TokenEstimate.Request(prompt, messages);
            if (Compaction.MakePlan(before, ContextLimit(profile), messages, force: true) is not { } plan)
            {
                vm.Note($"The conversation is already as compact as it gets (~{Fmt.N(before)} tokens).");
                return;
            }
            var summary = await Compaction.SummarizeAsync(MakeClient(profile, sessionId), plan, model: profile.Model, focus: focus,
                cancellationToken: ct);
            if (summary is null)
            {
                vm.Note("Compaction failed: the model did not return a summary. Nothing was changed.", MessageRole.Error);
                return;
            }
            if (ct.IsCancellationRequested) return;
            var newMessages = new List<LlmMessage> { LlmMessage.SystemText(Compaction.SummaryHeader + summary) };
            newMessages.AddRange(plan.ToKeep);
            ApplyCompaction(sessionId, plan, summary, newMessages);
            var after = TokenEstimate.Request(prompt, newMessages);
            var text = $"Compacted {plan.ToSummarize.Count} messages: ~{Fmt.N(before)} → ~{Fmt.N(after)} tokens.";
            vm.Note(text);
            Log.RecordItem(sessionId, "notice", text);
        }
        finally
        {
            vm.Activity = null;
        }
    }

    // MARK: - Turns

    /// <param name="automatic">The harness's own turn (background agents finished): no prompt of the person's, so no
    /// Prompts-timeline row and no phases of its own.</param>
    private async Task RunTurnAsync(SessionVM vm, string text, IReadOnlyList<MessageAttachment> attachments,
                                    string? goal, string? modelText, CancellationToken ct, bool resumingGoal = false,
                                    string? reply = null, bool recall = true, bool automatic = false)
    {
        var sessionId = vm.Id;
        vm.Running = true;
        vm.Stopping = false;
        // A new prompt begins a new turn: fresh phases. A resumed goal continues the same turn when it is still on screen.
        // Either way the prompt gets its row in the Prompts timeline.
        if (!automatic && (!resumingGoal || vm.TurnPhase is not { IsOver: false })) PhasesBegin(vm, modelText ?? goal ?? text);
        var source = goal is not null ? PromptSource.Goal : modelText is not null && text.StartsWith('/') ? PromptSource.Skill : PromptSource.Typed;
        if (!automatic) BeginPrompt(vm, text, source);
        // How it ended, for its timeline row and the chat's task list.
        var outcome = PromptOutcome.Completed;
        string? reason = null;
        try
        {
            if (goal is not null)
            {
                if (await RunGoalAsync(vm, goal, resumingGoal, ct, reply, attachments) is GoalOutcome.Blocked blocked)
                {
                    outcome = PromptOutcome.NeedsYou;
                    reason = blocked.Reason;
                }
            }
            else await TurnAsync(vm, modelText ?? text, text, attachments, ct, recall ? text : null);
            // The engine run ended: validate what happened (a goal validated itself before it was accepted).
            if (!automatic)
            {
                if (outcome == PromptOutcome.NeedsYou) PhasesAbandon(vm, "Paused — the agent needs you: " + reason, completed: false);
                else PhasesValidate(vm);
                if (outcome != PromptOutcome.NeedsYou && vm.TurnPhase is { Phase: TurnPhase.Results, LastValidationFailure: { } failure })
                {
                    outcome = PromptOutcome.Issues;
                    reason = failure;
                }
            }
        }
        catch (OperationCanceledException)
        {
            outcome = PromptOutcome.Stopped;
            reason = "Stopped before it finished.";
            if (!automatic) PhasesAbandon(vm, "Stopped before finishing.", completed: false);
            vm.EndStreaming();
            // A deliberate Stop is not a pause for an answer.
            vm.BlockedGoal = null;
            var note = goal is not null ? "Stopped. The goal was not finished — send `/goal` to pick it back up." : "Stopped.";
            vm.Note(note);
            Log.RecordItem(sessionId, "notice", note);
        }
        catch (LlmException error)
        {
            // A server overflow that still surfaced tells us the real window is smaller than we
            // budgeted — learn it so the gauge and future compaction use the true number.
            if (error.Kind == LlmErrorKind.Overflow && error.Limit > 0 && ChatRoute(vm) is { } provider)
            {
                _probedContext[provider.RouteId] = Math.Min(error.Limit, _probedContext.GetValueOrDefault(provider.RouteId, error.Limit));
                ContextInfoChanged?.Invoke();
            }
            ReportFailure(vm, Describe(error));
            if (error.InnerException is AwsSignInRequiredException expired) AwsSignInProfile = expired.Profile;
            (outcome, reason) = (PromptOutcome.Failed, Describe(error));
            if (!automatic) PhasesAbandon(vm, Describe(error), completed: false);
        }
        catch (Exception error)
        {
            ReportFailure(vm, Describe(error));
            (outcome, reason) = (PromptOutcome.Failed, Describe(error));
            if (!automatic) PhasesAbandon(vm, Describe(error), completed: false);
        }
        finally
        {
            vm.Running = false;
            vm.Stopping = false;
            vm.Goal = null;
            vm.Activity = null;
            vm.Retry = null;
            vm.RunningTool = null;
            vm.ClearReasoning();
            vm.EndStreaming();
            Log.Touch(sessionId);
            vm.UpdatedAt = DateTimeOffset.Now;
            SortSessions();
            if (!automatic) FinishPrompt(vm, outcome, reason);
            AfterWork(vm, outcome);
        }
    }

    private void ReportFailure(SessionVM vm, string message)
    {
        _failedRuns.Add(vm.Id);
        vm.EndStreaming();
        vm.Note(message, MessageRole.Error);
        Log.RecordItem(vm.Id, "error", message, isError: true);
        Banner = message;
    }

    /// <summary>One user message → one engine run (which may take many tool steps).</summary>
    /// <param name="recallQuery">What to look up in long-term memory for this turn (the user's message, or a
    /// goal); null for automatic messages, which never trigger a lookup.</param>
    private async Task<RunResult> TurnAsync(SessionVM vm, string modelText, string displayText,
                                            IReadOnlyList<MessageAttachment> attachments, CancellationToken ct,
                                            string? recallQuery = null)
    {
        var sessionId = vm.Id;
        // A chat whose timeline was never loaded (opened after a relaunch): bring it back first, so the
        // model keeps its history.
        Hydrate(vm);
        // Prime the model transcript from the timeline when it's missing (a chat from an earlier launch),
        // before this turn's entry is added so its text isn't sent twice.
        if (!_transcripts.ContainsKey(sessionId)) _transcripts[sessionId] = ReplayMessages(vm.Entries);
        var firstBadges = attachments.Select(a => AttachmentBadge.Of(a)).ToList();
        var userEntryId = vm.AppendMessage(MessageRole.User, displayText, attachments: firstBadges);
        Log.RecordItem(sessionId, "user", displayText, attachments: firstBadges);
        PromptEntryLanded(vm, userEntryId, displayText);
        // A plan proposed earlier in Plan only mode is old news once the chat works in another mode.
        if (vm.Preset != PermissionPreset.Plan) vm.ProposedPlan = null;
        // Notes that bear on this message ride along with it (a few, only when relevant — never a dump).
        if (recallQuery is not null && RecallBlockFor(vm, recallQuery) is { } recalled) modelText = recalled + "\n\n" + modelText;
        if (vm.Title == "New chat")
        {
            var first = displayText.Split('\n').FirstOrDefault() ?? displayText;
            var title = TextUtil.Prefix(first.Trim(), 48);
            if (title.Length > 0) RenameSession(sessionId, title);
        }

        // The chat's own model (an older chat that never chose takes the primary now, and keeps it). Re-read what the server
        // serves right now: the Spark can swap models between turns, and the window/model id must follow.
        PinRouteIfUnset(vm);
        RequireGuardianSignIn(vm);
        RequirePinnedRoute(vm);
        var profile = await ResolveRouteAsync(vm) ?? throw LlmException.NoModel();
        ct.ThrowIfCancellationRequested();
        // A Sensitive chat never sends to a model it may not use (Claude Code reads files itself; an online model with the
        // shield off would see everything): the message stays in the chat, marked as not delivered.
        if (SensitivityRefusal(vm, profile) is { } refusal)
        {
            MarkUndelivered(vm, userEntryId);
            throw new SensitivityRefusalException(refusal);
        }
        // What this turn runs on, for its timeline row and the activity menu.
        vm.TurnModel = profile.Model;
        vm.TurnRoute = RouteLabel(profile);
        // Claude Code runs the turn itself, with its own agent loop and tools (AgentHost.ClaudeCode.cs).
        if (ClaudeCodeRoutes.IsClaudeCode(profile)) return await ClaudeCodeTurnAsync(vm, modelText, attachments, profile, userEntryId, ct);
        if (_claudeChats.Remove(sessionId) && !_engineKeys.ContainsKey(sessionId))
        {
            var moved = $"This chat now uses {RouteLabel(profile)} — `{profile.Model}`.";
            vm.Note(moved);
            Log.RecordItem(sessionId, "notice", moved);
        }
        var window = ContextLimit(profile);
        var thinking = vm.Thinking;
        var skillState = BuildSkillState(vm);
        var shell = Config.ResolvedAgentShell;
        // The chat's subagent servers: its model leads them (the engine is rebuilt when the pool changes — never with load).
        var subagentPool = SubagentPoolFor(vm, profile, IsUnattended(sessionId));
        var key = new EngineKey(ProfileKey(profile), window, thinking, vm.Preset, skillState.Signature,
                                Config.ComputerToolsEnabled, VisionOn(profile), shell.Executable, Vault.Revision,
                                vm.Goal is not null, MemorySignature(vm), AgentsStampFor(vm), RoutingStamp(profile, subagentPool, vm.PinnedRoute is not null), IsUnattended(sessionId));
        if (_engineKeys.TryGetValue(sessionId, out var previous))
        {
            // The chat was moved to another model (its model menu): say which one answers from here on.
            if (!string.Equals(ServerOf(previous.Profile), ServerOf(ProfileKey(profile)), StringComparison.Ordinal))
                vm.Note($"This chat now uses {RouteLabel(profile)} — `{profile.Model}`, {Fmt.N(window)}-token window.");
            else if (!string.Equals(ModelOf(previous.Profile), profile.Model, StringComparison.Ordinal))
                vm.Note($"The server is now serving `{profile.Model}` (was `{ModelOf(previous.Profile)}`) — switched to it, {Fmt.N(window)}-token window.");
        }
        if (!_engines.TryGetValue(sessionId, out var engine) || _engineKeys.GetValueOrDefault(sessionId) != key)
        {
            engine = BuildEngine(vm, MakeChatClient(profile, sessionId), window, thinking, skillState, subagentPool);
            _engineKeys[sessionId] = key;
        }
        _retryRoute.Remove(sessionId);
        // A turn reports its own backup and its own masking.
        _failoverNoted.Remove(sessionId);
        _pendingShield.Remove(sessionId);
        var input = _transcripts[sessionId].ToList();
        // Attachments go as what this route takes: text read out of every file (a long one in parts the model reads with
        // read_attachment), pictures only to a model that takes them.
        if (attachments.Count > 0) attachments = await PrepareAttachmentsAsync(attachments, vm, userEntryId, profile, window);

        // Engine events arrive on a pool thread; hop to the UI thread so the timeline is only ever
        // mutated from one place.
        void Sink(EngineEvent evt) => _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => Apply(evt, sessionId));
        var progress = new RunProgress();
        RunResult result;
        try
        {
            result = await Task.Run(() =>
            {
                // Everything this run starts (shell commands, processes, subagents) belongs to this chat.
                ChatProcesses.Current = sessionId;
                return engine.RunAsync(input, modelText, attachments, Sink, ct, progress);
            }, CancellationToken.None);
        }
        catch (Exception)
        {
            // Keep what the run already did — tool calls that ran and their results — so the model's
            // memory matches the files on disk and the timeline, instead of rolling back to before
            // the run.
            if (progress.Salvaged is { } salvaged) _transcripts[sessionId] = salvaged.ToList();
            else
            {
                MarkUndelivered(vm, userEntryId); // the model never saw this message...
                // ...nor the reports of the background agents that came with it: they wait for the next one.
                if (progress.TakenReports.Count > 0 && _backgroundPools.TryGetValue(sessionId, out var pool)) pool.Requeue(progress.TakenReports);
            }
            vm.Retry = null;
            throw;
        }
        vm.Retry = null;
        _transcripts[sessionId] = result.Messages.ToList();
        vm.LastUsage = result.Usage;
        if (result.Usage is { } used)
        {
            vm.TurnPromptTokens += used.PromptTokens;
            vm.TurnCompletionTokens += used.CompletionTokens;
        }
        // How full the context is now: server-reported prompt tokens when available, otherwise a
        // character-based estimate of the whole request.
        vm.ContextUsed = result.LastPromptTokens ?? TokenEstimate.Request(engine.SystemPrompt, result.Messages);
        if (result.DeniedCount > 0) vm.Note($"{result.DeniedCount} tool call(s) were denied.");
        // A run that kept repeating one action stops itself (a goal handles that in its own loop).
        if (result.Stalled && vm.Goal is null)
        {
            vm.Note("⚠️ " + result.LastReplyText, MessageRole.Error);
            Log.RecordItem(sessionId, "error", result.LastReplyText, isError: true);
        }
        // The only limit left: hundreds of steps in one turn, none of them the same. (A /goal simply goes on in its next round.)
        if (result.HitIterationLimit && vm.Goal is null)
        {
            var steps = engine.Config.MaxIterations * Math.Max(1, engine.Config.MaxCheckpoints);
            var text = $"⏸ Stopped after {steps} steps in one turn without finishing — a safety limit, so a run can't spend for ever. Send a message (“keep going” will do) to carry on.";
            vm.Note(text);
            Log.RecordItem(sessionId, "notice", text);
        }
        return result;
    }

    // MARK: - Attachments

    /// <summary>Where a chat's attachments are saved (their full text, for read_attachment).</summary>
    public AttachmentStore AttachmentStoreFor(string sessionId) =>
        AttachmentStore.ForChat(sessionId, AttachmentsRootForTesting ?? AppPaths.Attachments);

    /// <summary>Tests: keep chats' attachments here instead of the data folder.</summary>
    public string? AttachmentsRootForTesting { get; set; }

    /// <summary>What the main route takes besides text — what the composer's chips describe.</summary>
    public AttachmentRoute AttachmentRouteFor(ProviderProfile? profile = null)
    {
        profile ??= Config.ActiveProvider;
        if (profile is null) return Core.AttachmentRoute.TextOnly;
        if (ClaudeCodeRoutes.IsClaudeCode(profile)) return ClaudeCodeAttachments;
        var route = Core.AttachmentRoute.Of(profile);
        return VisionOn(profile) ? route : route with { Vision = false };
    }

    /// <summary>Read (if the composer hadn't), budget, split and save a message's attachments off the UI thread, and put
    /// the final badges — how each one went — under the user's message and in its log row.</summary>
    private async Task<IReadOnlyList<MessageAttachment>> PrepareAttachmentsAsync(IReadOnlyList<MessageAttachment> attachments, SessionVM vm,
                                                                                string entryId, ProviderProfile profile, int window)
    {
        var store = AttachmentStoreFor(vm.Id);
        var route = AttachmentRouteFor(profile);
        var previous = vm.Activity;
        if (attachments.Any(a => a.Digest is null)) vm.Activity = "Reading attachments…";
        AttachmentPrep.Prepared prepared;
        try
        {
            prepared = await Task.Run(() => AttachmentPrep.Prepare(attachments, window, store));
        }
        finally
        {
            vm.Activity = previous;
        }
        // How each one actually goes once the route's limits apply (past them: "sent as text").
        var badges = prepared.Badges.Zip(AttachmentWire.Deliveries(prepared.Attachments, route),
            (badge, delivery) => badge with { Label = badge.Label + " · " + delivery.Label() }).ToList();
        vm.SetAttachments(entryId, badges);
        Log.SetAttachments(vm.Id, badges);
        return prepared.Attachments;
    }

    /// <summary>Changes when the memory part of the system prompt would (memory on/off, a pinned note edited),
    /// so the engine is rebuilt exactly then and the server's prompt cache stays warm the rest of the time.</summary>
    private int MemorySignature(SessionVM vm) => MemoryPromptFor(vm.WorkspacePath, vm.Preset != PermissionPreset.Plan).GetHashCode();

    /// <summary>Every model client the host makes goes through here, so tests can swap the network. A route that isn't
    /// private gets the sensitive-data shield (reporting to <paramref name="sessionId"/>'s chat, when given).</summary>
    private IProviderClient MakeClient(ProviderProfile profile, string? sessionId = null) =>
        ProviderClients.Create(profile, HttpHandlerForTesting, ShieldFor(sessionId),
            sessionId is null ? null : text => _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => OnAdapted(sessionId, text)));

    /// <summary>A parameter the server refused was dropped (and remembered): one line in the chat, not an error.</summary>
    private void OnAdapted(string sessionId, string text)
    {
        if (Session(sessionId) is not { } vm) return;
        vm.Note(text);
        Log.RecordItem(sessionId, "notice", text);
    }

    /// <summary>Tests: answer model requests from this handler instead of the network.</summary>
    public HttpMessageHandler? HttpHandlerForTesting { get; set; }

    /// <summary>How engine runs retry a model that doesn't answer (tests shorten the waits).</summary>
    public RetryPolicy RetryPolicy { get; set; } = RetryPolicy.Standard;

    private static string ProfileKey(ProviderProfile p) =>
        string.Join("|", p.Kind, p.Name, p.BaseUrl, p.Model, p.Temperature, p.MaxOutputTokens, p.ContextWindow,
            p.ReasoningEffort, p.Vision, p.ApiKey?.GetHashCode(),
            p.CustomHeaders is null ? "" : string.Join(",", p.CustomHeaders.OrderBy(h => h.Key).Select(h => $"{h.Key}={h.Value}")));

    private static string ModelOf(string profileKey) => profileKey.Split('|') is { Length: > 3 } parts ? parts[3] : "";

    /// <summary>The kind, name and address in a profile key: which route it is, whatever model it serves.</summary>
    private static string ServerOf(string profileKey) => string.Join("|", profileKey.Split('|').Take(3));

    private void Apply(EngineEvent evt, string sessionId)
    {
        if (Session(sessionId) is not { } vm) return;
        switch (evt)
        {
            case EngineEvent.TextDelta d:
                vm.Retry = null;
                vm.ClearReasoning();
                vm.AppendDelta(d.Text);
                // The planning draft streams live: the first text of a turn is the plan being written.
                PhasesDraft(vm, d.Text);
                break;

            case EngineEvent.ReasoningDelta r:
                vm.Retry = null;
                vm.AppendReasoning(r.Text);
                break;

            case EngineEvent.Retrying retrying:
            {
                // The failed attempt's partial reply is void — the retry streams the whole reply
                // again — so drop its bubble.
                vm.DropStreaming();
                vm.ClearReasoning();
                vm.Retry = new RetryState(retrying.Attempt, retrying.Reason, DateTimeOffset.Now + retrying.Delay);
                if (retrying.Attempt == 1)
                {
                    var text = $"⚠️ The model didn't answer ({retrying.Reason}). Retrying automatically until it's available — press Stop to give up.";
                    vm.Note(text);
                    Log.RecordItem(sessionId, "notice", text);
                    if (_queueSessions.TryGetValue(sessionId, out var taskId))
                        Queue.Note(taskId, $"Model unavailable ({retrying.Reason}) — retrying until it answers.");
                }
                break;
            }

            case EngineEvent.Recovered recovered:
            {
                vm.Retry = null;
                var text = $"✓ The model is answering again (after {recovered.Attempts} retr{(recovered.Attempts == 1 ? "y" : "ies")}).";
                vm.Note(text);
                Log.RecordItem(sessionId, "notice", text);
                if (_queueSessions.TryGetValue(sessionId, out var taskId)) Queue.Note(taskId, text);
                break;
            }

            case EngineEvent.AssistantMessage m:
            {
                // Fold a turn's complete text if deltas never arrived, then close the bubble so any
                // tool calls render after it.
                var replyId = vm.StreamingId;
                if (vm.StreamingId is null && m.Text.Length > 0) replyId = vm.AppendMessage(MessageRole.Assistant, m.Text);
                // What the shield masked for this reply shows on it (its request reported just before it ended).
                ShieldReport? shield = null;
                if (m.Text.Length > 0 && _pendingShield.Remove(sessionId, out var masked) && masked.Summary is { } summary)
                {
                    shield = masked;
                    if (vm.Entries.LastOrDefault(e => e.Id == replyId) is MessageEntryVM reply)
                    {
                        reply.Shield = summary;
                        reply.ShieldDetails = masked.Details;
                    }
                }
                if (m.Text.Length > 0) Log.RecordItem(sessionId, "assistant", m.Text, argSummary: shield?.Summary);
                vm.EndStreaming();
                // A plan written in prose ("Here's my plan: 1. … 2. …") shows in the plan panel too, unless
                // the agent is keeping a live todo list that is still in progress.
                if (m.Calls.Count > 0 || m.Text.Length > 0)
                {
                    var steps = PlanExtractor.FromAssistantText(m.Text);
                    if (steps.Count >= PlanExtractor.MinSteps && (vm.Todos.Count == 0 || vm.Todos.All(t => t.Status == TodoStatus.Completed)))
                    {
                        vm.Outline = steps;
                        AutoOpenPlan(vm);
                    }
                }
                break;
            }

            case EngineEvent.ToolStarted t:
                vm.ClearReasoning();
                vm.StartTool(t.Id, t.Name, t.Preview);
                break;

            case EngineEvent.ToolFinished f:
                vm.FinishTool(f.Id, f.Ok, f.Summary, f.Output);
                if (f.Name == "propose_skill" && f.Ok && f.Output.Contains("Saved draft", StringComparison.Ordinal))
                {
                    RefreshDrafts();
                    vm.Note("📝 The agent drafted a skill. It is not active until you approve it — open **Skills** under the composer (or Settings › Skills) to review it.");
                }
                var preview = vm.Entries.LastOrDefault(e => e.Id == f.Id) is ToolEntryVM card ? card.Preview : null;
                Log.RecordItem(sessionId, "tool", f.Summary, f.Name, preview, f.Output, !f.Ok);
                // A tool call is a step of the turn — and means the plan is settled (the model has moved to work).
                PhasesStep(vm, $"{Icons.ToolLabel(f.Name)} {TextUtil.Prefix((preview ?? f.Summary).Replace('\n', ' '), 120)}" + (f.Ok ? "" : " — failed"));
                PhasesPlanSettled(vm);
                break;

            case EngineEvent.ToolImages images:
                vm.AttachImages(images.Id, images.Images.Select(i => ImageTools.Thumbnail(i.Data)).OfType<System.Windows.Media.Imaging.BitmapSource>());
                break;

            case EngineEvent.FilesChanged changes:
                vm.RecordFileChanges(changes.Changes);
                PhasesFilesChanged(vm, changes.Changes);
                OnFilesChanged?.Invoke(changes.Changes);
                break;

            case EngineEvent.Finished finished:
                vm.LastUsage = finished.Usage;
                vm.EndStreaming();
                // A question-only turn settles its plan on the prose it wrote.
                PhasesPlanFromProse(vm);
                break;

            case EngineEvent.PermissionQuestion q:
                if (!vm.PendingGates.Any(g => g.Id == q.Id)) vm.PendingGates.Add(new GateVM(q.Id, q.Name, q.Detail));
                break;

            case EngineEvent.Todos todos:
                vm.SetTodos(todos.Items);
                AutoOpenPlan(vm);
                // The todo list is the plan: it settles (or refines) the estimate.
                PhasesTodos(vm);
                break;

            case EngineEvent.PlanProposed proposed:
                vm.ProposedPlan = proposed.Text;
                AutoOpenPlan(vm);
                break;

            case EngineEvent.Failed failed:
                vm.Note(failed.Message, MessageRole.Error);
                Banner = failed.Message;
                break;
        }
    }

    private bool VisionOn(ProviderProfile? profile)
    {
        if (profile is null) return true;
        if (_noVisionRoutes.Contains(profile.RouteId)) return false;
        // Unset: what the model is known to take (Z.ai's text models read no images), else assume it can.
        return profile.Vision ?? ModelRequestRules.For(profile).AcceptsImages ?? true;
    }

    // MARK: - Skills

    /// <summary>Reload the approval queue (and tell open views to reload).</summary>
    public void RefreshDrafts()
    {
        PendingDrafts.Clear();
        foreach (var draft in SkillDrafts.List(SkillLocations)) PendingDrafts.Add(draft);
        SkillsRevision++;
    }

    /// <summary>Every skill visible from a chat's project (shadowed ones included).</summary>
    public IReadOnlyList<Skill> Skills(SessionVM? vm) =>
        SkillCatalog.LoadAll(vm is null ? ProjectContext?.Root : vm.WorkspacePath, SkillLocations, Config.SkillSources);

    public SkillSelection Selection(string sessionId)
    {
        var chosen = Config.SkillsFor(sessionId);
        return new SkillSelection
        {
            Pinned = new HashSet<string>(chosen.Pinned, StringComparer.OrdinalIgnoreCase),
            Auto = chosen.Auto,
            Disabled = new HashSet<string>(Config.DisabledSkills, StringComparer.OrdinalIgnoreCase),
        };
    }

    private SkillState BuildSkillState(SessionVM vm)
    {
        var all = Skills(vm);
        var selection = Selection(vm.Id);
        var result = SkillPrompt.Build(all, selection);
        var active = SkillPrompt.Active(all, selection);
        var hash = new HashCode();
        hash.Add(result.Text);
        foreach (var s in active) hash.Add(s.Id);
        return new SkillState(all, active, result, hash.ToHashCode());
    }

    public bool IsPinned(Skill skill, SessionVM vm) =>
        Config.SkillsFor(vm.Id).Pinned.Contains(skill.Id, StringComparer.OrdinalIgnoreCase);

    public void SetPinned(Skill skill, bool on, SessionVM vm)
    {
        var chosen = Config.SkillsFor(vm.Id);
        var pinned = chosen.Pinned.Where(p => !string.Equals(p, skill.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (on) pinned.Add(skill.Id);
        Config.SetSkillsFor(vm.Id, chosen with { Pinned = pinned });
        SkillsRevision++;
    }

    public void SetAutoSkills(bool on, SessionVM vm)
    {
        Config.SetSkillsFor(vm.Id, Config.SkillsFor(vm.Id) with { Auto = on });
        SkillsRevision++;
    }

    public void ClearPinnedSkills(SessionVM vm)
    {
        Config.SetSkillsFor(vm.Id, Config.SkillsFor(vm.Id) with { Pinned = [] });
        SkillsRevision++;
    }

    /// <summary>/skills: what exists and what this chat uses.</summary>
    private string SkillsSummary(SessionVM vm)
    {
        var all = Skills(vm).Where(s => !s.Shadowed).ToList();
        if (all.Count == 0)
            return "No skills yet. Create one in Settings › Skills, import from Claude/Cursor, or try `/skill new <what it should do>`.";
        var selection = Selection(vm.Id);
        var lines = all.Take(40).Select(s =>
        {
            var mark = selection.Pinned.Contains(s.Id) ? "📌" : selection.Disabled.Contains(s.Id) ? "○ off" : "•";
            var tags = string.Join(", ", new[] { s.Origin.Label(), s.Kind == SkillKind.Skill ? null : s.Kind.Label().ToLowerInvariant(), s.AlwaysApply ? "always" : null }
                .Where(t => t is not null));
            return $"{mark} **{s.Name}** ({tags}) — {TextUtil.Prefix(s.Description, 90)}";
        });
        var text = $"Skills ({all.Count}):\n" + string.Join("\n", lines);
        if (all.Count > 40) text += $"\n… and {all.Count - 40} more (see the Skills button).";
        text += $"\n\nAuto-pick by the model: **{(selection.Auto ? "on" : "off")}**. `/skill <name>` selects one for this chat, `/<name>` runs it, `/skill new <what>` writes a new one.";
        if (PendingDrafts.Count > 0)
            text += $"\n📝 {PendingDrafts.Count} draft{(PendingDrafts.Count == 1 ? "" : "s")} awaiting your approval.";
        return text;
    }

    private Task? HandleSkillCommand(string? arg, SessionVM vm)
    {
        if (string.IsNullOrEmpty(arg))
        {
            vm.Note(SkillsSummary(vm));
            return null;
        }
        var lower = arg.ToLowerInvariant();
        foreach (var verb in new[] { "new ", "generate ", "create " })
        {
            if (!lower.StartsWith(verb, StringComparison.Ordinal)) continue;
            var goal = arg[verb.Length..].Trim();
            if (goal.Length == 0) break;
            return StartRun(vm, ct => GenerateSkillFromChatAsync(vm, goal, ct));
        }
        if (lower is "off" or "clear" or "none")
        {
            ClearPinnedSkills(vm);
            vm.Note("Cleared the skills selected for this chat.");
            return null;
        }
        if (lower is "auto on" or "auto off")
        {
            SetAutoSkills(lower == "auto on", vm);
            vm.Note($"The model {(lower == "auto on" ? "can" : "can no longer")} choose skills on its own in this chat.");
            return null;
        }
        var live = SkillPrompt.Active(Skills(vm), Selection(vm.Id));
        var key = SkillNaming.Slug(arg);
        var skill = live.FirstOrDefault(s => s.Slug == key) ?? live.FirstOrDefault(s => key.Length > 0 && s.Slug.Contains(key));
        if (skill is null)
        {
            vm.Note($"No active skill matches “{arg}”. `/skills` lists them.", MessageRole.Error);
            return null;
        }
        var now = !IsPinned(skill, vm);
        SetPinned(skill, now, vm);
        vm.Note(now
            ? $"📌 **{skill.Name}** is selected for this chat — its instructions are included from the next message."
            : $"**{skill.Name}** is no longer selected for this chat.");
        return null;
    }

    /// <summary>/name args: the skill or command being run, if the text names one.</summary>
    private (Skill Skill, string Args)? MatchSkillCommand(string text, SessionVM vm)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('/')) return null;
        var headLength = 0;
        while (headLength < trimmed.Length && !char.IsWhiteSpace(trimmed[headLength])) headLength++;
        var word = trimmed[1..headLength];
        if (word.Length == 0 || word.Contains('/') || word.Contains('\\')) return null;
        var args = trimmed[headLength..].Trim();
        var key = SkillNaming.Slug(word);
        var live = SkillPrompt.Active(Skills(vm), Selection(vm.Id));
        var skill = live.FirstOrDefault(s => s.UserInvocable && (s.Slug == key || string.Equals(s.Name, word, StringComparison.OrdinalIgnoreCase)));
        return skill is null ? null : (skill, args);
    }

    /// <summary>The message the model sees when the user runs a skill.</summary>
    private static string? InvocationText(Skill skill, string arguments)
    {
        if (skill.Document() is not { } doc) return null;
        var body = SkillArguments.Expand(doc.Body, arguments).Trim();
        if (body.Length > SkillPrompt.PerSkillCap) body = TextUtil.Prefix(body, SkillPrompt.PerSkillCap) + "\n[… truncated]";
        var withArgs = arguments.Length == 0 ? "" : $" with: {arguments}";
        return $"The user ran the skill “{skill.Name}”{withArgs}. Follow it now.\n\n" +
               $"<skill name=\"{skill.Name}\" directory=\"{skill.Directory}\">\n{body}\n</skill>";
    }

    /// <summary>Ask the model to write a skill (optionally from a chat, or improving one).</summary>
    public async Task<GeneratedSkill> GenerateSkillAsync(string goal, SessionVM? vm, string? improving = null,
                                                         CancellationToken ct = default)
    {
        var profile = await (vm is null ? ResolveRouteAsync() : ResolveRouteAsync(vm)) ?? throw LlmException.NoModel();
        if (vm is not null && !_transcripts.ContainsKey(vm.Id)) Hydrate(vm);
        var conversation = vm is not null ? _transcripts.GetValueOrDefault(vm.Id) ?? [] : [];
        var request = new SkillGenerationRequest(goal, conversation, Skills(vm).Select(s => s.Name).ToList(), improving);
        return await SkillGenerator.GenerateAsync(MakeClient(profile), profile.Model, request, ct: ct);
    }

    /// <summary>/skill new …: write a skill from this chat and queue it for approval.</summary>
    private async Task GenerateSkillFromChatAsync(SessionVM vm, string goal, CancellationToken ct)
    {
        vm.Running = true;
        vm.Activity = "Writing a skill…";
        try
        {
            var generated = await GenerateSkillAsync(goal, vm, ct: ct);
            if (ct.IsCancellationRequested) return;
            var scope = vm.WorkspacePath is not null ? SkillScope.Project : SkillScope.User;
            var draft = SkillDrafts.Create(generated.Text, scope: scope, projectRoot: vm.WorkspacePath, source: "chat",
                locations: SkillLocations);
            RefreshDrafts();
            var warn = generated.Issues.Where(i => i.Severity >= SkillIssueSeverity.Warning).Select(i => i.Message).ToList();
            vm.Note($"📝 Drafted skill **{draft.Name}** — {draft.Description}\nIt is not active yet: open **Skills** under the composer to review, edit and approve it."
                    + (warn.Count == 0 ? "" : "\nNotes: " + string.Join(" ", warn)));
        }
        catch (OperationCanceledException)
        {
            vm.Note("Stopped.");
        }
        catch (Exception error)
        {
            vm.Note($"Couldn't write the skill: {Describe(error)}", MessageRole.Error);
        }
        finally
        {
            vm.Running = false;
            vm.Stopping = false;
            vm.Activity = null;
        }
    }

    // MARK: - Stopping

    /// <param name="pauseTasks">Stop means stop: what the chat has queued with "Add to tasks" waits until Resume (false when a
    /// task is taken off the list, which is not a Stop of the whole list).</param>
    public void StopSession(string id, bool pauseTasks = true)
    {
        if (pauseTasks && Queue.Tasks.Any(t => t.SessionId == id && t.AsPrompt && t.Status == QueueTaskStatus.Queued))
            _chatTasksPaused.TryAdd(id, ChatTaskRules.PauseReason(PromptOutcome.Stopped));
        _stoppedChats.Add(id);
        if (Session(id) is { } vm && vm.Running) vm.Stopping = true;
        ResolveAllGates(id, false);
        if (_runs.TryGetValue(id, out var cts)) cts.Cancel();
        // Stop in a chat stops what it launched in the background too.
        if (_backgroundPools.TryGetValue(id, out var pool)) pool.StopAll();
    }

    public void StopAll()
    {
        StopAllQueues();
        foreach (var id in _runs.Keys.Concat(_backgroundPools.Keys).Distinct().ToList()) StopSession(id);
    }

    /// <summary>The app is closing: cancel every run and background agent, but keep each running task
    /// list's "resume on launch" so it picks back up next time.</summary>
    public void Shutdown()
    {
        _shuttingDown = true;
        foreach (var id in _runs.Keys.Concat(_backgroundPools.Keys).Distinct().ToList()) StopSession(id);
        // Claude Code turns run in their own process: interrupted, a moment to end, then killed — none outlives Vision.
        ClaudeCodeRunner.StopAll();
    }

    private bool _shuttingDown;

    // MARK: - Context window

    /// <summary>The active model's context budget, in tokens: a user override in the provider config
    /// wins, then a value learned by probing the server, then the well-known model tables, then a
    /// conservative default. Read-only — the probe lives in <see cref="EnsureContextProbe"/>.</summary>
    public int ContextLimit() => ContextLimit(Config.ActiveProvider);

    /// <summary>Where the primary's window figure came from (a chat's: <see cref="ContextSource(SessionVM?)"/>).</summary>
    public string ContextSource() => ContextSource((SessionVM?)null);

    /// <summary>The model id actually in use for the active route (follows a swap).</summary>
    public string? ActiveModelId => Config.ActiveProvider is { } p ? Effective(p).Model : null;

    /// <summary>The profile with the model the server actually serves.</summary>
    public ProviderProfile Effective(ProviderProfile provider)
    {
        if (_routeInfo.TryGetValue(provider.RouteId, out var info) && info.ServedModel.Length > 0)
            return provider.DeepCopy() with { Model = info.ServedModel, ApiKey = provider.ApiKey };
        return provider;
    }

    /// <summary>Ask the server what it serves and how big its window is, then return the profile to
    /// use. Cached for a few seconds so /goal rounds don't re-ask; a failed probe keeps what we knew.</summary>
    public async Task<ProviderProfile?> ResolveRouteAsync(bool force = false)
    {
        if (Config.ActiveProvider is not { } provider) return null;
        await ProbeAsync(provider, force);
        return Config.ActiveProvider is { } current ? Effective(current) : null;
    }

    /// <summary>Fire the server probe for the selected chat's route so ContextLimit fills in a learned value.
    /// Safe to call repeatedly: one in flight at a time, and a recent answer is reused.</summary>
    public void EnsureContextProbe() => EnsureContextProbe(Selected);

    /// <summary>Forget cached probes (Settings changed the route, or the user asked).</summary>
    public void ResetRouteCache()
    {
        _routeInfo.Clear();
        _probedContext.Clear();
        _engines.Clear();
        _engineKeys.Clear();
        ResetWorkerProbes();
        RefreshFleet();
        ContextInfoChanged?.Invoke();
        EnsureContextProbe();
    }

    /// <summary>How much of the context a session is using right now, in tokens: the figure recorded
    /// at the end of the last turn (server-reported prompt tokens, or an estimate), plus the draft.</summary>
    public int ContextUsed(string sessionId, string draft)
    {
        int used;
        if (Session(sessionId)?.ContextUsed is { } recorded)
        {
            used = recorded;
        }
        else
        {
            var prompt = _systemPrompts.GetValueOrDefault(sessionId) ?? _basePrompt;
            used = TokenEstimate.Request(prompt, _transcripts.GetValueOrDefault(sessionId) ?? []);
        }
        var trimmed = draft.Trim();
        if (trimmed.Length > 0) used += Math.Max(1, trimmed.Length / 4);
        return used;
    }

    // MARK: - Compaction
    //
    // When the model transcript grows toward the context window, the older messages are replaced by
    // a summary the model writes, and the recent tail is kept verbatim. The engine calls this on
    // every model call (in-loop), so it also fires mid-run when tool output accumulates, and again
    // on a server overflow as a second line of defence.

    public async Task<IReadOnlyList<LlmMessage>> CompactTranscriptAsync(string sessionId, int used,
                                                                        IReadOnlyList<LlmMessage> messages,
                                                                        CancellationToken ct)
    {
        var limit = _engineKeys.TryGetValue(sessionId, out var key) ? key.Window : ContextLimit(Session(sessionId));
        if (Compaction.MakePlan(used, limit, messages) is not { } plan) return messages;
        var vm = Session(sessionId);
        if (vm is not null) vm.Activity = "Compacting conversation to fit the context window…";
        try
        {
            var profile = await ResolveRouteAsync(vm);
            if (profile is null) return messages;
            var summary = await Compaction.SummarizeAsync(MakeClient(profile, sessionId), plan, model: profile.Model, cancellationToken: ct);
            // No summary (no client, or it failed): keep the transcript as-is; the server may still
            // accept it, and an overflow is surfaced rather than silently dropped.
            if (summary is null) return messages;
            var newMessages = new List<LlmMessage> { LlmMessage.SystemText(Compaction.SummaryHeader + summary) };
            newMessages.AddRange(plan.ToKeep);
            ApplyCompaction(sessionId, plan, summary, newMessages);
            return newMessages;
        }
        finally
        {
            if (vm is not null) vm.Activity = null;
        }
    }

    /// <summary>Rewrite the session's display + persisted log so the summarized part is marked by a
    /// compaction divider, and point the model transcript at the new (shorter) list.</summary>
    private void ApplyCompaction(string sessionId, CompactionPlan plan, string summary, List<LlmMessage> newMessages)
    {
        if (Session(sessionId) is not { } vm) return;
        _transcripts[sessionId] = newMessages;
        // What was recalled may have been summarised away: it may be recalled again. So may what was read ("that text
        // is still above" must not be said of text that is now only in the summary).
        if (_recalls.TryGetValue(sessionId, out var recalled)) recalled.Reset();
        if (_caches.TryGetValue(sessionId, out var cache)) cache.NoteCompaction();
        // Reset the gauge: the model now carries a small context.
        var prompt = _systemPrompts.GetValueOrDefault(sessionId) ?? _basePrompt;
        vm.ContextUsed = TokenEstimate.Request(prompt, newMessages);

        // The transcript's user-message count maps 1:1 onto the display's user entries, so the cut is
        // "the Nth+1 user entry", where N is how many user messages were summarized. Counting (instead
        // of matching text) keeps duplicates like repeated "hi" from cutting at the wrong spot.
        var summarizedUsers = plan.ToSummarize.Count(m => m.Role == Vision.Core.MessageRole.User);
        var seenUsers = 0;
        int? cut = null;
        for (var i = 0; i < vm.Entries.Count; i++)
        {
            if (vm.Entries[i] is not MessageEntryVM { Role: MessageRole.User }) continue;
            if (seenUsers == summarizedUsers)
            {
                cut = i;
                break;
            }
            seenUsers++;
        }
        // Earlier messages stay visible above the divider (replay starts from the divider, so they
        // are not sent to the model again). A cut inside one long turn goes at the end.
        vm.Entries.Insert(cut ?? vm.Entries.Count, new CompactionEntryVM(plan.ToSummarize.Count, summary));
        // Re-sync the persisted log so a restart reloads the compacted conversation.
        Log.Resync(sessionId, LogRows(vm.Entries, sessionId));
        vm.NotifyContentChanged();
    }

    /// <summary>Persisted log rows for the current display entries — the single place that turns a
    /// (possibly compacted) session into storage.</summary>
    public static List<LogItemRow> LogRows(IEnumerable<ChatEntryVM> entries, string sessionId)
    {
        var output = new List<LogItemRow>();
        var seq = 0;
        foreach (var entry in entries)
        {
            var row = entry switch
            {
                MessageEntryVM m => new LogItemRow(sessionId, seq, m.Role switch
                {
                    MessageRole.User => "user",
                    MessageRole.Assistant => "assistant",
                    MessageRole.Notice => "notice",
                    _ => "error",
                }, m.Text, null, m.Role == MessageRole.Assistant ? m.Shield : null, null, m.Role == MessageRole.Error, entry.At)
                {
                    Attachments = m.Attachments.Count > 0 ? m.Attachments : null,
                },
                ToolEntryVM t => new LogItemRow(sessionId, seq, "tool", t.Summary, t.Name, t.Preview, t.Output, t.IsOk == false, entry.At),
                // The removed count rides in ArgSummary so a reloaded divider still says how much
                // was folded away.
                CompactionEntryVM c => new LogItemRow(sessionId, seq, "compaction", c.Summary, "compaction", c.Removed.ToString(), null, false, entry.At),
                _ => new LogItemRow(sessionId, seq, "todos", null, null, null, null, false, entry.At),
            };
            output.Add(row);
            seq++;
        }
        return output;
    }

    public static string Describe(Exception error) => error switch
    {
        LlmException llm => llm.Message,
        HttpRequestException http => $"Network error: {http.Message}",
        _ => error.Message,
    };
}
