using System.Windows.Threading;
using Vision.Core;

namespace Vision.App.Model;

// MARK: - Which model a chat talks to, and what it may see
//
// The roster around the primary model: backups that take a turn over when the primary stops answering, the
// sensitive-data shield on every route that isn't private (its report shows as a chip on the reply), the chat's pool of
// subagent servers (what its agent tool lists), and the plans whose terms allow only interactive use.

public sealed partial class AgentHost
{
    /// <summary>Said once per chat when a picture or a file that isn't plain text goes to a model that isn't private.</summary>
    public const string UncheckedNotice = ShieldReport.UncheckedNotice;

    /// <summary>Chat → what the shield masked in the request whose reply is about to be shown.</summary>
    private readonly Dictionary<string, ShieldReport> _pendingShield = new();
    /// <summary>Chats whose current turn already said it ran on a backup.</summary>
    private readonly HashSet<string> _failoverNoted = new();
    private IReadOnlyList<PluginManifest>? _userPlugins;

    /// <summary>The user's own plugins (a chat without a project gets no project ones).</summary>
    private IReadOnlyList<PluginManifest> UserPlugins() => _userPlugins ??= PluginLoader.Load(null).Plugins;

    /// <summary>The shield's settings now (null when it is switched off), reporting to <paramref name="sessionId"/>'s chat.</summary>
    private ShieldOptions? ShieldFor(string? sessionId)
    {
        var privacy = Config.Privacy;
        IReadOnlyList<string> vault;
        try
        {
            vault = Vault.ValuesForRedaction().Select(v => v.Value).ToList();
        }
        catch (Exception)
        {
            vault = [];
        }
        // The vault's secrets are masked even with the switch off (as in Vision for macOS); with it off and an empty
        // vault there is nothing to do.
        if (!privacy.Mask && vault.Count == 0) return null;
        return new ShieldOptions
        {
            Categories = privacy.Mask ? privacy.Categories : SensitiveCategories.None,
            CustomTerms = privacy.Mask ? privacy.TermLines : [],
            VaultSecrets = vault,
            Report = sessionId is null ? null : report => _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => OnShieldReport(sessionId, report)),
        };
    }

    /// <summary>The client a chat's own turns use: the primary, and — when there are backups — the backups behind it,
    /// each with its own shield. Subagents never get backups (the fleet moves their tasks itself), and unattended work
    /// never falls back to a plan that allows only interactive use.</summary>
    private IProviderClient MakeChatClient(ProviderProfile profile, string sessionId)
    {
        var primary = MakeClient(profile, sessionId);
        // A chat another app started stays on its model: a backup could be somewhere its material isn't allowed to go.
        if (Session(sessionId)?.PinnedRoute is not null) return primary;
        var backups = BackupRoutes(profile.RouteId).Where(b => !b.IsInteractiveOnly || !IsUnattended(sessionId)).ToList();
        // A Sensitive chat falls over only to models it may use.
        if (Session(sessionId) is { Sensitive: true }) backups = SensitivityPolicy.AdmittedBackups(backups, ShieldOn).ToList();
        if (backups.Count == 0) return primary;
        return new FailoverClient(primary, backups.Select(b => MakeClient(b, sessionId)).ToList(),
            notice => _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => OnFailover(sessionId, notice)));
    }

    /// <summary>The enabled backups for a chat on <paramref name="routeId"/> (that route itself left out), in list order, with
    /// their keys.</summary>
    private IReadOnlyList<ProviderProfile> BackupRoutes(string? routeId) =>
        RouteRoster.Backups(Config.Providers, routeId).Where(route => !ClaudeCodeRoutes.IsClaudeCode(route)).Select(route =>
        {
            var copy = route.DeepCopy();
            var key = Config.ApiKey(route);
            copy.ApiKey = key.Length == 0 ? null : key;
            return copy;
        }).ToList();

    private void OnShieldReport(string sessionId, ShieldReport report)
    {
        if (Session(sessionId) is not { } vm) return;
        if (report.Unchecked > 0 && !vm.Entries.OfType<MessageEntryVM>().Any(m => m.Text == UncheckedNotice))
        {
            vm.Note(UncheckedNotice);
            Log.RecordItem(sessionId, "notice", UncheckedNotice);
        }
        if (report.Total > 0) _pendingShield[sessionId] = report;
        else _pendingShield.Remove(sessionId);
    }

    private void OnFailover(string sessionId, FailoverNotice notice)
    {
        if (Session(sessionId) is not { } vm || !_failoverNoted.Add(sessionId)) return;
        vm.Note(notice.Text);
        Log.RecordItem(sessionId, "notice", notice.Text);
    }

    /// <summary>Changes when what a chat's engine is built with around the model does (the shield's settings, the backups,
    /// the chat's subagent pool — never how busy its servers are), so the engine, and the prompt cache with it, is rebuilt
    /// exactly then.</summary>
    private string RoutingStamp(ProviderProfile chatRoute, SubagentPool pool, bool pinned)
    {
        var backups = string.Join(",", RouteRoster.Backups(Config.Providers, chatRoute.RouteId).Select(b => b.RouteId + (b.IsPrivateRoute ? "+p" : "")));
        return $"{Config.Privacy}|{chatRoute.IsPrivateRoute}|{chatRoute.IsInteractiveOnly}|{backups}|{pool.Signature}|{pinned}";
    }

    // MARK: Interactive-only plans

    /// <summary>Whether a chat is working without the user: a task from its task list, or a /goal (which carries on by
    /// itself).</summary>
    private bool IsUnattended(string sessionId) => _queueSessions.ContainsKey(sessionId) || Session(sessionId)?.Goal is not null;

    /// <summary>The primary route, when its plan's terms allow only interactive use (the Qwen Coding Plan). A chat checks its own
    /// route: <see cref="InteractiveOnlyRoute(SessionVM?)"/>.</summary>
    public ProviderProfile? InteractiveOnlyPrimary =>
        Config.Providers.FirstOrDefault(p => p.RouteId == Config.ActiveRoute) is { IsInteractiveOnly: true } primary ? primary : null;

    public static string QueueRefusal(ProviderProfile route) =>
        $"{route.Name} is for interactive use only under its plan's terms, so the task list won't run on it. Pick another model for this chat (the model menu under the message box) for unattended work.";

    public static string GoalRefusal(ProviderProfile route) =>
        $"{route.Name} is for interactive use only under its plan's terms, so the goal won't carry on by itself. Reply to take it one round further, or pick another model for this chat for unattended work.";
}
