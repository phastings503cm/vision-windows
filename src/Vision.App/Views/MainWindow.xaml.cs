using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Input;
using Vision.App.Infrastructure;
using Vision.App.Model;
using Vision.App.Views.Chat;
using Vision.App.Views.Code;
using Vision.App.Views.Dialogs;
using Vision.App.Views.Import;
using Vision.App.Views.Settings;
using Vision.App.Views.Skills;
using Vision.App.Views.Wizard;
using Vision.Core;

namespace Vision.App.Views;

/// <summary>The one window: top bar, sidebar, and either the chat or the code workspace.</summary>
public partial class MainWindow : Window
{
    public AppModel Model { get; }

    private readonly ChatView _chatView;
    private readonly CodeModeView _codeView;
    private readonly EmptyStateView _emptyState;
    private SessionVM? _watchedSession;
    private bool _closingConfirmed;
    private SettingsWindow? _settings;
    private VaultWindow? _vault;
    private readonly QueuePanel _queuePanel;
    private readonly PromptsPanel _promptsPanel;
    private readonly RunPanelHost _runPanel;
    private readonly PlanPanel _planPanel;
    private readonly ActivityMenu _activityMenu;
    private readonly System.Windows.Threading.DispatcherTimer _activityTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow(AppModel model)
    {
        Model = model;
        InitializeComponent();
        DataContext = model;

        _chatView = new ChatView(model);
        _codeView = new CodeModeView(model);
        _emptyState = new EmptyStateView(model);
        Sidebar.Attach(model);
        _queuePanel = new QueuePanel(model) { ShowsCloseButton = false };
        _queuePanel.CloseRequested += () => SetQueuePanel(false);
        _promptsPanel = new PromptsPanel(model, PromptActions);
        // The right column: the task list and the Prompts timeline, one tab each.
        _runPanel = new RunPanelHost(_queuePanel, _promptsPanel);
        _runPanel.CloseRequested += () => SetQueuePanel(false);
        QueueHost.Content = _runPanel;
        _activityMenu = new ActivityMenu(model);
        _activityMenu.Dismiss += () => ActivityPopup.IsOpen = false;
        ActivityPopup.Child = _activityMenu;
        _activityTimer.Tick += (_, _) => UpdateActivityButton();
        _activityTimer.Start();
        _planPanel = new PlanPanel(model);
        _planPanel.CloseRequested += () => SetPlanPanel(false);
        PlanHost.Content = _planPanel;

        RestorePlacement();
        SidebarColumn.Width = new GridLength(PanelLayout.Clamp(model.Config.SidebarWidth, 200, 440));

        model.PropertyChanged += OnModelChanged;
        model.Host.PropertyChanged += OnHostChanged;
        model.Config.PropertyChanged += OnConfigChanged;
        model.Spark.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SparkController.Revision) or nameof(SparkController.IsSwitching)) UpdateModelLabel();
            if (e.PropertyName is nameof(SparkController.ConfirmTarget) && model.Spark.ConfirmTarget is not null)
                Dispatcher.BeginInvoke(ConfirmSparkSwap);
        };
        model.Host.ContextInfoChanged += UpdateModelLabel;
        model.SettingsRequested += (tab, action) => ShowSettings(tab, action);
        model.WizardRequested += ShowWizard;
        model.BedrockGuideRequested += ShowBedrockGuide;
        model.SwapperGuideRequested += ShowSwapperGuide;
        model.PlanGuideRequested += ShowPlanGuide;
        model.MemoryRequested += ShowMemory;
        model.ExternalImportRequested += ShowExternalImport;
        model.MemoriesRequested += ShowMemories;
        model.OpenClawImportRequested += ShowOpenClawImport;
        model.Host.OpenMemoryManagerRequested += ShowMemories;
        model.Host.PlanAppeared += OnPlanAppeared;
        model.ImageRequested += path => ImageViewerWindow.Show(this, path);

        RegisterShortcuts();
        UpdateMode();
        UpdateDetail();
        UpdateProject();
        UpdateModelLabel();
        UpdatePreset();
        UpdateBanner();
        UpdateRunning();
        UpdateQueueButton();
        WatchSelected(); // the chat that is open at launch too, not only the ones selected later
        SetQueuePanel(model.Config.QueuePanelOpen);
        SetPlanPanel(model.Config.PlanPanelOpen);
        UpdatePlanButton();
        Body.SizeChanged += (_, _) => FitPanels();
        Closing += OnClosing;
    }

    // MARK: - State → chrome

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppModel.Mode):
                UpdateMode();
                UpdateDetail();
                break;
            case nameof(AppModel.Project):
                UpdateProject();
                break;
            case nameof(AppModel.Update):
                UpdateBanner();
                break;
        }
    }

    private void OnHostChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AgentHost.SelectedId):
            case nameof(AgentHost.Selected):
                WatchSelected();
                UpdateModelLabel();
                UpdateDetail();
                UpdatePreset();
                UpdateRunning();
                UpdatePlanButton();
                break;
            case nameof(AgentHost.Banner):
            case nameof(AgentHost.AwsSignInProfile):
                UpdateBanner();
                break;
            case nameof(AgentHost.AnyRunning):
            case nameof(AgentHost.RunningCount):
                UpdateRunning();
                break;
            case nameof(AgentHost.QueueRunning):
                UpdateQueueButton();
                break;
        }
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppConfig.ActiveRoute):
            case nameof(AppConfig.ActiveProvider):
                UpdateModelLabel();
                UpdateBanner();
                break;
            case nameof(AppConfig.Preset):
                UpdatePreset();
                break;
            case nameof(AppConfig.ExternalImportOffered):
                // Taken up from Settings or Memory & Skills as well as from the card itself.
                if (Model.Config.ExternalImportOffered) ImportCard.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void WatchSelected()
    {
        if (_watchedSession is not null) _watchedSession.PropertyChanged -= OnSessionChanged;
        _watchedSession = Model.Host.Selected;
        if (_watchedSession is not null) _watchedSession.PropertyChanged += OnSessionChanged;
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionVM.Running) or nameof(SessionVM.Stopping)) UpdateRunning();
        if (e.PropertyName is nameof(SessionVM.Preset)) UpdatePreset();
        if (e.PropertyName is nameof(SessionVM.RouteId)) UpdateModelLabel();
        if (e.PropertyName is nameof(SessionVM.Title)) UpdateTitle();
        if (e.PropertyName is nameof(SessionVM.Todos) or nameof(SessionVM.Outline) or nameof(SessionVM.Running)) UpdatePlanButton();
    }

    private void UpdateMode()
    {
        ChatModeButton.IsChecked = Model.IsChatMode;
        CodeModeButton.IsChecked = Model.IsCodeMode;
    }

    /// <summary>One chat view serves both modes, so a half-written message survives a switch.</summary>
    private void UpdateDetail()
    {
        var session = Model.Host.Selected;
        _chatView.Session = session;
        if (Model.IsChatMode)
        {
            _codeView.ChatHost.Content = null;
            Detail.Content = session is null ? _emptyState : _chatView;
        }
        else
        {
            if (!ReferenceEquals(Detail.Content, _codeView))
            {
                Detail.Content = null;
                Detail.Content = _codeView;
            }
            _codeView.ChatHost.Content = session is null ? _codeView.NoChatPlaceholder : _chatView;
        }
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var parts = new List<string>();
        if (Model.Host.Selected is { } session && Model.IsChatMode) parts.Add(session.Title);
        if (Model.ProjectName is { } project) parts.Add(project);
        parts.Add("Vision");
        Title = string.Join(" — ", parts);
    }

    private void UpdateProject()
    {
        ProjectLabel.Text = Model.ProjectName ?? "No project";
        ProjectButton.ToolTip = Model.Project ?? "Open a project folder (Ctrl+O)";
        BranchLabel.Text = Model.Project is { } root ? ProjectContext.GitBranch(root) ?? "" : "";
        BranchLabel.Visibility = BranchLabel.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateTitle();
    }

    private void UpdateModelLabel()
    {
        // The selected chat's model (each chat has its own); without a chat, the primary.
        var selected = Model.Host.Selected;
        if (Model.Host.GuardianSignInNeeded(selected))
        {
            ModelLabel.Text = "Sign in to Guardian";
            ModelButton.ToolTip = GuardianRoutes.SignInNeeded;
            return;
        }
        var provider = Model.Host.ChatRoute(selected);
        var served = Model.Host.ModelLabelFor(selected);
        ModelLabel.Text = provider is null ? "No model" : served ?? provider.Model;
        ModelButton.ToolTip = provider is null
            ? "No model configured — run the setup wizard"
            : ClaudeCodeRoutes.IsClaudeCode(provider)
            ? $"{served}\nClaude Code on this computer — your Claude plan, through the claude app"
            : $"{provider.DisplayName}\n{provider.BaseUrl}" + (provider.IsPrivateRoute ? "\nPrivate" : "\nNot private: sensitive details are masked") +
              (Model.Spark.IsSwitchingFor(provider) ? $"\n{Model.Spark.BoxFor(provider)?.Name} is switching models…" : "");
    }

    private void UpdatePreset()
    {
        // The open chat's preset when there is one — that is what is in force — otherwise the
        // default for new chats.
        var preset = Model.Host.Selected?.Preset ?? Model.Config.AsPreset;
        PresetGlyph.Text = Icons.ForPreset(preset);
        PresetLabel.Text = preset.Label();
        PresetGlyph.Foreground = preset == PermissionPreset.FullAccess
            ? ThemeService.Brush("SystemFillColorCriticalBrush", System.Windows.Media.Colors.IndianRed)
            : ThemeService.Brush("TextFillColorSecondaryBrush", System.Windows.Media.Colors.Gray);
        PresetButton.ToolTip = preset.Detail();
    }

    private void UpdateRunning()
    {
        var session = Model.Host.Selected;
        StopButton.Visibility = session is { Running: true } ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = session is { Stopping: false };
        StopLabel.Text = session is { Stopping: true } ? "Stopping…" : "Stop";
    }

    /// <summary>The top bar's live count of what is working (main agents, subagents, task lists).</summary>
    private void UpdateActivityButton()
    {
        var count = Model.Host.ActivityCount();
        ActivityBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ActivityCount.Text = count.ToString();
        ActivityGlyph.SetResourceReference(TextBlock.ForegroundProperty, count > 0 ? "SystemFillColorSuccessBrush" : "TextFillColorPrimaryBrush");
        ActivityButton.ToolTip = count == 0
            ? "Background activity (Ctrl+Shift+A) — nothing is working right now"
            : $"Background activity (Ctrl+Shift+A) — {count} working: see what, on which model, assigned to what";
    }

    private void ToggleActivity()
    {
        ActivityPopup.IsOpen = !ActivityPopup.IsOpen;
        if (ActivityPopup.IsOpen) _activityMenu.Refresh();
    }

    /// <summary>The self-test renders the menu as it would show.</summary>
    public ActivityMenu SelfTestActivityMenu()
    {
        _activityMenu.Refresh();
        return _activityMenu;
    }

    /// <summary>The self-test hands the menu back after rendering it elsewhere.</summary>
    public void RestoreActivityMenu(ActivityMenu menu) => ActivityPopup.Child = menu;

    /// <summary>What the Prompts panel and All prompts do with a prompt: jump to it in its chat, or quote it in the composer.</summary>
    private PromptActions PromptActions => new(JumpToPrompt, AddPromptToComposer);

    private void JumpToPrompt(PromptRecord record)
    {
        if (Model.Host.Session(record.ChatId) is not { } vm)
        {
            Dialog.Info("That chat is gone", "The chat this prompt was sent in has been deleted, so there is nothing to jump to.");
            return;
        }
        Model.OpenSession(vm.Id);
        Activate();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (Model.Host.EntryIdFor(record, vm) is { } entryId && _chatView.ScrollToEntry(entryId)) return;
            vm.Note($"That prompt (“{TextUtil.Prefix(record.FirstLine, 60)}”) is no longer in the transcript — it was compacted away. Its record is in Prompts (Ctrl+Shift+H).");
        });
    }

    private void AddPromptToComposer(PromptRecord record)
    {
        if (Model.Host.Session(record.ChatId) is { } vm && vm.Id != Model.Host.SelectedId) Model.OpenSession(vm.Id);
        if (Model.Host.Selected is null) return;
        Activate();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => _chatView.InsertText(PromptTimeline.Quote(record)));
    }

    /// <summary>Put <paramref name="text"/> and <paramref name="files"/> in the selected chat's composer, unsent (Ludworks
    /// Browser's "Send to AI").</summary>
    public void PrefillComposer(string text, IReadOnlyList<string> files)
    {
        if (Model.Host.Selected is null) return;
        if (Model.IsCodeMode) Model.Code.ShowChat = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => _chatView.Prefill(text, files));
    }

    /// <summary>Show the right column on a tab; the same shortcut again hides it.</summary>
    private void ToggleRunTab(RunTab tab)
    {
        if (QueueShown && _runPanel.Tab == tab)
        {
            SetQueuePanel(false);
            return;
        }
        _runPanel.Show(tab);
        if (!QueueShown) SetQueuePanel(true);
    }

    /// <summary>Show the Prompts tab (the self-test drives it).</summary>
    public void ShowPromptsPanel(string? select = null)
    {
        _runPanel.Show(RunTab.Prompts);
        SetQueuePanel(true);
        _promptsPanel.Select(select);
    }

    private void ShowAllPrompts() => AllPromptsWindow.Show(this, Model, PromptActions);

    private void UpdateQueueButton()
    {
        var running = Model.Host.QueueRunningCount;
        QueueGlyph.Text = running > 0 ? Icons.Play : Icons.Queue;
        QueueDot.Visibility = running > 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueButton.ToolTip = running switch
        {
            0 => "Task list (Ctrl+Shift+Q) — line up tasks for this chat and let it work them unattended",
            1 => "A chat is working its task list (Ctrl+Shift+Q to show this chat's list)",
            _ => $"{running} chats are working their task lists (Ctrl+Shift+Q to show this chat's list)",
        };
    }

    /// <summary>Show or hide the task list panel on the right.</summary>
    private void SetQueuePanel(bool open)
    {
        Model.Config.QueuePanelOpen = open;
        // Too narrow a window for both: the one you just asked for wins (the plan is set aside, and returns when the queue closes).
        var setPlanAside = open && PlanShown && Body.ActualWidth > 0 && RoomForPanels() < PanelLayout.PlanMin + PanelLayout.QueueMin;
        _runPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        QueueSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        QueueColumn.MinWidth = open ? PanelLayout.QueueMin : 0;
        QueueColumn.Width = new GridLength(open ? PanelLayout.Clamp(Model.Config.QueuePanelWidth, PanelLayout.QueueMin, PanelLayout.QueueMax) : 0);
        if (setPlanAside)
        {
            // (After the queue is shown, so putting the plan away is not undone at once for want of a queue to make room for.)
            _planHeldBack = true;
            SetPlanPanel(false, remember: false);
        }
        FitPanels();
    }

    // MARK: - Room for the panels

    private const double ChatMinWidth = 360;
    private const double SplitterWidth = 5;

    /// <summary>The plan panel is open as far as the user is concerned but hidden because the window has no room for it and the queue.</summary>
    private bool _planHeldBack;

    private bool PlanShown => _planPanel.Visibility == Visibility.Visible;
    private bool QueueShown => _runPanel.Visibility == Visibility.Visible;

    /// <summary>The sidebar's width as it is now or is about to be (its column only reports the new width after the next
    /// layout pass).</summary>
    private double SidebarNow => Sidebar.Visibility == Visibility.Visible ? Math.Clamp(SidebarColumn.Width.Value, 200, 440) : 0;

    /// <summary>How wide the panels on the right can be together: what the body has left after the sidebar, the chat's own
    /// minimum and the splitters.</summary>
    private double RoomForPanels() => Body.ActualWidth - SidebarNow - 3 * SplitterWidth - ChatMinWidth;

    /// <summary>Give the open panels the width they were last given, or less if the window has no room for it (the plan
    /// first, then the queue), and close the plan when the two won't fit side by side at all. Runs when a panel opens, the
    /// sidebar appears and the window changes size — and panels squeezed earlier grow back when there is room again.</summary>
    private void FitPanels()
    {
        if (Body.ActualWidth <= 0) return; // not laid out yet: the first size change fits them
        // A plan set aside for room comes back when the window has room for it again (or the queue is out of the way).
        if (_planHeldBack && !PlanShown && (!QueueShown || RoomForPanels() >= PanelLayout.PlanMin + PanelLayout.QueueMin))
        {
            _planHeldBack = false;
            SetPlanPanel(true, remember: false);
            return;
        }
        var fit = PanelLayout.Fit(RoomForPanels(), Model.Config.PlanPanelWidth, Model.Config.QueuePanelWidth, PlanShown, QueueShown);
        if (fit.ClosePlan)
        {
            _planHeldBack = true;
            SetPlanPanel(false, remember: false); // fits what is left (the queue alone) on the way out
            return;
        }
        if (PlanShown) PlanColumn.Width = new GridLength(fit.Plan);
        if (QueueShown) QueueColumn.Width = new GridLength(fit.Queue);
    }

    private void ToggleQueuePanel() => ToggleRunTab(RunTab.Tasks);

    /// <summary>Show or hide the task list panel (the self-test drives it).</summary>
    public void ShowQueuePanel(bool open)
    {
        if (open) _runPanel.Show(RunTab.Tasks);
        SetQueuePanel(open);
    }

    /// <summary>Open the queue's add card and use it (the self-test drives it: the card has to survive being opened again and again).</summary>
    public void SelfTestAddQueueTasks(params string[] titles) => _queuePanel.SelfTestAddTasks(titles);

    /// <summary>The chat view, for the self-test's measurements.</summary>
    internal ChatView SelfTestChatView => _chatView;

    /// <summary>The panel is on the right, so dragging left makes it wider and the chat gives way (never past its minimum).</summary>
    private void QueueSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var most = Math.Max(QueueColumn.MinWidth, Math.Min(QueueColumn.MaxWidth, RoomForPanels() - (PlanShown ? PlanColumn.ActualWidth : 0)));
        QueueColumn.Width = new GridLength(Math.Clamp(QueueColumn.ActualWidth - e.HorizontalChange, QueueColumn.MinWidth, most));
    }

    private void QueueSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (e.HorizontalChange != 0) Model.Config.QueuePanelWidth = QueueColumn.ActualWidth; // (a click that moved nothing keeps the width it wanted)
    }

    // MARK: - Plan panel

    /// <summary>Show or hide the plan panel on the right.</summary>
    private void SetPlanPanel(bool open) => SetPlanPanel(open, remember: true);

    /// <param name="remember">False when the window, not the user, decided: the plan is set aside for lack of room and comes
    /// back when there is room again, so the open/closed choice that is saved stays the user's.</param>
    private void SetPlanPanel(bool open, bool remember)
    {
        if (remember)
        {
            Model.Config.PlanPanelOpen = open;
            _planHeldBack = false;
        }
        if (open && QueueShown && Body.ActualWidth > 0 && RoomForPanels() < PanelLayout.PlanMin + PanelLayout.QueueMin) SetQueuePanel(false);
        _planPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        Model.PlanPanelVisible = open;
        PlanSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PlanColumn.MinWidth = open ? PanelLayout.PlanMin : 0;
        PlanColumn.Width = new GridLength(open ? PanelLayout.Clamp(Model.Config.PlanPanelWidth, PanelLayout.PlanMin, PanelLayout.PlanMax) : 0);
        FitPanels();
        UpdatePlanButton();
    }

    private void TogglePlanPanel() => SetPlanPanel(_planPanel.Visibility != Visibility.Visible);

    /// <summary>Show or hide the plan panel (the self-test drives it).</summary>
    public void ShowPlanPanel(bool open) => SetPlanPanel(open);

    private void PlanSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (e.HorizontalChange != 0) Model.Config.PlanPanelWidth = PlanColumn.ActualWidth;
    }

    /// <summary>The agent made a plan in the chat you're looking at: show it (once per chat), unless you've
    /// turned that off.</summary>
    private void OnPlanAppeared(SessionVM session)
    {
        if (session.Id != Model.Host.SelectedId || PlanShown) return;
        // Opening by itself never takes the room of a panel you opened: the plan waits (its button still counts the steps).
        if (QueueShown && Body.ActualWidth > 0 && RoomForPanels() < PanelLayout.PlanMin + PanelLayout.QueueMin) return;
        SetPlanPanel(true, remember: false); // (the window's doing, not the user's choice: it is not saved as one)
    }

    /// <summary>The button's little counter: how far the open chat's plan has got ("3/7").</summary>
    private void UpdatePlanButton()
    {
        var session = Model.Host.Selected;
        var steps = session is null ? 0 : session.Outline.Count > 0 ? session.Outline.Count : session.Todos.Count;
        var done = session is null || session.Outline.Count > 0 ? 0 : session.Todos.Count(t => t.Status == TodoStatus.Completed);
        PlanCount.Text = steps > 0 ? $"{done}/{steps}" : "";
        PlanCount.Visibility = steps > 0 ? Visibility.Visible : Visibility.Collapsed;
        var working = session is { Running: true } && steps > 0 && done < steps;
        PlanGlyph.SetResourceReference(TextBlock.ForegroundProperty, working || _planPanel.Visibility == Visibility.Visible
            ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
        PlanButton.ToolTip = steps > 0
            ? $"Plan: {done} of {steps} steps done (Ctrl+Shift+P)"
            : "Plan (Ctrl+Shift+P) — what the agent is working through";
    }

    private void UpdateBanner()
    {
        var banner = Model.Host.Banner;
        BannerCard.Visibility = banner is null ? Visibility.Collapsed : Visibility.Visible;
        BannerText.Text = banner ?? "";
        BannerSetupButton.Visibility = !Model.Config.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        BannerAwsButton.Visibility = Model.Host.AwsSignInProfile is not null ? Visibility.Visible : Visibility.Collapsed;
        BannerAwsButton.IsEnabled = !_signingInToAws;
        if (Model.Update is { } update)
        {
            UpdateCard.Visibility = Visibility.Visible;
            UpdateText.Text = $"Vision {update.Version} is available (you have {AppInfo.Version}).";
        }
        else
        {
            UpdateCard.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Switching what a box serves takes minutes and affects every client, so ask.</summary>
    private async void ConfirmSparkSwap()
    {
        var spark = Model.Spark;
        if (spark.ConfirmTarget is not { } target || spark.ConfirmBox is not { } box) return;
        var current = box.Status?.ActiveModel?.Title;
        var message = (current is null ? "" : $"{current} stops, then ") +
                      $"{target.Title} loads ({SwapperSetupFlows.SwitchTime(target)}, {target.EffectiveContext:N0}-token context). OpenClaw and every chat here follow it " +
                      "automatically; if it fails to load, the previous model comes back.";
        spark.ConfirmTarget = null;
        if (!Dialog.Confirm($"Switch {box.Name} to {target.Title}?", message, "Switch")) return;
        if (await spark.SwapAsync(box, target.Key) is { } error) Dialog.Info($"Couldn't switch {box.Name}", error);
    }

    // MARK: - Top bar menus

    private void ModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Spark.Boxes.Any(b => b.Status is null)) _ = Model.Spark.RefreshAsync();
        Menus.Open(Menus.Model(Model, Model.Host.Selected), (FrameworkElement)sender);
    }

    private void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Header("New chats start with"));
        foreach (var preset in PermissionPresets.All)
        {
            var item = new MenuItem
            {
                Header = preset.Label(),
                IsChecked = Model.Config.AsPreset == preset,
                ToolTip = preset.Detail(),
                Icon = new TextBlock { Text = Icons.ForPreset(preset), Style = (Style)FindResource("Glyph"), FontSize = 13 },
            };
            item.Click += (_, _) => ChoosePreset(preset);
            menu.Items.Add(item);
        }
        if (Model.Host.Selected is { } session)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = $"This chat: {session.Preset.Label()}", IsEnabled = false });
        }
        Open(menu, (FrameworkElement)sender);
    }

    private void ChoosePreset(PermissionPreset preset)
    {
        if (preset == PermissionPreset.FullAccess && !Dialog.Confirm("Turn on full access?",
                "New chats will write files and run shell commands without asking, anywhere your user account can reach. " +
                "Existing chats keep the preset they were created with.", "Enable Full Access", destructive: true))
            return;
        Model.Config.Preset = preset.RawValue();
    }

    private void ProjectButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Open Folder…", Model.ChooseProject, Icons.OpenFolder));
        var recents = Model.Config.LiveRecentProjects.Where(p => !string.Equals(p, Model.Project, StringComparison.OrdinalIgnoreCase)).ToList();
        if (recents.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Header("Recent"));
            foreach (var path in recents.Take(10))
            {
                var item = Item(System.IO.Path.GetFileName(path.TrimEnd('\\')), () => Model.OpenProject(path), Icons.Recent);
                item.ToolTip = path;
                menu.Items.Add(item);
            }
        }
        if (Model.Project is { } project)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Reveal in Explorer", () => ShellIntegration.OpenFolder(project), Icons.OpenInWindow));
            menu.Items.Add(Item("Copy Path", () => Clipboard.SetText(project), Icons.Copy));
            menu.Items.Add(Item("Open Terminal Here", () =>
            {
                Model.Mode = WorkspaceMode.Code;
                Model.Code.NewTerminal();
            }, Icons.Terminal));
            menu.Items.Add(Item("Close Folder", Model.CloseProject, Icons.Close));
        }
        Open(menu, (FrameworkElement)sender, PlacementMode.Bottom);
    }

    private static MenuItem Header(string text) =>
        new() { Header = text, IsEnabled = false, FontSize = 11.5, FontWeight = FontWeights.SemiBold };

    private MenuItem Item(string header, Action action, string? glyph = null)
    {
        var item = new MenuItem { Header = header };
        if (glyph is not null) item.Icon = new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = 13 };
        item.Click += (_, _) => action();
        return item;
    }

    private static void Open(ContextMenu menu, FrameworkElement target, PlacementMode placement = PlacementMode.Bottom)
    {
        menu.PlacementTarget = target;
        menu.Placement = placement;
        menu.IsOpen = true;
    }

    private void RecentMenu_Opened(object sender, RoutedEventArgs e)
    {
        RecentMenu.Items.Clear();
        var recents = Model.Config.LiveRecentProjects;
        if (recents.Count == 0)
        {
            RecentMenu.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            return;
        }
        foreach (var path in recents)
        {
            var item = new MenuItem { Header = path.Replace("_", "__") };
            item.Click += (_, _) => Model.OpenProject(path);
            RecentMenu.Items.Add(item);
        }
        RecentMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "Clear Recent" };
        clear.Click += (_, _) => Model.Config.RecentProjects.Clear();
        RecentMenu.Items.Add(clear);
    }

    // MARK: - Shortcuts

    private void RegisterShortcuts()
    {
        void Bind(Key key, ModifierKeys modifiers, Action action) =>
            InputBindings.Add(new KeyBinding(new RelayCommand(action), key, modifiers));

        Bind(Key.N, ModifierKeys.Control, () => NewChat());
        Bind(Key.N, ModifierKeys.Control | ModifierKeys.Shift, NewPlainChat);
        Bind(Key.O, ModifierKeys.Control, Model.ChooseProject);
        Bind(Key.S, ModifierKeys.Control, Model.Code.SaveActive);
        Bind(Key.S, ModifierKeys.Control | ModifierKeys.Shift, Model.Code.SaveAll);
        Bind(Key.W, ModifierKeys.Control, CloseEditor);
        Bind(Key.OemComma, ModifierKeys.Control, () => ShowSettings(SettingsTab.General));
        Bind(Key.OemPeriod, ModifierKeys.Control, Model.StopSelected);
        Bind(Key.OemPeriod, ModifierKeys.Control | ModifierKeys.Shift, Model.StopAll);
        Bind(Key.M, ModifierKeys.Control | ModifierKeys.Shift, ShowMemory);
        Bind(Key.Q, ModifierKeys.Control | ModifierKeys.Shift, ToggleQueuePanel);
        Bind(Key.H, ModifierKeys.Control | ModifierKeys.Shift, () => ToggleRunTab(RunTab.Prompts));
        Bind(Key.H, ModifierKeys.Control | ModifierKeys.Alt, ShowAllPrompts);
        Bind(Key.A, ModifierKeys.Control | ModifierKeys.Shift, ToggleActivity);
        Bind(Key.P, ModifierKeys.Control | ModifierKeys.Shift, TogglePlanPanel);
        Bind(Key.K, ModifierKeys.Control | ModifierKeys.Shift, ShowVault);
        Bind(Key.D1, ModifierKeys.Control, () => Model.Mode = WorkspaceMode.Chat);
        Bind(Key.D2, ModifierKeys.Control, () => Model.Mode = WorkspaceMode.Code);
        Bind(Key.B, ModifierKeys.Control, ToggleTree);
        Bind(Key.D, ModifierKeys.Control | ModifierKeys.Shift, ToggleEditor);
        Bind(Key.F, ModifierKeys.Control | ModifierKeys.Shift, ToggleChatOnly);
        Bind(Key.E, ModifierKeys.Control | ModifierKeys.Shift, ToggleSidebar);
        Bind(Key.C, ModifierKeys.Control | ModifierKeys.Shift, ToggleChatPanel);
        Bind(Key.Oem3, ModifierKeys.Control, ToggleTerminal);
        Bind(Key.Oem3, ModifierKeys.Control | ModifierKeys.Shift, NewTerminal);
        Bind(Key.L, ModifierKeys.Control, FocusComposer);
        Bind(Key.P, ModifierKeys.Control, GoToFile);
        Bind(Key.F1, ModifierKeys.None, ShowShortcuts);
    }

    private void NewChat()
    {
        Model.NewChat();
        FocusComposer();
    }

    /// <summary>A regular chat, without a project — even while one is open.</summary>
    private void NewPlainChat()
    {
        Model.NewPlainChat();
        FocusComposer();
    }

    private void CloseEditor()
    {
        if (Model.IsCodeMode) Model.Code.CloseActiveBuffer();
    }

    private void ToggleTree()
    {
        Model.Mode = WorkspaceMode.Code;
        Model.Code.ShowTree = !Model.Code.ShowTree;
    }

    private void ToggleEditor()
    {
        Model.Mode = WorkspaceMode.Code;
        _codeView.ToggleEditor();
    }

    /// <summary>Code mode with only the chat and the project status — or back to the panes that were showing.</summary>
    private void ToggleChatOnly()
    {
        Model.Mode = WorkspaceMode.Code;
        Model.Code.ToggleChatOnly();
    }

    private void ToggleSidebar()
    {
        var visible = Sidebar.Visibility == Visibility.Visible;
        Sidebar.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        SidebarSplitter.Visibility = Sidebar.Visibility;
        SidebarColumn.MinWidth = visible ? 0 : 200;
        SidebarColumn.Width = visible ? new GridLength(0) : new GridLength(PanelLayout.Clamp(Model.Config.SidebarWidth, 200, 440));
        FitPanels(); // a sidebar that comes back takes room from the panels; one that goes gives it back
    }

    private void ToggleChatPanel()
    {
        Model.Mode = WorkspaceMode.Code;
        Model.Code.ShowChat = !Model.Code.ShowChat;
    }

    private void ToggleTerminal()
    {
        if (Model.Project is null) return;
        Model.Mode = WorkspaceMode.Code;
        Model.Code.ToggleTerminal();
    }

    private void NewTerminal()
    {
        if (Model.Project is null) return;
        Model.Mode = WorkspaceMode.Code;
        Model.Code.NewTerminal();
    }

    private void FocusComposer()
    {
        if (Model.Host.Selected is null) Model.NewChat();
        if (Model.IsCodeMode) Model.Code.ShowChat = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, _chatView.FocusComposer);
    }

    private void GoToFile()
    {
        if (Model.Project is null) return;
        Model.Mode = WorkspaceMode.Code;
        Model.Code.ShowTree = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, _codeView.FocusFilter);
    }

    private void ShowShortcuts() => Dialog.Info("Keyboard shortcuts", string.Join("\n", new[]
    {
        "Ctrl+N\tNew chat",
        "Ctrl+Shift+N\tNew chat without a project",
        "Ctrl+O\tOpen folder",
        "Ctrl+L\tFocus the chat input",
        "Enter\tSend  ·  Shift+Enter: new line  ·  while the agent works, Enter adds it to this chat's tasks",
        "Ctrl+Enter\tAdd to tasks: it starts by itself when the work before it finishes",
        "Ctrl+.\tStop the agent  ·  Ctrl+Shift+.: stop all",
        "Ctrl+1 / Ctrl+2\tChat / Code",
        "Ctrl+P\tGo to file",
        "Ctrl+B\tToggle file tree",
        "Ctrl+Shift+D\tToggle the editor in code mode (opening a file shows it)",
        "Ctrl+Shift+F\tChat only in code mode: hide the file tree, editor and terminal",
        "Ctrl+`\tToggle terminal  ·  Ctrl+Shift+`: new terminal",
        "Ctrl+Shift+C\tToggle the chat panel in code mode",
        "Ctrl+Shift+E\tToggle the sidebar",
        "Ctrl+S / Ctrl+Shift+S\tSave / Save all",
        "Ctrl+W\tClose editor",
        "Ctrl+Shift+M\tMemory & skills",
        "Ctrl+Shift+Q\tTask list (this chat's): what it is doing now, and what is queued",
        "Ctrl+Shift+H\tPrompts (this chat's): every prompt it sent, and how it ended",
        "Ctrl+Alt+H\tAll prompts, across chats",
        "Ctrl+Shift+A\tBackground activity: what is working, on which model",
        "Ctrl+Shift+P\tPlan panel (what the agent is working through)",
        "Ctrl+Shift+K\tCredentials vault",
        "Ctrl+,\tSettings",
        "",
        "In the chat: Ctrl+F finds text anywhere in it (Enter: older match, Shift+Enter: newer); Ctrl+C in a message with nothing selected copies the whole message.",
        "In the terminal: Ctrl+Shift+C / Ctrl+Shift+V copy and paste; Ctrl+C copies when text is selected.",
    }));

    // MARK: - Windows

    public void ShowWizard()
    {
        var wizard = new SetupWizardWindow(Model) { Owner = this };
        wizard.ShowDialog();
        UpdateModelLabel();
        UpdateBanner();
        _ = OfferExternalImportAsync();
    }

    public void ShowBedrockGuide(Vision.App.Views.Guide.BedrockPage page, ProviderProfile? route)
    {
        var owner = OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;
        var wizard = new SetupWizardWindow(Model) { Owner = owner };
        wizard.StartBedrockGuide(page, route);
        wizard.ShowDialog();
        UpdateModelLabel();
        UpdateBanner();
    }

    /// <summary>"Connect my Spark Swapper" in the setup window, over whatever window is in front (Settings, say).</summary>
    public void ShowSwapperGuide(bool login)
    {
        var owner = OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;
        var wizard = new SetupWizardWindow(Model) { Owner = owner };
        wizard.StartSwapperGuide(login);
        wizard.ShowDialog();
        UpdateModelLabel();
        UpdateBanner();
    }

    /// <summary>"Connect a subscription plan" in the setup window.</summary>
    public void ShowPlanGuide(string? plan)
    {
        var owner = OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;
        var wizard = new SetupWizardWindow(Model) { Owner = owner };
        wizard.StartPlanGuide(plan);
        wizard.ShowDialog();
        UpdateModelLabel();
        UpdateBanner();
    }

    public void ShowSettings(SettingsTab tab = SettingsTab.General, SkillsAction? action = null)
    {
        if (_settings is { IsLoaded: true })
        {
            _settings.Navigate(tab, action);
            _settings.Activate();
            return;
        }
        _settings = new SettingsWindow(Model, tab, action) { Owner = this };
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
    }

    public void ShowVault()
    {
        if (_vault is { IsLoaded: true })
        {
            _vault.Activate();
            return;
        }
        _vault = new VaultWindow(Model.Host) { Owner = this };
        _vault.Closed += (_, _) => _vault = null;
        _vault.Show();
    }

    public void ShowMemory()
    {
        var window = new MemoryWindow(Model) { Owner = this };
        window.ShowDialog();
    }

    private MemoriesWindow? _memories;

    /// <summary>The remembered notes: search, add, edit, pin, delete.</summary>
    public void ShowMemories()
    {
        if (_memories is { IsLoaded: true })
        {
            _memories.Activate();
            return;
        }
        _memories = new MemoriesWindow(Model) { Owner = OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this };
        _memories.Closed += (_, _) => _memories = null;
        _memories.Show();
    }

    /// <summary>"Bring in from OpenClaw": skills, memory, keys and model servers from an OpenClaw install on this
    /// PC or on another machine.</summary>
    public void ShowOpenClawImport()
    {
        var owner = OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;
        new OpenClawImportWindow(Model) { Owner = owner }.ShowDialog();
    }

    /// <summary>"Bring in Claude Code &amp; Cursor". Opened over whatever window is in front, so it works
    /// from Settings and from Memory &amp; Skills alike.</summary>
    public void ShowExternalImport()
    {
        ImportCard.Visibility = Visibility.Collapsed;
        var owner = OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;
        new ExternalImportWindow(Model) { Owner = owner }.ShowDialog();
        Model.Config.ExternalImportOffered = true;
    }

    /// <summary>A quiet, one-time offer: when Claude Code or Cursor is on this PC and has something Vision
    /// doesn't, say so once, with a button. Dismissing it or opening the import ends the offer for good.</summary>
    public async Task OfferExternalImportAsync()
    {
        if (Model.Config.ExternalImportOffered || SelfTest.Current is not null) return;
        var support = Model.Host.SkillLocations;
        ExternalInventory inventory;
        try
        {
            inventory = await Task.Run(() => ExternalScanner.Scan(ExternalLocations.Standard, support));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (Model.Config.ExternalImportOffered || inventory.Suggested == 0) return;
        ImportText.Text = $"Claude Code and Cursor are on this PC. Bring {ExternalLabels.Summarize(inventory.Items.Where(i => i.SelectedByDefault))} into Vision?";
        ImportCard.Visibility = Visibility.Visible;
    }

    /// <summary>Bring the window forward when a second launch hands us a folder.</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    // MARK: - Menu handlers

    private void NewChat_Click(object sender, RoutedEventArgs e) => NewChat();
    private void NewPlainChat_Click(object sender, RoutedEventArgs e) => NewPlainChat();
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Model.ChooseProject();
    private void CloseFolder_Click(object sender, RoutedEventArgs e) => Model.CloseProject();
    private void NewTerminal_Click(object sender, RoutedEventArgs e) => NewTerminal();
    private void Save_Click(object sender, RoutedEventArgs e) => Model.Code.SaveActive();
    private void SaveAll_Click(object sender, RoutedEventArgs e) => Model.Code.SaveAll();
    private void CloseEditor_Click(object sender, RoutedEventArgs e) => CloseEditor();
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void ChatMode_Click(object sender, RoutedEventArgs e) => Model.Mode = WorkspaceMode.Chat;
    private void CodeMode_Click(object sender, RoutedEventArgs e) => Model.Mode = WorkspaceMode.Code;
    private void ToggleSidebar_Click(object sender, RoutedEventArgs e) => ToggleSidebar();
    private void ToggleTree_Click(object sender, RoutedEventArgs e) => ToggleTree();
    private void ToggleEditor_Click(object sender, RoutedEventArgs e) => ToggleEditor();
    private void ChatOnly_Click(object sender, RoutedEventArgs e) => ToggleChatOnly();
    private void ToggleTerminal_Click(object sender, RoutedEventArgs e) => ToggleTerminal();
    private void ToggleChatPanel_Click(object sender, RoutedEventArgs e) => ToggleChatPanel();
    private void GoToFile_Click(object sender, RoutedEventArgs e) => GoToFile();
    private void FocusComposer_Click(object sender, RoutedEventArgs e) => FocusComposer();
    private void Stop_Click(object sender, RoutedEventArgs e) => Model.StopSelected();
    private void StopAll_Click(object sender, RoutedEventArgs e) => Model.StopAll();
    private void Memory_Click(object sender, RoutedEventArgs e) => ShowMemory();
    private void Vault_Click(object sender, RoutedEventArgs e) => ShowVault();
    private void ToggleQueue_Click(object sender, RoutedEventArgs e) => ToggleQueuePanel();
    private void TogglePrompts_Click(object sender, RoutedEventArgs e) => ToggleRunTab(RunTab.Prompts);
    private void AllPrompts_Click(object sender, RoutedEventArgs e) => ShowAllPrompts();
    private void Activity_Click(object sender, RoutedEventArgs e) => ToggleActivity();
    private void TogglePlan_Click(object sender, RoutedEventArgs e) => TogglePlanPanel();
    private void Memories_Click(object sender, RoutedEventArgs e) => ShowMemories();
    private void ImportOpenClaw_Click(object sender, RoutedEventArgs e) => ShowOpenClawImport();
    private void QueueLog_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Host.SelectedId is not { } chatId) return;
        new QueueLogWindow(Model.Host, chatId) { Owner = this }.Show();
    }
    private void Skills_Click(object sender, RoutedEventArgs e) => ShowSettings(SettingsTab.Skills);
    private void GenerateSkill_Click(object sender, RoutedEventArgs e) => ShowSettings(SettingsTab.Skills, SkillsAction.Generate);
    private void ImportSkills_Click(object sender, RoutedEventArgs e) => ShowSettings(SettingsTab.Skills, SkillsAction.Import);
    private void ImportExternal_Click(object sender, RoutedEventArgs e) => ShowExternalImport();
    private void BringInFromCard_Click(object sender, RoutedEventArgs e) => ShowExternalImport();
    private void DismissImport_Click(object sender, RoutedEventArgs e)
    {
        ImportCard.Visibility = Visibility.Collapsed;
        Model.Config.ExternalImportOffered = true;
    }
    private void ReloadContext_Click(object sender, RoutedEventArgs e) => Model.Host.RefreshProjectContext();
    private void Wizard_Click(object sender, RoutedEventArgs e)
    {
        Model.Host.Banner = null;
        ShowWizard();
    }
    private void Shortcuts_Click(object sender, RoutedEventArgs e) => ShowShortcuts();
    private void DataFolder_Click(object sender, RoutedEventArgs e) => ShellIntegration.OpenFolder(AppPaths.Root);
    private void Releases_Click(object sender, RoutedEventArgs e) => ShellIntegration.Open(UpdateChecker.ReleasesPage);
    private void DismissBanner_Click(object sender, RoutedEventArgs e) => Model.Host.Banner = null;

    private bool _signingInToAws;

    /// <summary>The banner's "Sign in to AWS…": the AWS sign-in behind a Bedrock route ran out. Sign the
    /// same profile in again (the browser opens), then the next message just works.</summary>
    private async void AwsSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Host.AwsSignInProfile is not { } profile || _signingInToAws) return;
        var region = Model.Config.Providers.FirstOrDefault(p => p.Kind == ProviderKind.Bedrock && (p.AwsProfile ?? AwsSignIn.DefaultProfile) == profile)?.AwsRegion;
        _signingInToAws = true;
        Model.Host.Banner = "Finish signing in to AWS in the browser window that just opened…";
        Model.Host.AwsSignInProfile = profile;
        try
        {
            var result = await Task.Run(() => AwsAccounts.SignInAgainAsync(profile, region));
            _signingInToAws = false;
            if (result.Succeeded)
            {
                Model.Host.Banner = null;
                Model.Host.Broadcast($"Signed in to AWS again (profile \"{profile}\"). Send your message again to carry on.");
            }
            else
            {
                Model.Host.Banner = result.Message;
                Model.Host.AwsSignInProfile = profile;
            }
        }
        catch (Exception error)
        {
            _signingInToAws = false;
            Model.Host.Banner = $"Signing in to AWS failed: {error.Message}";
            Model.Host.AwsSignInProfile = profile;
        }
        finally
        {
            _signingInToAws = false;
            UpdateBanner();
        }
    }
    private void DismissUpdate_Click(object sender, RoutedEventArgs e) => Model.Update = null;
    private void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        ShellIntegration.Open(Model.Update?.Url ?? UpdateChecker.ReleasesPage);
        Model.Update = null;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        var release = await UpdateChecker.CheckAsync();
        if (release is null)
        {
            Dialog.Info("You're up to date", $"Vision {AppInfo.Version} is the latest release (or the release feed could not be reached).");
            return;
        }
        Model.Update = new AppModel.UpdateInfo(release.Tag, release.Url);
    }

    private void About_Click(object sender, RoutedEventArgs e) => Dialog.Info("About Vision",
        $"Vision for Windows {AppInfo.Version}\n\nA native AI assistant and coding agent: chat with any OpenAI-compatible model server, " +
        "or open a project folder and it works there with file, search, and shell tools, asking before anything risky.\n\n" +
        $"Data folder: {AppPaths.Root}\n{UpdateChecker.ReleasesPage.Replace("/latest", "")}");

    private void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (e.HorizontalChange == 0) return;
        Model.Config.SidebarWidth = SidebarColumn.ActualWidth;
        FitPanels();
    }

    // MARK: - Placement and closing

    private void RestorePlacement()
    {
        if (Model.Config.Window is not { } placement) return;
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var visible = placement.Left + 100 > virtualLeft && placement.Top + 50 > virtualTop
                      && placement.Left < virtualLeft + SystemParameters.VirtualScreenWidth - 100
                      && placement.Top < virtualTop + SystemParameters.VirtualScreenHeight - 50;
        if (!visible) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = Math.Max(MinWidth, placement.Width);
        Height = Math.Max(MinHeight, placement.Height);
        if (placement.Maximized) Loaded += (_, _) => WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty) return;
        Model.Config.SaveWindow(new AppConfig.WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            WindowState == WindowState.Maximized));
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingConfirmed) return;
        if (!Model.Code.ConfirmCloseAll())
        {
            e.Cancel = true;
            return;
        }
        if (Model.AnythingRunning && !Dialog.Confirm("Stop running agents?",
                $"{Formatting.Plural(Model.Host.RunningCount, "agent is", "agents are")} still working. Quitting stops them.",
                "Stop and Quit", destructive: true))
        {
            e.Cancel = true;
            return;
        }
        _closingConfirmed = true;
        SavePlacement();
        foreach (var terminal in Model.Code.Terminals.ToList()) terminal.Dispose();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Dark title bar in dark mode (Windows 10 20H1+ / 11).
        ApplyTitleBarTheme();
        ThemeService.Instance.Changed += ApplyTitleBarTheme;
    }

    private void ApplyTitleBarTheme()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = ThemeService.Instance.IsDark ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
