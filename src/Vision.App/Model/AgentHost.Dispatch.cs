using System.IO;
using Vision.Core;

namespace Vision.App.Model;

// MARK: - Chats another app starts
//
// Another app on this PC can start a chat through the dispatch folder (DispatchInbox): in the background, on the model it chose,
// and never with full access, because it's working from text nobody here has read. Every chat here already keeps its own route;
// what a request's model adds is that the chat stays there. It has no backups (MakeChatClient) and no other subagent servers
// (SubagentPoolFor), and if the route goes it stops rather than moves (ChatRoute, RequirePinnedRoute). Choosing a model in the chat
// makes it an ordinary chat. Vision for macOS does the same (AppTransport+Dispatch.swift).

public sealed partial class AgentHost
{
    /// <summary>What a pinned chat says when its model has gone.</summary>
    public const string PinnedRouteGone = "The model this chat was sent to isn't available any more. Pick a model to carry on.";

    /// <summary>What another app can pick from right now: the routes that are on, and Guardian's models while signed in.</summary>
    public DispatchCatalog BuildDispatchCatalog() => DispatchCatalog.From(DispatchRoutes(), Config.ActiveRoute, Config.LiveRecentProjects);

    private List<ProviderProfile> DispatchRoutes()
    {
        var routes = Config.Providers.Where(p => p.IsEnabled).ToList();
        if (GuardianAccount?.Invoke() is null) return routes;
        foreach (var id in GuardianModelIds?.Invoke() ?? [])
        {
            var routeId = GuardianRoutes.LiveRouteId(id);
            if (routes.All(r => r.RouteId != routeId) && Config.UsableRoute(routeId) is { } live) routes.Add(live);
        }

        return routes;
    }

    /// <summary>Starts a chat for a request, in the background: it doesn't take over the chat on screen, or change what the primary
    /// model is or what the project's next chats start with.</summary>
    public DispatchResult StartDispatch(DispatchRequest request)
    {
        DispatchResult Refuse(string why) => new(request.Id, false, why);

        ProviderProfile? route = null;
        if (request.Model is { } routeId)
        {
            route = Config.UsableRoute(routeId);
            if (route is null) return Refuse($"That model isn't available in {DispatchInbox.AppName} any more.");
            if (route.IsInteractiveOnly) return Refuse($"{route.Name} is for interactive use only under its plan's terms, so another app can't start a chat on it.");
        }
        else if (Config.ActiveProvider is null)
        {
            return Refuse($"{DispatchInbox.AppName} has no model set up yet.");
        }

        // What's sent while the model server is switching isn't run.
        var server = Config.Providers.FirstOrDefault(p => p.RouteId == (route?.RouteId ?? Config.ActiveRoute));
        if (IsRouteSwitching is { } switching ? switching(server) : IsServerSwitching?.Invoke() == true)
            return Refuse($"{RouteLabel(route ?? Config.ActiveProvider!)} is switching models right now. Send this again once it's ready.");

        string? folder = null;
        if (request.Folder is { } path)
        {
            if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) return Refuse($"The folder {path} doesn't exist.");
            folder = path;
        }

        // The text comes from outside the app, so it doesn't get to run unasked with full access.
        var preset = Config.AsPreset == PermissionPreset.FullAccess ? PermissionPreset.WorkspaceWrite : Config.AsPreset;
        var vm = NewSession(folder, preset, select: false);
        var title = TextUtil.Prefix(request.Title.Trim(), 120);
        if (title.Length > 0) RenameSession(vm.Id, title);
        if (route is not null)
        {
            vm.RouteId = route.RouteId;
            vm.PinnedRoute = route.RouteId;
            vm.Thinking = null;
            Log.SetChoice(vm.Id, vm.RouteId, null);
            Log.SetPinnedRoute(vm.Id, vm.PinnedRoute);
        }
        else
        {
            vm.RouteId = Config.ActiveRoute;
            Log.SetChoice(vm.Id, vm.RouteId, vm.Thinking?.RawValue());
        }

        var label = route is null ? null : RouteLabel(route) + " · " + route.Model;
        var note = $"Sent from {request.Source}" + (label is null ? "." : $". This chat runs on {label}; picking another model in the list moves it to that one.");
        vm.Note(note);
        Log.RecordItem(vm.Id, "notice", note);
        Send(request.Prompt, vm.Id);
        return new DispatchResult(request.Id, true, $"Started a chat in {DispatchInbox.AppName}" + (label is null ? "" : " on " + label) + ".");
    }

    /// <summary>Publishes what can be picked, starts what's waiting, and says what became of each. The files are handled off this
    /// thread, because a request can be megabytes and a virus scanner can hold any of them. This runs every 2 seconds.</summary>
    public async Task PumpDispatchAsync(DispatchInbox inbox)
    {
        var catalog = DispatchCatalog.From(DispatchRoutes(), Config.ActiveRoute, []);
        var recent = Config.RecentProjects.ToList();
        var taken = await Task.Run(() =>
        {
            try
            {
                // Looking at a folder on a network drive that has gone can take seconds.
                inbox.Publish(catalog with { Folders = recent.Where(Directory.Exists).ToList() });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The last one stands until the next round.
            }

            return inbox.TakeWaiting();
        });
        foreach (var item in taken)
        {
            DispatchResult result;
            try
            {
                result = item switch
                {
                    DispatchTaken.Request request => StartDispatch(request.Value),
                    DispatchTaken.Rejected rejected => new DispatchResult(rejected.Id, false, rejected.Reason),
                    _ => throw new InvalidOperationException(),
                };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Nobody's waiting on this thread, but the sender is: it hears that this one didn't start.
                result = new DispatchResult(item is DispatchTaken.Request r ? r.Value.Id : "", false, $"{DispatchInbox.AppName} couldn't start this: {ex.Message}");
            }

            try
            {
                await Task.Run(() => inbox.Record(result));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The sender says it doesn't know how this went, which is the truth.
            }
        }
    }

    /// <summary>Back to following the chat's own choice like any other chat, because the user picked a model.</summary>
    public void Unpin(SessionVM vm)
    {
        if (vm.PinnedRoute is null) return;
        vm.PinnedRoute = null;
        Log.SetPinnedRoute(vm.Id, null);
    }

    /// <summary>Refuse a turn on a pinned route that has gone, rather than quietly moving the chat to a model it wasn't sent to.</summary>
    private void RequirePinnedRoute(SessionVM vm)
    {
        if (vm.PinnedRoute is not { } pinned || Config.UsableRoute(pinned) is not null) return;
        throw LlmException.Rejected(404, PinnedRouteGone, "the model this chat was sent to is gone", permanent: true);
    }
}
