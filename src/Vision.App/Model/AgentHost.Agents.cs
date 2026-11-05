using System.IO;
using Vision.Core;

namespace Vision.App.Model;

// MARK: - Subagents across servers, memory, caching
//
// Everything the host adds around a chat's engine beyond the model loop itself:
//  * the fleet — every chat's own model is the primary for its subagents, and the routes ticked "Subagent" in
//    Settings › AI Models (any kind: a second Spark, Bedrock, a plan, Guardian's models, Claude Code) are the workers
//    they may hand side tasks to — each chat its own pool of them (SubagentPool), never its own machine
//  * a roster per chat, so the plan panel can show who is working on what, and where
//  * long-term memory — notes searched for each message, never loaded wholesale
//  * a result cache per chat, so repeated reads and searches don't cost time or context
// Runs on the UI thread like the rest of AgentHost; engines call back through the dispatcher.

public sealed partial class AgentHost
{
    /// <summary>Long-term memory: notes searched for each message (%APPDATA%\Vision\memory).</summary>
    public MemoryStore Memory { get; private set; } = null!;

    /// <summary>The model servers subagents run on: the routes enlisted for subagents (shared by every chat; each chat's own
    /// model joins as its primary when it asks). Reconfigured whenever the routes change.</summary>
    public AgentFleet Fleet { get; } = new();

    /// <summary>Guardian's models while signed in, in Guardian's order (set by the Guardian connection).</summary>
    public Func<IReadOnlyList<string>>? GuardianModelIds { get; set; }

    private readonly Dictionary<string, AgentRoster> _rosters = new();
    private readonly Dictionary<string, ToolCache> _caches = new();
    private readonly Dictionary<string, MemoryRecallSession> _recalls = new();
    private readonly Dictionary<string, (ModelInfo Info, DateTimeOffset At)> _workerProbe = new();
    private readonly Dictionary<string, (AgentCatalog Catalog, string Stamp)> _catalogs = new(StringComparer.OrdinalIgnoreCase);

    private void InitAgents(MemoryStore? memory)
    {
        Memory = memory ?? new MemoryStore();
        Config.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppConfig.ActiveRoute) or nameof(AppConfig.SubagentsUsePrimary)) RefreshFleet();
            // The shield's settings are part of every engine: chats pick them up from their next message.
            if (e.PropertyName is nameof(AppConfig.Privacy)) _engines.Clear();
        };
        Config.Providers.CollectionChanged += (_, _) => RefreshFleet();
        Config.GuardianSubagentsChanged += RefreshFleet;
        RefreshFleet();
    }

    // MARK: Fleet

    /// <summary>Rebuild the fleet from the configured routes. Call it after changing which routes are subagent servers (the
    /// route list itself, the Guardian map and the sign-in are watched).</summary>
    public void RefreshFleet()
    {
        var walk = PoolRoutes();
        var handles = SubagentPool.Handles(walk);
        var handleOf = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < walk.Count; i++) handleOf.TryAdd(walk[i].RouteId, handles[i]);
        var servers = new List<FleetServer>();
        foreach (var route in EnlistedRoutes)
        {
            var routeId = route.RouteId;
            var label = RouteLabel(route);
            var facts = walk.FirstOrDefault(f => f.RouteId == routeId) ?? SubagentRouteFacts.From(route, enlisted: true);
            servers.Add(new FleetServer
            {
                Id = routeId,
                Label = label,
                IsPrimary = false,
                // Private as a Sensitive chat counts it: own hardware, Guardian, Bedrock in your account — never Claude Code.
                IsPrivate = SensitivityPolicy.IsPrivate(route),
                InteractiveOnly = route.IsInteractiveOnly,
                MaxParallel = Math.Clamp(route.SubagentParallel ?? 2, 1, 16),
                Handle = handleOf.GetValueOrDefault(routeId),
                Machine = SubagentPool.Machine(facts),
                Kind = facts.Kind.Word(),
                Resolve = ct => ResolveWorkerTargetAsync(routeId, label, ct),
            });
        }
        Fleet.UsePrimaryWhenWorkersBusy = Config.SubagentsUsePrimary;
        Fleet.Configure(servers);
    }

    /// <summary>How a route is named next to a subagent: the route's own name (e.g. "Spark 2").</summary>
    public static string RouteLabel(ProviderProfile route) => route.Name.Length > 0 ? route.Name : route.Model;

    /// <summary>The routes enlisted for subagents, in Settings order: every route that is on and ticked "Subagent" — any kind,
    /// the primary and Claude Code included — then Guardian's models ticked for subagents, while signed in. Which of them a chat
    /// may use is its pool (<see cref="SubagentPoolFor(SessionVM)"/>): never its own machine, one route per machine.</summary>
    public IReadOnlyList<ProviderProfile> EnlistedRoutes
    {
        get
        {
            var list = Config.Providers.Where(p => p.IsEnabled && p.SubagentWorker).ToList();
            if (GuardianAccount?.Invoke() is null) return list;
            foreach (var id in GuardianModelIds?.Invoke() ?? [])
            {
                var routeId = GuardianRoutes.LiveRouteId(id);
                if (!Config.GuardianSubagents.ContainsKey(id) || Config.Providers.Any(p => p.RouteId == routeId)) continue;
                if (Config.UsableRoute(routeId) is { } live) list.Add(live);
            }
            return list;
        }
    }

    /// <summary>Every route a chat's pool is chosen from, in order — Settings' routes, then Guardian's models that have no saved
    /// route (signed in: the account's list; signed out: the ones ticked for subagents, so a chat can say they wait for the
    /// sign-in) — as the facts the pool decides on. Handles are worked out over all of them.</summary>
    private List<SubagentRouteFacts> PoolRoutes()
    {
        var list = Config.Providers.Select(p => FactsFor(p, enlisted: p.IsEnabled && p.SubagentWorker)).ToList();
        var seen = new HashSet<string>(Config.Providers.Select(p => p.RouteId), StringComparer.Ordinal);
        var signedIn = GuardianAccount?.Invoke() is not null;
        IReadOnlyList<string> ids = signedIn
            ? GuardianModelIds?.Invoke() ?? []
            : Config.GuardianSubagents.Keys.Order(StringComparer.Ordinal).ToList();
        foreach (var id in ids)
        {
            var routeId = GuardianRoutes.LiveRouteId(id);
            if (!seen.Add(routeId)) continue;
            var enlisted = Config.GuardianSubagents.ContainsKey(id);
            if (signedIn && Config.UsableRoute(routeId) is { } live) list.Add(FactsFor(live, enlisted));
            else
                list.Add(new SubagentRouteFacts
                {
                    RouteId = routeId, Name = GuardianRoutes.RouteName, Model = id, Kind = SubagentKind.Guardian, Enlisted = enlisted,
                    Parallel = Config.GuardianSubagents.GetValueOrDefault(id) is > 0 and var parallel ? parallel : null,
                });
        }
        return list;
    }

    /// <summary>What the pool knows about a route now: the model its server last reported (sticky), the window it settled on,
    /// whether it takes pictures.</summary>
    private SubagentRouteFacts FactsFor(ProviderProfile route, bool enlisted)
    {
        var claudeCode = ClaudeCodeRoutes.IsClaudeCode(route);
        string? served = null;
        int? probed = null;
        if (!claudeCode)
        {
            lock (_workerProbe)
            {
                if (_workerProbe.TryGetValue(route.RouteId, out var known))
                {
                    served = known.Info.Id;
                    probed = known.Info.ContextWindow;
                }
            }
            if (_routeInfo.TryGetValue(route.RouteId, out var info) && string.IsNullOrEmpty(served)) served = info.ServedModel;
        }
        if (_probedContext.TryGetValue(route.RouteId, out var learned)) probed ??= learned;
        var model = string.IsNullOrEmpty(served) ? route.Model : served;
        int? window = route.ContextWindow is > 0 and var set ? set
            : claudeCode ? probed ?? ClaudeCodeRoutes.LearnedWindow(route.Model) ?? ClaudeCodeRoutes.DefaultContextWindow
            : probed ?? FallbackContextWindow.Limit(model);
        return SubagentRouteFacts.From(route, enlisted, model, window, VisionOn(route));
    }

    /// <summary>This chat's subagent pool now: what its model may hand side tasks to (see <see cref="SubagentPool.Build"/>).</summary>
    public SubagentPool SubagentPoolFor(SessionVM vm, ProviderProfile chatRoute, bool unattended) =>
        // A chat another app started hands side tasks only to its own model: the other servers are where its material mustn't go.
        SubagentPool.Build(vm.PinnedRoute is null ? PoolRoutes() : [], FactsFor(chatRoute, enlisted: false), guardianSignedIn: GuardianAccount?.Invoke() is not null,
            sensitive: vm.Sensitive, shieldOn: ShieldOn, unattended: unattended, overflow: Config.SubagentsUsePrimary);

    /// <summary>This chat's subagent pool now (none while its model can't be resolved).</summary>
    public SubagentPool? SubagentPoolFor(SessionVM vm) => ChatRoute(vm) is { } route ? SubagentPoolFor(vm, route, IsUnattended(vm.Id)) : null;

    private async Task<FleetTarget> ResolveWorkerTargetAsync(string routeId, string label, CancellationToken cancellationToken)
    {
        // The route, its key (a Guardian model's: the sign-in's token now) and the vision setting belong to the UI thread (Settings
        // edits them; keys are cached there): take a copy there, and do the slow part — asking the server what it serves — here
        // on the caller's thread.
        var (profile, vision, guardian) = await _dispatcher.InvokeAsync(() =>
        {
            var route = Config.UsableRoute(routeId);
            var isGuardian = Config.Providers.FirstOrDefault(p => p.RouteId == routeId) is { } stored ? GuardianRoutes.IsGuardian(stored) : GuardianRoutes.IsLiveRouteId(routeId);
            return (route, route is not null && VisionOn(route), isGuardian);
        }).Task.ConfigureAwait(false);
        if (profile is null)
            throw guardian
                ? LlmException.Rejected(401, GuardianRoutes.SignInNeeded, "signed out of Guardian", permanent: true)
                : LlmException.Connection($"{label} is switched off or no longer set up");
        if (ClaudeCodeRoutes.IsClaudeCode(profile)) return await ResolveClaudeCodeWorkerAsync(profile).ConfigureAwait(false);
        var client = MakeClient(profile);
        var model = profile.Model;
        var window = profile.ContextWindow;
        if (profile.Kind != ProviderKind.Bedrock)
        {
            // Ask the server what it serves right now (a Spark may have swapped models); the answer is
            // reused for a minute. A server that names no model at all isn't answering.
            ModelInfo? info = null;
            lock (_workerProbe)
            {
                if (_workerProbe.TryGetValue(routeId, out var known) && DateTimeOffset.Now - known.At < TimeSpan.FromSeconds(60)) info = known.Info;
            }
            if (info is null)
            {
                var fresh = await client.ModelInfoAsync(null, cancellationToken).ConfigureAwait(false);
                if (fresh.Served.Count > 0)
                {
                    lock (_workerProbe) _workerProbe[routeId] = (fresh, DateTimeOffset.Now);
                    info = fresh;
                }
                // A hosted API (Kimi, Z.ai, OpenRouter, Guardian…) may not list models for this key or plan: its configured model is
                // the answer, and the first request says whether it works (a refusal moves the task on). Only a server you run
                // yourself — a Spark that is down or swapping lists nothing — counts as not answering.
                else if (AgentFleet.WorkerNeedsModelList(profile)) throw LlmException.Connection($"{profile.BaseUrl} didn't answer");
            }
            if (!string.IsNullOrEmpty(info?.Id)) model = info.Id;
            window ??= info?.ContextWindow;
        }
        // The worker's own settings — never the main route's temperature, output limit or thinking level.
        return new FleetTarget(client, model, window ?? FallbackContextWindow.Limit(model) ?? FallbackContextWindow.DefaultLimit)
        {
            Vision = vision, Temperature = profile.Temperature, MaxOutputTokens = profile.MaxOutputTokens,
            OwnSettings = true, Thinking = profile.Thinking,
        };
    }

    /// <summary>A Claude Code route as a subagent server: each task one `claude -p` job in the chat's folder, with the CLI this
    /// route runs (found once; none installed is a server fault, so the next server is tried).</summary>
    private async Task<FleetTarget> ResolveClaudeCodeWorkerAsync(ProviderProfile profile)
    {
        var (launch, unsupported, projects) = await _dispatcher.InvokeAsync(async () =>
        {
            var found = await ClaudeCodeLaunchFor(profile);
            var known = _claudeUnsupported.TryGetValue(found.Display, out var flags)
                ? new HashSet<string>(flags, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            return (found, known, ClaudeCodeProjects(profile));
        }).Task.Unwrap().ConfigureAwait(false);
        var wsl = ClaudeCodeRoutes.UsesWsl(profile);
        return new FleetTarget(MakeClient(profile), profile.Model, ClaudeCodeRoutes.LearnedWindow(profile.Model) ?? ClaudeCodeRoutes.DefaultContextWindow)
        {
            Vision = false,
            OwnSettings = true,
            Thinking = profile.Thinking,
            External = new ClaudeCodeSubagent(launch, profile.Model, profile.Thinking)
            {
                Starter = ClaudeCodeStarterForTesting,
                IsTrusted = folder => ClaudeCodeTrust.IsTrusted(folder, projects),
                MapPath = wsl ? ClaudeCodeCli.FromWslPath : null,
                Unsupported = unsupported,
            },
        };
    }

    /// <summary>Forget what was learned about the subagent servers (Settings changed, or the user asked).</summary>
    private void ResetWorkerProbes()
    {
        lock (_workerProbe) _workerProbe.Clear();
    }

    // MARK: Per-chat helpers

    /// <summary>This chat's subagent roster (created on first use); its changes are mirrored into the
    /// session so the plan panel can show them.</summary>
    private AgentRoster RosterFor(string sessionId)
    {
        if (_rosters.TryGetValue(sessionId, out var existing)) return existing;
        var roster = new AgentRoster();
        roster.Changed += run =>
        {
            if (run.Id.Length == 0) return;
            _dispatcher.BeginInvoke(() =>
            {
                if (Session(sessionId) is not { } vm) return;
                var isNew = vm.Agents.All(a => a.Id != run.Id);
                vm.UpsertAgent(run);
                // A subagent that just started works one of the plan's steps: say which, so the plan shows who works it.
                // The agent tool's step argument names it; without one, the best guess from its words (none on a tie).
                if (isNew && run.Assignment is null && PlanAssignment.Step(run.RequestedStep, run.Description, run.Prompt ?? "", vm.Todos) is { } step)
                    roster.SetAssignment(run.Id, step);
            });
        };
        _rosters[sessionId] = roster;
        return roster;
    }

    private ToolCache CacheFor(string sessionId)
    {
        if (_caches.TryGetValue(sessionId, out var existing)) return existing;
        return _caches[sessionId] = new ToolCache();
    }

    private MemoryRecallSession RecallFor(string sessionId)
    {
        if (_recalls.TryGetValue(sessionId, out var existing)) return existing;
        return _recalls[sessionId] = new MemoryRecallSession();
    }

    /// <summary>What the cache has saved in a chat so far ("12 repeated lookups answered from cache…"); empty if nothing yet.</summary>
    public string CacheSummary(string sessionId) => _caches.TryGetValue(sessionId, out var cache) ? cache.Stats.Describe() : "";

    /// <summary>The subagent types this chat offers: built-ins plus agent files from the user's folders and the project's.</summary>
    private AgentCatalog CatalogFor(SessionVM vm)
    {
        var root = vm.WorkspacePath ?? "";
        // A file added or edited while the app is running is picked up at the next turn (the stamp is a few directory listings).
        var stamp = AgentCatalog.Stamp(vm.WorkspacePath);
        if (_catalogs.TryGetValue(root, out var known) && known.Stamp == stamp) return known.Catalog;
        var catalog = AgentCatalog.Load(vm.WorkspacePath);
        _catalogs[root] = (catalog, stamp);
        return catalog;
    }

    /// <summary>Changes whenever an agent file is added, removed or edited in this chat's folders — so the engine (which
    /// carries the agent types, their tools and the prompt that lists them) is rebuilt for the next turn.</summary>
    private string AgentsStampFor(SessionVM vm)
    {
        CatalogFor(vm);
        return _catalogs.TryGetValue(vm.WorkspacePath ?? "", out var known) ? known.Stamp : "";
    }

    /// <summary>Re-read agent files (the user edited one, or opened another project).</summary>
    public void RefreshAgentTypes()
    {
        _catalogs.Clear();
        _engines.Clear();
    }

    /// <summary>Raised the first time a chat makes a plan (a todo list, or a proposed plan), so the window can
    /// show the plan panel by itself. Once per chat: closing the panel is not undone by every later update.</summary>
    public event Action<SessionVM>? PlanAppeared;
    private readonly HashSet<string> _plansAnnounced = new();

    private void AutoOpenPlan(SessionVM vm)
    {
        // A plan made in a chat that isn't open is not announced yet: the window would decline it, and the one chance would
        // be gone by the time the user gets there. It is announced with the first update they are there for.
        if (!Config.PlanPanelAutoOpen || vm.Id != SelectedId) return;
        if (_plansAnnounced.Add(vm.Id)) PlanAppeared?.Invoke(vm);
    }

    private void ForgetChatState(string sessionId)
    {
        _plansAnnounced.Remove(sessionId);
        _rosters.Remove(sessionId);
        _caches.Remove(sessionId);
        _recalls.Remove(sessionId);
        _stoppedChats.Remove(sessionId);
        _failedRuns.Remove(sessionId);
    }

    // MARK: Memory in the prompt

    /// <summary>The memory section of the system prompt (how to use the tools, the few pinned notes), or empty
    /// when memory is off.</summary>
    private string MemoryPromptFor(string? workspace, bool canSave = true) =>
        Config.MemoryEnabled ? MemoryPrompt.Section(Memory, workspace, canSave) : "";

    /// <summary>The notes that bear on <paramref name="message"/>, as text to put in front of it; null when
    /// none do (the usual case). Shows a one-line notice of what was recalled.</summary>
    private string? RecallBlockFor(SessionVM vm, string message)
    {
        if (!Config.MemoryEnabled) return null;
        MemoryRecallResult? recall;
        try
        {
            recall = MemoryRecall.Build(Memory, message, vm.WorkspacePath, RecallFor(vm.Id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null; // memory is a help, never a reason for a turn to fail
        }
        if (recall is null) return null;
        var titles = string.Join(" · ", recall.Items.Select(i => TextUtil.Prefix(i.Title, 40)));
        vm.Note($"🧠 Remembered {recall.Items.Count} note{(recall.Items.Count == 1 ? "" : "s")}: {titles}");
        return recall.Block;
    }

    // MARK: /remember, /memory, /agents

    private void RememberCommand(string text, SessionVM vm)
    {
        var note = text.Trim();
        // "global: …" makes a note for every chat; otherwise it belongs to the folder this chat works in.
        var global = false;
        foreach (var prefix in new[] { "global:", "everywhere:" })
        {
            if (!note.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            global = true;
            note = note[prefix.Length..].Trim();
            break;
        }
        if (note.Length == 0)
        {
            vm.Note("Usage: `/remember <what to remember>` — for example `/remember the staging server is called orion`. " +
                    "It belongs to this chat's project; start with `global:` for a note that applies everywhere. " +
                    "`/memory` shows everything that's remembered.");
            return;
        }
        if (SecretGuard.LooksLikeSecret(note))
        {
            vm.Note("That looks like a key or password, and memory is stored as plain text. Put it in the Credentials Vault (Ctrl+Shift+K) instead.", MessageRole.Error);
            return;
        }
        var project = global ? null : vm.WorkspacePath;
        var saved = Memory.Save(new MemoryDraft { Body = note, Source = "user", Kind = MemoryKinds.Note, Project = project });
        var where = project is null ? "in every chat" : "in this project";
        var text2 = saved.Kind == MemorySaveKind.Created
            ? $"🧠 Remembered {where}: “{TextUtil.Prefix(saved.Item.Title, 80)}”. " +
              (Config.MemoryEnabled ? "It will come up when it's relevant; `/memory` lets you edit or remove it."
                                    : "Memory is switched off right now, so it isn't used until you turn it back on (Session › Remembered Notes).")
            : $"🧠 Updated the note “{TextUtil.Prefix(saved.Item.Title, 80)}”.";
        if (Memory.StorageProblem is { } problem) text2 += "\n⚠️ " + problem + " The note is kept for this session.";
        vm.Note(text2);
    }

    private void MemoryCommand(string? query, SessionVM vm)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            var stats = Memory.Stats();
            vm.Note($"Memory: {stats.Count} note{(stats.Count == 1 ? "" : "s")} ({stats.Pinned} pinned). Opening the memory manager.");
            OpenMemoryManagerRequested?.Invoke();
            return;
        }
        var hits = Memory.Search(query, new MemoryQueryOptions { Project = vm.WorkspacePath, Limit = 8, IncludePinned = true, Browse = true });
        if (hits.Count == 0)
        {
            vm.Note($"Nothing remembered matches “{query}”.");
            return;
        }
        vm.Note($"Remembered notes matching “{query}”:\n" + string.Join("\n", hits.Select(h =>
            $"- **{h.Item.Title}** [{h.Item.Id}] — {TextUtil.Prefix(h.Item.Body.Replace('\n', ' '), 140)}")));
    }

    /// <summary>Raised when a slash command wants the memory manager window.</summary>
    public event Action? OpenMemoryManagerRequested;

    private void AgentsCommand(SessionVM vm)
    {
        var lines = new List<string> { "**Subagent types** (the agent tool's `agent_type`):" };
        foreach (var type in CatalogFor(vm).All)
            lines.Add($"- `{type.Name}` — {TextUtil.Prefix(type.Description, 110)}" + (type.Origin == "built-in" ? "" : $" *({type.Origin})*"));
        lines.Add("");
        lines.AddRange(ServerLines(vm));
        var running = vm.Agents.Where(a => a.IsRunning).ToList();
        lines.Add("");
        lines.Add(running.Count == 0
            ? "No subagents running in this chat."
            : "**Running now:**\n" + string.Join("\n", running.Select(a => $"- {a.Description} ({a.AgentType}{(a.Server is null ? "" : " · " + a.Server)}) — {a.Activity}")));
        vm.Note(string.Join("\n", lines));
    }

    /// <summary>/agents: this chat's model, the subagent servers it may hand tasks to (with how busy each is), and the enlisted
    /// ones it can't use, with why.</summary>
    private List<string> ServerLines(SessionVM vm)
    {
        if (ChatRoute(vm) is not { } route) return ["**This chat's model:** none set up yet."];
        if (ClaudeCodeRoutes.IsClaudeCode(route))
            return ["This chat runs on Claude Code, which uses its own subagents. Vision's subagent servers serve chats on other models."];
        var pool = SubagentPoolFor(vm, route, IsUnattended(vm.Id));
        var lines = new List<string> { $"**This chat's model (primary):** {RouteLabel(route)} — {Effective(route).Model}" };
        var snapshot = Fleet.Snapshot();
        if (!pool.HasWorkers)
        {
            lines.Add("**Its subagent servers:** none — subagents run on this chat's own model. Tick “Subagent” on other models in Settings › AI Models " +
                      "(a second DGX Spark, Amazon Bedrock, Guardian, Claude Code) and this chat's model can hand side tasks to them.");
        }
        else
        {
            lines.Add("**Its subagent servers:**");
            foreach (var entry in pool.Workers)
            {
                var status = snapshot.FirstOrDefault(s => s.Id == entry.RouteId);
                var load = status is null ? $"0/{entry.Parallel} busy, 0 done"
                    : $"{status.InFlight}/{status.MaxParallel} busy, {status.Completed} done{(status.Failed > 0 ? $", {status.Failed} failed" : "")}"
                      + (status.Healthy ? "" : " — not answering, skipped for a minute");
                lines.Add($"- `{entry.Id}` {entry.Provider} · {entry.Model} · {(entry.IsPrivate ? "private" : "not private")} · {entry.Tier.Word()} — {load}");
            }
        }
        if (pool.Dropped.Count > 0)
            lines.Add("Not for this chat: " + string.Join(", ", pool.Dropped.Select(d => $"{d.Label} ({d.Reason})")));
        return lines;
    }
}
