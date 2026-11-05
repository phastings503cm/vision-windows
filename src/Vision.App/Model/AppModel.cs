using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Vision.App.Model.Code;
using Vision.Core;
using Microsoft.Win32;

namespace Vision.App.Model;

/// <summary>Which half of the window is showing.</summary>
public enum WorkspaceMode { Chat, Code }

/// <summary>Settings pages. (<see cref="Spark"/> is the "Model servers" page: the Spark Swapper boxes.)</summary>
public enum SettingsTab { General, Models, Spark, Skills, Plugins, Editor, Guardian, ImportExport, ClaudeCode }

public enum SkillsAction { Generate, Import, NewManual }

/// <summary>Root-level state: the config, the host that drives sessions, the current project
/// folder, and the code-mode workspace.</summary>
public sealed partial class AppModel : ObservableObject
{
    public AppConfig Config { get; }
    public AgentHost Host { get; }
    /// <summary>The DGX Spark's model switcher.</summary>
    public SparkController Spark { get; }
    /// <summary>File tree, open editors, and terminals for the current project.</summary>
    public CodeWorkspace Code { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsChatMode), nameof(IsCodeMode))] private WorkspaceMode _mode;
    /// <summary>The folder both modes operate in.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ProjectName))] private string? _project;
    /// <summary>A newer release is available (shown as a banner until dismissed).</summary>
    [ObservableProperty] private UpdateInfo? _update;
    /// <summary>The plan panel is on screen (the transcript's plan card then shrinks to a line).</summary>
    [ObservableProperty] private bool _planPanelVisible;

    /// <summary>The last connection test of each route (Settings › AI Models shows it as a dot): route id → result.</summary>
    public Dictionary<string, RouteCheck> RouteChecks { get; } = new(StringComparer.Ordinal);

    public sealed record RouteCheck(bool Ok, string Detail, DateTimeOffset At);

    public sealed record UpdateInfo(string Version, string Url);

    /// <summary>Views ask the shell to open windows through these.</summary>
    public event Action<SettingsTab, SkillsAction?>? SettingsRequested;
    public event Action? WizardRequested;
    /// <summary>Open the setup window straight into the Amazon Bedrock guide, on the given page (for an
    /// existing Bedrock route, starting from its sign-in and Region).</summary>
    public event Action<Views.Guide.BedrockPage, ProviderProfile?>? BedrockGuideRequested;
    /// <summary>Open the setup window on "Connect my Spark Swapper" (true: straight to the address-and-login form).</summary>
    public event Action<bool>? SwapperGuideRequested;
    /// <summary>Open the setup window on "Connect a subscription plan" (optionally with a plan already chosen).</summary>
    public event Action<string?>? PlanGuideRequested;
    public event Action? MemoryRequested;
    public event Action? ExternalImportRequested;
    public event Action? MemoriesRequested;
    public event Action? OpenClawImportRequested;
    public event Action<string>? ImageRequested;

    /// <param name="host">The agent host to use (tests pass one with their own files); by default one
    /// over <paramref name="config"/> and <paramref name="log"/>.</param>
    public AppModel(AppConfig config, ConversationLog log, Dispatcher dispatcher, AgentHost? host = null)
    {
        Config = config;
        Host = host ?? new AgentHost(config, log, dispatcher);
        Spark = new SparkController(config, Host);
        Code = new CodeWorkspace(config, dispatcher);
        _mode = config.LastMode == "code" ? WorkspaceMode.Code : WorkspaceMode.Chat;

        // Only the primary model's own box switching holds the chats back (another box may switch freely).
        Host.IsServerSwitching = () => Spark.IsSwitchingFor(Config.Providers.FirstOrDefault(p => p.RouteId == Config.ActiveRoute));
        // A chat waits only for its own model's box: another chat's box switching holds nothing back.
        Host.IsRouteSwitching = route => Spark.IsSwitchingFor(route);
        // Agents that reported while a chat's box was busy switching models are heard as soon as that box is done — whichever
        // box it is (each chat checks its own box again). (Every status poll raises IsSwitching, changed or not, so only the
        // step from switching to not switching counts.)
        var edge = new SwitchEdge();
        Spark.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SparkController.IsSwitching) && edge.Finished(Spark.Boxes.Where(b => b.IsSwitching).Select(b => b.Id)))
                Host.ResumeAfterServerSwitch();
        };
        Host.OnSwapCommand = (arg, vm) => _ = Spark.HandleCommandAsync(arg, vm);
        // The agent's file writes drive the editor's live reload.
        Host.OnFilesChanged = changes => Code.ApplyExternalChanges(changes);
        Code.ImageRequested += path => ImageRequested?.Invoke(path);

        // Reopen the most recent project so the app comes back where it left off.
        if (config.LiveRecentProjects.FirstOrDefault() is { } recent) OpenProject(recent, activateSession: false);
    }

    public bool IsChatMode => Mode == WorkspaceMode.Chat;
    public bool IsCodeMode => Mode == WorkspaceMode.Code;
    public string? ProjectName => Project is null ? null : Path.GetFileName(Project.TrimEnd('\\', '/'));
    public SessionVM? SelectedSession => Host.Selected;
    public bool NeedsSetup => !Config.WizardCompleted || !Config.IsConfigured;

    partial void OnModeChanged(WorkspaceMode value) => Config.LastMode = value == WorkspaceMode.Code ? "code" : "chat";

    public void Start()
    {
        StartGuardian();
        Spark.StartMonitoring();
        Host.EnsureContextProbe();
        // A task list interrupted mid-run (crash, restart, quit) resumes by itself; one the user stopped
        // on purpose does not.
        Host.ResumeQueueIfNeeded();
        StartDispatchWatch();
    }

    // MARK: - Projects

    /// <summary>Ask for a folder and adopt it.</summary>
    public void ChooseProject()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the folder the agent should work in",
            Multiselect = false,
        };
        if (Project is not null && Directory.Exists(Project)) dialog.InitialDirectory = Project;
        var owner = Application.Current?.MainWindow;
        if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true) OpenProject(dialog.FolderName);
    }

    /// <summary>Adopt a folder as the project: it becomes the agent's working directory, the
    /// permission boundary, and the root of the file tree.</summary>
    public void OpenProject(string path, bool activateSession = true)
    {
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path))
        {
            Config.ForgetProject(path);
            Host.Banner = $"{path} no longer exists.";
            return;
        }
        Project = path;
        Config.TouchProject(path);
        Host.AdoptProject(path);
        Code.Open(path);

        if (!activateSession) return;
        // Return to this project's most recent chat rather than piling up empty ones.
        if (Host.Sessions.FirstOrDefault(s => SamePath(s.WorkspacePath, path)) is { } existing) Select(existing.Id);
        else NewChat();
    }

    public void CloseProject()
    {
        if (!Code.ConfirmCloseAll()) return;
        Project = null;
        Host.AdoptProject(null);
        Code.Close();
    }

    public void ForgetProject(string path)
    {
        Config.ForgetProject(path);
        if (SamePath(Project, path)) CloseProject();
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // MARK: - Sessions

    public void Select(string? id)
    {
        Host.SelectedId = id;
        if (Host.Selected is not { } session) return;
        Host.Hydrate(session);
        // Make sure the context gauge has a number before the user asks.
        Host.EnsureContextProbe();
        // Following a chat into its project keeps both modes in step. (A chat without a project has none to follow; its
        // scratch folder is not a project.)
        if (session.WorkspacePath is { } cwd && !SamePath(cwd, Project) && Directory.Exists(cwd)) OpenProject(cwd, activateSession: false);
    }

    public SessionVM NewChat()
    {
        var session = Host.NewSession(Project);
        Host.EnsureContextProbe();
        return session;
    }

    /// <summary>A regular chat, with no project folder — even while a project is open.</summary>
    public SessionVM NewPlainChat()
    {
        Mode = WorkspaceMode.Chat;
        var session = Host.NewSession(null);
        Host.EnsureContextProbe();
        return session;
    }

    public void Send(string text, SessionVM session, IReadOnlyList<MessageAttachment>? attachments = null) =>
        Host.Send(text, session.Id, attachments);

    // MARK: - Stopping

    public void StopSelected()
    {
        if (SelectedSession is { Running: true } session) Host.StopSession(session.Id);
    }

    public void StopAll() => Host.StopAll();

    /// <summary>App exit: cancel everything without marking the queue as stopped by the user.</summary>
    public void Shutdown()
    {
        _dispatchTimer?.Stop();
        Host.Shutdown();
    }

    /// <summary>Anything working — a turn, a queue task, or a background agent.</summary>
    public bool AnythingRunning => Host.AnythingRunning;

    /// <summary>Jump to a chat by id (the queue's "Open chat").</summary>
    public void OpenSession(string id)
    {
        Mode = WorkspaceMode.Chat;
        Select(id);
    }

    // MARK: - Windows

    public void ShowSettings(SettingsTab tab = SettingsTab.General, SkillsAction? action = null) => SettingsRequested?.Invoke(tab, action);
    public void ShowWizard() => WizardRequested?.Invoke();
    public void ShowBedrockGuide(Views.Guide.BedrockPage page = Views.Guide.BedrockPage.Welcome, ProviderProfile? route = null) =>
        BedrockGuideRequested?.Invoke(page, route);
    /// <summary>"Connect my Spark Swapper": paste a connection code (or sign in with an address and login).</summary>
    public void ShowSwapperGuide(bool login = false) => SwapperGuideRequested?.Invoke(login);
    /// <summary>"Connect a subscription plan": GLM, Kimi, Qwen or OpenAI.</summary>
    public void ShowPlanGuide(string? plan = null) => PlanGuideRequested?.Invoke(plan);
    public void ShowMemory() => MemoryRequested?.Invoke();
    /// <summary>Open "Bring in Claude Code &amp; Cursor".</summary>
    public void ShowExternalImport() => ExternalImportRequested?.Invoke();
    /// <summary>Open the remembered-notes manager.</summary>
    public void ShowMemories() => MemoriesRequested?.Invoke();
    /// <summary>Open "Bring in from OpenClaw".</summary>
    public void ShowOpenClawImport() => OpenClawImportRequested?.Invoke();
}
