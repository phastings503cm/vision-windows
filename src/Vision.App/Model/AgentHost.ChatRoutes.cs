using Vision.Core;

namespace Vision.App.Model;

// MARK: - Each chat's own model
//
// Two projects open, a prompt in each: the model picked in one chat must not become the other's. A chat carries its own
// route and thinking level (stored with the conversation); a new chat starts with what its project's chats last chose (else
// the primary), and every path that works for a chat — its turns, retries, /goal, its task list, its subagents' fallback
// server, compaction, carrying on after background agents — uses that chat's route. The primary in Settings › AI Models is
// the default for new chats and projects (it leads nothing: each chat's own model leads that chat's subagents); choosing a
// model in a chat changes only that chat (and what its project's next chats start with). Vision for macOS does the same
// (ChatRoutes.swift).

public sealed partial class AgentHost
{
    /// <summary>The route a chat talks to, with its key: its own while that is configured and on (one of Guardian's models while
    /// signed in counts), else the primary. None for a chat on a Guardian model while Guardian is signed out: it waits for the
    /// sign-in rather than quietly moving to another model. None too for a chat another app started on a route that has gone:
    /// what it holds was sent to that model and nowhere else.</summary>
    public ProviderProfile? ChatRoute(SessionVM? vm) =>
        GuardianSignInNeeded(vm) ? null
        : vm?.PinnedRoute is { } pinned ? Config.UsableRoute(pinned)
        : Config.UsableRoute(vm?.RouteId) ?? Config.ActiveProvider;

    /// <summary>The chat is on one of Guardian's models (or a saved Guardian route) and Guardian is signed out: it says "Sign in to
    /// Guardian to use this model" instead of answering.</summary>
    public bool GuardianSignInNeeded(SessionVM? vm) => vm?.RouteId is { } id && IsGuardianChoice(id) && GuardianAccount?.Invoke() is null;

    /// <summary>A route id that means Guardian's proxy: a saved Guardian route (switched on), or a Guardian model id no saved
    /// route has.</summary>
    public bool IsGuardianChoice(string routeId) =>
        Config.Providers.FirstOrDefault(p => p.RouteId == routeId) is { } stored
            ? stored.IsEnabled && GuardianRoutes.IsGuardian(stored)
            : GuardianRoutes.IsLiveRouteId(routeId);

    /// <summary>The routes a chat can resolve changed without Settings (Guardian signed in or out, its model list arrived): the
    /// model buttons and gauges look again.</summary>
    public void NotifyRoutesChanged() => ContextInfoChanged?.Invoke();

    /// <summary>Raised when a message is sent to a chat on a Guardian model while signed out (the app offers the sign-in).</summary>
    public event Action? GuardianSignInRequired;

    /// <summary>Refuse a turn on a Guardian model while signed out, in those words (and offer the sign-in).</summary>
    private void RequireGuardianSignIn(SessionVM vm)
    {
        if (!GuardianSignInNeeded(vm)) return;
        GuardianSignInRequired?.Invoke();
        throw LlmException.Rejected(401, GuardianRoutes.SignInNeeded, "signed out of Guardian", permanent: true);
    }

    public ProviderProfile? ChatRoute(string sessionId) => ChatRoute(Session(sessionId));

    /// <summary>The configured route a chat shows in its model menu (its own, else the primary).</summary>
    public string? ChatRouteId(SessionVM? vm) => ChatRoute(vm)?.RouteId;

    /// <summary>The model the chat's route serves now (follows a Spark swap).</summary>
    public string? ModelIdFor(SessionVM? vm) => ChatRoute(vm) is { } route ? Effective(route).Model : null;

    /// <summary>What the chat's model takes: which thinking levels its picker offers.</summary>
    public ModelRequestRules? RequestRules(SessionVM? vm)
    {
        if (ChatRoute(vm) is not { } route) return null;
        var live = Effective(route);
        return ModelRequestRules.For(live, live.Model);
    }

    /// <summary>The thinking level a chat asks for: its own, else its route's default.</summary>
    public ThinkingLevel? ThinkingFor(SessionVM vm) => vm.Thinking ?? ChatRoute(vm)?.Thinking;

    /// <summary>A new chat starts with its project's last choice, else the primary.</summary>
    private void AssignInitialChoice(SessionVM vm)
    {
        var inherited = Config.LastChoice(vm.WorkspacePath);
        vm.RouteId = Config.UsableRoute(inherited?.Route)?.RouteId ?? Config.ActiveRoute;
        vm.Thinking = ThinkingLevels.FromRaw(inherited?.Thinking);
        Log.SetChoice(vm.Id, vm.RouteId, vm.Thinking?.RawValue());
    }

    /// <summary>An older chat that never chose, or whose route is gone or switched off: it takes the primary of the moment it
    /// sends, and keeps it (saying so when it had another).</summary>
    private void PinRouteIfUnset(SessionVM vm)
    {
        // A chat on a Guardian model keeps it while signed out: signing in again brings it back.
        if (GuardianSignInNeeded(vm)) return;
        // A chat another app started stays where it was sent (RequirePinnedRoute stops it if that has gone).
        if (vm.PinnedRoute is not null) return;
        if (vm.RouteId is not null && Config.UsableRoute(vm.RouteId) is not null) return;
        if (Config.ActiveRoute is not { } primary || vm.RouteId == primary) return;
        var previous = vm.RouteId;
        vm.RouteId = primary;
        Log.SetChoice(vm.Id, vm.RouteId, vm.Thinking?.RawValue());
        if (previous is not null && Config.ActiveProvider is { } route)
        {
            var text = $"This chat's model is no longer set up (or is switched off), so it now uses {RouteLabel(route)} — {route.Model}.";
            vm.Note(text);
            Log.RecordItem(vm.Id, "notice", text);
        }
    }

    /// <summary>Choose a chat's model: that chat only. Its project's next chats start with it too.</summary>
    public void SetChatRoute(SessionVM vm, string routeId)
    {
        if (Config.UsableRoute(routeId) is null) return;
        // The user chose: a chat another app started is an ordinary chat from here on.
        Unpin(vm);
        if (vm.RouteId != routeId)
        {
            vm.RouteId = routeId;
            // A level the new model doesn't offer is shown (and sent) as its nearest one; the chat keeps its own choice.
            Log.SetChoice(vm.Id, routeId, vm.Thinking?.RawValue());
            EnsureContextProbe(vm);
            ContextInfoChanged?.Invoke();
        }
        Config.RememberChoice(new ChatChoice(routeId, vm.Thinking?.RawValue()), vm.WorkspacePath);
    }

    /// <summary>Set a chat's thinking level (null = the route's default). Remembered for its project's next chats.</summary>
    public void SetChatThinking(SessionVM vm, ThinkingLevel? level)
    {
        vm.Thinking = level;
        Log.SetChoice(vm.Id, vm.RouteId, level?.RawValue());
        Config.RememberChoice(new ChatChoice(vm.RouteId ?? Config.ActiveRoute, level?.RawValue()), vm.WorkspacePath);
    }

    /// <summary>A route was saved under a new id: the chats on it follow it.</summary>
    private void OnRouteRenamed(string oldRouteId, string newRouteId)
    {
        foreach (var vm in Sessions.Where(s => s.RouteId == oldRouteId))
        {
            vm.RouteId = newRouteId;
            Log.SetChoice(vm.Id, newRouteId, vm.Thinking?.RawValue());
        }

        foreach (var vm in Sessions.Where(s => s.PinnedRoute == oldRouteId))
        {
            vm.PinnedRoute = newRouteId;
            Log.SetPinnedRoute(vm.Id, newRouteId);
        }
    }

    /// <summary>The chat's route when its plan's terms allow only interactive use (the Qwen Coding Plan).</summary>
    public ProviderProfile? InteractiveOnlyRoute(SessionVM? vm) => ChatRoute(vm) is { IsInteractiveOnly: true } route ? route : null;

    public ProviderProfile? InteractiveOnlyRoute(string sessionId) => InteractiveOnlyRoute(Session(sessionId));

    /// <summary>The chat's route is a Spark Swapper box that is switching models right now.</summary>
    private bool ChatServerSwitching(SessionVM vm) =>
        IsRouteSwitching is { } switching ? switching(Config.Providers.FirstOrDefault(p => p.RouteId == ChatRouteId(vm))) : IsServerSwitching?.Invoke() == true;

    /// <summary>True while the given route's model server is switching models (Spark swapper).</summary>
    public Func<ProviderProfile?, bool>? IsRouteSwitching { get; set; }

    /// <summary>Ask the chat's server what it serves and how big its window is, then return the profile to use.</summary>
    public async Task<ProviderProfile?> ResolveRouteAsync(SessionVM? vm, bool force = false)
    {
        if (ChatRoute(vm) is not { } provider) return null;
        await ProbeAsync(provider, force);
        return ChatRoute(vm) is { } current ? Effective(current) : null;
    }

    /// <summary>The context window of a route: the user's override, the server's answer, the model tables, or a default.</summary>
    public int ContextLimit(ProviderProfile? provider)
    {
        if (provider is null) return FallbackContextWindow.DefaultLimit;
        if (provider.ContextWindow is > 0 and var overridden) return overridden;
        if (_probedContext.TryGetValue(provider.RouteId, out var probed)) return probed;
        return FallbackContextWindow.Limit(Effective(provider).Model) ?? FallbackContextWindow.DefaultLimit;
    }

    /// <summary>The context window of the chat's route.</summary>
    public int ContextLimit(SessionVM? vm) => ContextLimit(ChatRoute(vm));

    /// <summary>Where a chat's window figure came from, for /context and the gauge tooltip.</summary>
    public string ContextSource(SessionVM? vm)
    {
        if (ChatRoute(vm) is not { } provider) return "default";
        if (provider.ContextWindow is > 0) return "set manually in Settings";
        if (_probedContext.ContainsKey(provider.RouteId)) return "detected from the server";
        if (FallbackContextWindow.Limit(Effective(provider).Model) is not null) return "from the built-in model table (server did not report one)";
        return "a conservative default (server did not report one — set it in Settings)";
    }

    /// <summary>Fire the server probe for a chat's route so its gauge fills in a learned value.</summary>
    public void EnsureContextProbe(SessionVM? vm)
    {
        if (ChatRoute(vm) is not { } provider) return;
        var routeId = provider.RouteId;
        if (_probingContext.Contains(routeId)) return;
        if (_routeInfo.TryGetValue(routeId, out var info) && DateTimeOffset.Now - info.At < TimeSpan.FromSeconds(60)) return;
        _probingContext.Add(routeId);
        _ = Probe();

        async Task Probe()
        {
            try
            {
                await ProbeAsync(provider, force: true);
            }
            catch (Exception)
            {
                // The gauge keeps its fallback figure.
            }
            finally
            {
                _probingContext.Remove(routeId);
            }
        }
    }

    /// <summary>Ask a route's server what it serves now (cached for a few seconds so /goal rounds don't re-ask; a failed probe
    /// keeps what was known).</summary>
    private async Task ProbeAsync(ProviderProfile provider, bool force)
    {
        var routeId = provider.RouteId;
        var fresh = _routeInfo.TryGetValue(routeId, out var known) && DateTimeOffset.Now - known.At < TimeSpan.FromSeconds(15);
        if (!force && fresh) return;
        var info = await MakeClient(provider).ModelInfoAsync();
        if (info.Served.Count == 0) return;
        _routeInfo[routeId] = new RouteInfo(info.Id, info.Served, info.ContextWindow, DateTimeOffset.Now);
        if (info.ContextWindow is > 0 and var limit && _probedContext.GetValueOrDefault(routeId) != limit) _probedContext[routeId] = limit;
        ContextInfoChanged?.Invoke();
    }

    /// <summary>The chat's own route as the fleet's "primary" for its subagents ("primary" in its agent tool): when no worker is
    /// free (or there are none) a subagent falls back to the model its own chat uses, never another chat's — and the chat's model
    /// may pick it by name.</summary>
    private FleetServer FleetHome(SessionVM vm, ProviderProfile profile)
    {
        var sessionId = vm.Id;
        var facts = FactsFor(profile, enlisted: false);
        return new FleetServer
        {
            Id = profile.RouteId,
            Label = RouteLabel(profile),
            IsPrimary = true,
            IsHome = true,
            Handle = SubagentPool.PrimaryId,
            // Private as a Sensitive chat counts it (own hardware, Guardian, Bedrock in your account).
            IsPrivate = SensitivityPolicy.IsPrivate(profile),
            InteractiveOnly = profile.IsInteractiveOnly,
            MaxParallel = Math.Clamp(profile.SubagentParallel ?? 4, 1, 16),
            Machine = SubagentPool.Machine(facts),
            Kind = facts.Kind.Word(),
            Resolve = _ => _dispatcher.InvokeAsync(() => ResolveHomeTargetAsync(sessionId)).Task.Unwrap(),
        };
    }

    /// <summary>The fleet's stand-in primary for a chat's subagents: that chat's own route, as its turns resolve it.</summary>
    private async Task<FleetTarget> ResolveHomeTargetAsync(string sessionId)
    {
        var vm = Session(sessionId);
        var profile = await ResolveRouteAsync(vm).ConfigureAwait(true) ?? throw LlmException.NoModel();
        return new FleetTarget(MakeClient(profile, sessionId), profile.Model, ContextLimit(profile))
        {
            Vision = VisionOn(profile), Temperature = profile.Temperature, MaxOutputTokens = profile.MaxOutputTokens,
        };
    }

    private void InitChatRoutes()
    {
        Config.RouteRenamed += OnRouteRenamed;
    }
}
