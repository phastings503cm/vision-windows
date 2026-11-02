using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Vision.App.Infrastructure;
using Vision.App.Model;
using Vision.App.Model.Code;
using Vision.App.Views.Skills;
using Vision.Core;
using Microsoft.Win32;
using MessageRole = Vision.App.Model.MessageRole;
using System.Runtime.InteropServices;

namespace Vision.App.Views.Chat;

/// <summary>The conversation: transcript, permission prompts, and the composer. One instance serves
/// every chat (and both modes); drafts and queued attachments are kept per chat.</summary>
public partial class ChatView : UserControl
{
    private readonly AppModel _model;
    private SessionVM? _session;
    private ScrollViewer? _scroller;
    /// <summary>Follow new output until the user scrolls up to read something.</summary>
    private bool _pinned = true;
    private readonly ObservableCollection<ComposerChip> _attachments = [];
    private readonly Dictionary<string, (string Text, List<ComposerChip> Attachments)> _drafts = new();
    private readonly ThinkingFooter _thinking = new();
    private readonly PhaseFooter _phase = new();
    private readonly FilesFooter _files = new();
    private IReadOnlyList<Skill> _slashSkills = [];
    private int _slashSkillsRevision = -1;
    private readonly DispatcherTimer _gaugeTimer;
    private GateVM? _gate;

    /// <summary>Attachments larger than this are refused: no model reads a 50 MB file usefully.</summary>
    private const long MaxAttachmentBytes = 25L * 1024 * 1024;

    public ChatView(AppModel model)
    {
        _model = model;
        InitializeComponent();
        InitPaging();
        AttachmentList.ItemsSource = _attachments;
        _attachments.CollectionChanged += (_, _) =>
        {
            AttachmentList.Visibility = _attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateSendState();
            RefreshChipDeliveries();
        };
        // Every way text is pasted (Ctrl+V, Shift+Insert, the context menu) and dropped: a very large block becomes a chip.
        DataObject.AddPastingHandler(Composer, OnComposerPasting);
        Composer.PreviewDragOver += OnComposerDragOver;
        Composer.PreviewDrop += OnComposerDrop;
        _gaugeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _gaugeTimer!.Stop();
            UpdateGauge();
        }, Dispatcher);
        _gaugeTimer.Stop();

        Transcript.Loaded += (_, _) => HookScroller();
        _model.Config.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppConfig.ActiveRoute) or nameof(AppConfig.ActiveProvider))
            {
                UpdateMeta();
                RefreshChipDeliveries();
            }
        };
        _model.Host.ContextInfoChanged += () =>
        {
            UpdateMeta();
            UpdateGauge();
            RefreshChipDeliveries();
        };
        _model.Host.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AgentHost.SkillsRevision)) UpdateSkillsButton();
        };
        _model.Host.PendingDrafts.CollectionChanged += (_, _) => UpdateSkillsButton();
        _model.Spark.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SparkController.ProgressLine) or nameof(SparkController.Revision)) UpdateSpark();
        };
        // While the plan panel shows the plan, the transcript's plan card is a single line.
        _model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppModel.PlanPanelVisible)) UpdateTodoCards();
        };
        _model.Code.MentionRequested += InsertMention;
        _thinking.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ThinkingFooter.IsVisible)) ScrollIfPinned();
        };
    }

    // MARK: - Session

    public SessionVM? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(value, _session)) return;
            Detach();
            _session = value;
            Attach();
        }
    }

    private void Detach()
    {
        if (_session is not { } old) return;
        _drafts[old.Id] = (Composer.Text, _attachments.ToList());
        old.PropertyChanged -= OnSessionChanged;
        old.Entries.CollectionChanged -= OnEntriesChanged;
        old.PendingGates.CollectionChanged -= OnGatesChanged;
        old.ContentChanged -= ScrollIfPinned;
        old.BackgroundJobs.CollectionChanged -= OnBackgroundJobsChanged;
        DetachPaging();
    }

    private void Attach()
    {
        _thinking.Session = _session;
        _phase.Session = _session;
        _files.Session = _session;
        _attachments.Clear();
        if (_session is not { } session)
        {
            Transcript.ItemsSource = null;
            Composer.Text = "";
            return;
        }
        session.PropertyChanged += OnSessionChanged;
        session.Entries.CollectionChanged += OnEntriesChanged;
        session.PendingGates.CollectionChanged += OnGatesChanged;
        session.ContentChanged += ScrollIfPinned;
        session.BackgroundJobs.CollectionChanged += OnBackgroundJobsChanged;

        Transcript.ItemsSource = TranscriptSource(session);
        if (_drafts.Remove(session.Id, out var draft))
        {
            Composer.Text = draft.Text;
            foreach (var attachment in draft.Attachments) _attachments.Add(attachment);
        }
        else
        {
            Composer.Text = "";
        }
        Composer.CaretIndex = Composer.Text.Length;

        UpdateStarter();
        UpdateTodoCards();
        UpdateGate();
        UpdateComposerState();
        UpdateMeta();
        UpdateSkillsButton();
        UpdateSpark();
        UpdateGoal();
        UpdateBackgroundBar();
        UpdateRunningTool();
        UpdateGauge();
        _pinned = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _scroller?.ScrollToEnd();
            // Virtualized rows measure as they appear; a second pass lands on the true bottom.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _scroller?.ScrollToEnd());
        });
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SessionVM.Running):
            case nameof(SessionVM.Stopping):
                UpdateComposerState();
                break;
            case nameof(SessionVM.Preset):
            case nameof(SessionVM.Thinking):
            case nameof(SessionVM.Cwd):
                UpdateMeta();
                break;
            case nameof(SessionVM.RouteId):
                UpdateMeta();
                UpdateGauge();
                UpdateStarter();
                // Another model, another "sent as …" (pictures, the window that decides the parts).
                RefreshChipDeliveries();
                break;
            case nameof(SessionVM.LastUsage):
            case nameof(SessionVM.ContextUsed):
                UpdateMeta();
                UpdateGauge();
                break;
            case nameof(SessionVM.Goal):
                UpdateGoal();
                break;
            case nameof(SessionVM.RunningTool):
            // "Waiting for the mouse…" (see SessionVM.ShowMachineWait) rides on the activity line.
            case nameof(SessionVM.Activity):
                UpdateRunningTool();
                break;
        }
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateStarter();
        if (e.NewItems is { } added && added.OfType<TodosEntryVM>().Any()) UpdateTodoCards();
        PageOnEntriesChanged();
        ScrollIfPinned();
    }

    /// <summary>The plan card in the transcript shrinks to "Plan updated — 3 of 7 done · Show" while the plan panel is open,
    /// so the plan isn't shown twice.</summary>
    private void UpdateTodoCards()
    {
        if (_session is null) return;
        foreach (var card in _session.Entries.OfType<TodosEntryVM>()) card.Compact = _model.PlanPanelVisible;
    }

    private void ShowTodoCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TodosEntryVM card)
        {
            _pinned = false;
            card.Expanded = true;
        }
    }

    private void OnGatesChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateGate();

    private void OnBackgroundJobsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateBackgroundBar();

    private void UpdateBackgroundBar()
    {
        var jobs = _session?.RunningBackgroundJobs ?? [];
        BackgroundBar.Visibility = jobs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (jobs.Count == 0) return;
        BackgroundTitle.Text = $"{jobs.Count} background agent{(jobs.Count == 1 ? "" : "s")}";
        BackgroundText.Text = string.Join(" · ", jobs.Select(j => $"{j.Id} “{j.Description}”"));
        BackgroundBar.ToolTip = string.Join("\n", jobs.Select(j => $"{j.Id} “{j.Description}” — running {j.Elapsed.FormattedDuration()}"));
    }

    private void BackgroundStop_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not { } session) return;
        var jobs = session.RunningBackgroundJobs;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var job in jobs)
        {
            var item = new MenuItem { Header = $"Stop {job.Id} “{job.Description}”".Replace("_", "__") };
            item.Click += (_, _) => _model.Host.StopBackgroundAgent(session.Id, job.Id);
            menu.Items.Add(item);
        }
        if (jobs.Count > 1)
        {
            menu.Items.Add(new Separator());
            var all = new MenuItem { Header = "Stop all" };
            all.Click += (_, _) => _model.Host.StopBackgroundAgents(session.Id);
            menu.Items.Add(all);
        }
        menu.IsOpen = true;
    }

    /// <summary>Re-create the transcript's rows, scrolled as they were, after its width shrank (the chat moved beside the
    /// editor): the virtualizing panel would otherwise keep arranging them at the width they had.</summary>
    public void RelayoutTranscript()
    {
        if (Transcript.ItemsSource is not { } source) return;
        var pinned = _pinned;
        var offset = _scroller?.VerticalOffset ?? 0;
        Transcript.ItemsSource = null;
        Transcript.ItemsSource = source;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (pinned) _scroller?.ScrollToEnd();
            else _scroller?.ScrollToVerticalOffset(offset);
        });
    }

    // MARK: - Self-test hooks

    /// <summary>The composer, for the self-test's measurements.</summary>
    internal TextBox SelfTestComposer => Composer;

    /// <summary>Attachments waiting in the composer, for the self-test.</summary>
    internal int SelfTestAttachmentCount => _attachments.Count;

    /// <summary>The composer's chips as they read ("PDF · 3 pages · sent as text"), for the self-test.</summary>
    internal IReadOnlyList<string> SelfTestChipLabels => _attachments.Select(c => (c.ShowsName ? c.Name + "  " : "") + c.Label).ToList();

    /// <summary>Attach a file the way a drop does, for the self-test.</summary>
    internal void SelfTestStage(string path) => Stage(path);

    /// <summary>Empty the composer's attachments (as sending does), for the self-test.</summary>
    internal void SelfTestClearAttachments() => _attachments.Clear();

    /// <summary>Press "Add to tasks" (Ctrl+Enter), for the self-test.</summary>
    internal void SelfTestAddToTasks() => AddToTasks();

    /// <summary>The transcript's scroll viewer, for the self-test's measurements.</summary>
    internal ScrollViewer? SelfTestScroller
    {
        get
        {
            HookScroller();
            return _scroller;
        }
    }

    // MARK: - Scrolling

    private void HookScroller()
    {
        if (_scroller is not null) return;
        _scroller = Transcript.Template.FindName("Scroller", Transcript) as ScrollViewer;
        if (_scroller is null) return;
        _scroller.ScrollChanged += (_, e) =>
        {
            // ScrollChanged bubbles: every text box and document inside a row raises its own as it is laid out (its extent
            // and viewport grow), and taken for the transcript's, each one snapped a pinned transcript back to its end — the
            // wheel couldn't move a long chat up. Only the transcript's own scroll viewer counts.
            if (!ReferenceEquals(e.OriginalSource, _scroller)) return;
            // The user scrolled (the wheel, the scrollbar, a key — even when rows laid out on the way changed the extent in the
            // same pass, which used to snap a pinned transcript straight back to its end), or only the offset changed: stay put
            // unless they came back to the bottom (of the newest page).
            if ((e.VerticalChange != 0 && (ReaderScrolling || MovedAwayFromEnd(e))) || (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0))
            {
                _pinned = _scroller.VerticalOffset >= _scroller.ScrollableHeight - 32 && _window is not { IsLive: false };
                PageOnScroll();
            }
            else if (_pinned)
            {
                _scroller.ScrollToEnd();
            }
        };
        WatchReaderScrolling(_scroller);
    }

    private bool _scrollQueued;

    private void ScrollIfPinned()
    {
        // A streamed reply changes the transcript many times a second: one scroll per frame is enough.
        if (!_pinned || _scroller is null || _scrollQueued) return;
        _scrollQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _scrollQueued = false;
            if (_pinned) _scroller?.ScrollToEnd();
        });
    }

    // MARK: - Chrome updates

    private void UpdateStarter()
    {
        var empty = _session is { Entries.Count: 0 };
        Starter.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Transcript.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (!empty || _session is not { } session) return;
        StarterSubtitle.Text = _model.Host.ChatRoute(session) is { } provider ? $"Running on {provider.DisplayName}" : "No model configured";
        // A chat without a project is a regular chat: general openers (and a skill or two), and a quiet way to open a folder.
        StarterTitle.Text = session.ProjectName ?? session.Title;
        OpenFolderLink.Visibility = session.IsPlain ? Visibility.Visible : Visibility.Collapsed;
        Suggestions.ItemsSource = session.IsPlain ? PlainOpeners(session) : new List<Suggestion>
        {
            new(Icons.Globe, "Give me a tour of this codebase — the entry points and how it fits together"),
            new(Icons.Wrench, "Find and fix any bugs you can verify with the tests"),
            new(Icons.Document, "Write a README section describing how to build and run this"),
            new(Icons.CheckList, "What would you improve first in this project, and why?"),
        };
    }

    /// <summary>What a regular chat offers to start with: general openers, and up to two of the user's skills.</summary>
    private List<Suggestion> PlainOpeners(SessionVM session)
    {
        var openers = new List<Suggestion>
        {
            new(Icons.Lightbulb, "Explain something simply"),
            new(Icons.Edit, "Help me write or rewrite something"),
            new(Icons.Globe, "Summarise a web page"),
            new(Icons.CheckList, "Plan something with me"),
        };
        try
        {
            var disabled = _model.Config.DisabledSkills;
            foreach (var skill in _model.Host.Skills(session)
                         .Where(s => !s.Shadowed && s.ModelInvocable && !s.AlwaysApply && !disabled.Contains(s.Id))
                         .Take(2))
                openers.Add(new Suggestion(Icons.Education, $"Use the {skill.Name} skill"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No skills to offer; the general openers stand.
        }
        return openers;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _model.ChooseProject();

    private void UpdateGate()
    {
        _gate = _session?.PendingGates.LastOrDefault();
        GateHost.Visibility = _gate is null ? Visibility.Collapsed : Visibility.Visible;
        if (_gate is null) return;
        GateDetail.Text = _gate.Detail;
        GateTool.Text = _gate.ToolLabel + (_session!.PendingGates.Count > 1 ? $"  ·  {_session.PendingGates.Count} waiting" : "");
        ScrollIfPinned();
    }

    private void UpdateComposerState()
    {
        var running = _session?.Running == true;
        SendButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        AddTaskButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = _session?.Stopping != true;
        StopGlyph.Text = _session?.Stopping == true ? Icons.Stopwatch : Icons.Stop;
        StopButton.ToolTip = _session?.Stopping == true ? "Stopping…" : "Stop the agent (Ctrl+.)";
        AttachButton.IsEnabled = !running;
        ModelMenuButton.IsEnabled = !running;
        ThinkingButton.IsEnabled = !running;
        UpdateSendState();
    }

    private void UpdateSendState()
    {
        SendButton.IsEnabled = Composer.Text.Trim().Length > 0 || _attachments.Count > 0;
        AddTaskButton.IsEnabled = Composer.Text.Trim().Length > 0;
    }

    private void UpdateMeta()
    {
        if (_session is not { } session) return;
        PresetGlyph.Text = Icons.ForPreset(session.Preset);
        PresetText.Text = session.Preset.Label();
        var fullAccess = session.Preset == PermissionPreset.FullAccess;
        PresetGlyph.SetResourceReference(TextBlock.ForegroundProperty, fullAccess ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
        PresetText.SetResourceReference(TextBlock.ForegroundProperty, fullAccess ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
        PresetInfo.ToolTip = session.Preset.Detail();

        // This chat's own model: another chat (another project) may be on another one at the same time.
        var route = _model.Host.ChatRoute(session);
        if (_model.Host.GuardianSignInNeeded(session))
        {
            // On one of Guardian's models while signed out: it waits for the sign-in rather than moving to another model.
            // What to do first: the composer's line is narrow and a Bedrock-style id is long.
            ModelMenuText.Text = $"Sign in to Guardian · {GuardianRoutes.ModelOfRouteId(session.RouteId) ?? "Guardian"}";
            ModelMenuButton.ToolTip = GuardianRoutes.SignInNeeded + " (Settings › Guardian), or pick another model for this chat.";
        }
        else
        {
            var current = _model.Host.ModelLabelFor(session) ?? route?.Model ?? "No model";
            var limit = _model.Host.ContextLimit(session);
            ModelMenuText.Text = $"{current} · {Menus.ShortTokens(limit)}";
            var claudeCode = ClaudeCodeRoutes.IsClaudeCode(route);
            var masked = route is { IsPrivateRoute: false } && _model.Config.Privacy.Mask && !claudeCode;
            ModelMenuButton.ToolTip = $"This chat's model{(route is null ? "" : $" ({route.DisplayName})")} and its context window. Pick another model for this chat " +
                                      "(other chats keep theirs; new chats in this project start with it), or a Spark Swapper model to switch what that box serves (also /swap)." +
                                      (masked ? "\nNot private: sensitive details are masked before anything is sent to it." : "") +
                                      (claudeCode ? "\nClaude Code on this computer, on your Claude plan: it reads your files itself, so what it reads isn't masked" +
                                                    (_model.Config.Privacy.Mask ? " (what Vision hands it — the chat so far, secrets in your messages — is)." : ".") : "");
        }

        ThinkingText.Text = Menus.ThinkingLabel(_model, session);
        ThinkingButton.ToolTip = "How hard the model thinks before answering (sent as reasoning_effort / enable_thinking). Also: /think <level>";

        ProjectInfo.Visibility = session.ProjectName is null ? Visibility.Collapsed : Visibility.Visible;
        ProjectText.Text = session.ProjectName ?? "";
        ProjectInfo.ToolTip = session.WorkspacePath;

        UsageText.Text = session.LastUsage is { } usage ? $"{usage.PromptTokens:N0} in · {usage.CompletionTokens:N0} out" : "";
        UsageText.Visibility = session.LastUsage is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateGauge()
    {
        if (_session is not { } session) return;
        var limit = _model.Host.ContextLimit(session);
        var used = _model.Host.ContextUsed(session.Id, Composer.Text);
        var fraction = limit > 0 ? Math.Min(1.0, used / (double)limit) : 0;
        GaugeFill.Width = Math.Max(fraction > 0 ? 3 : 0, 64 * fraction);
        var key = fraction >= 0.9 ? "SystemFillColorCriticalBrush" : fraction >= 0.7 ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush";
        GaugeFill.SetResourceReference(Border.BackgroundProperty, key);
        GaugeText.Text = $"{used:N0} / {limit:N0} ctx";
        var model = _model.Host.ModelIdFor(session) ?? "this model";
        Gauge.ToolTip = GaugeText.ToolTip =
            $"Context window in use for {model}: {used:N0} of {limit:N0} tokens — window {_model.Host.ContextSource(session)}. Type /compact to free space.";
    }

    private void UpdateSkillsButton()
    {
        if (_session is not { } session) return;
        var pinned = _model.Config.SkillsFor(session.Id).Pinned.Count;
        SkillsText.Text = pinned > 0 ? $"Skills · {pinned}" : "Skills";
        var key = pinned > 0 ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush";
        SkillsText.SetResourceReference(TextBlock.ForegroundProperty, key);
        SkillsGlyph.SetResourceReference(TextBlock.ForegroundProperty, key);
        var drafts = _model.Host.PendingDrafts.Count;
        DraftsBadge.Visibility = drafts > 0 ? Visibility.Visible : Visibility.Collapsed;
        DraftsCount.Text = drafts.ToString();
        SkillsButton.ToolTip = "Choose the skills this chat uses. Also: /skills, /skill <name>, or /<name> to run one." +
                               (drafts > 0 ? $"\n{drafts} drafted skill{(drafts == 1 ? "" : "s")} awaiting approval." : "");
    }

    private void UpdateSpark()
    {
        var line = _model.Spark.ProgressLine;
        SparkBanner.Visibility = line is null ? Visibility.Collapsed : Visibility.Visible;
        SparkText.Text = line ?? "";
        UpdateMeta();
    }

    private void UpdateGoal()
    {
        var goal = _session?.Goal;
        GoalBanner.Visibility = goal is null ? Visibility.Collapsed : Visibility.Visible;
        if (goal is null) return;
        GoalTitle.Text = $"Goal · round {goal.Round}";
        GoalText.Text = goal.Text;
        GoalBanner.ToolTip = goal.Text;
    }

    private void UpdateRunningTool()
    {
        var tool = _session?.RunningTool;
        RunningToolLine.Visibility = tool is null ? Visibility.Collapsed : Visibility.Visible;
        if (tool is null) return;
        RunningToolText.Text = $"{tool.Label.ToLowerInvariant()} {tool.Preview.Replace('\n', ' ')}";
        // A click or keystroke waiting its turn at the real mouse says so here, where it can be read in full.
        if (_session?.Activity is { } activity && activity.StartsWith("Waiting for", StringComparison.Ordinal))
            RunningToolText.Text += " — " + activity;
    }

    // MARK: - Composer

    public void FocusComposer()
    {
        Composer.Focus();
        Keyboard.Focus(Composer);
        Composer.CaretIndex = Composer.Text.Length;
    }

    private void Composer_FocusChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        ComposerBorder.SetResourceReference(Border.BorderBrushProperty,
            Composer.IsKeyboardFocused ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush");
        if (!Composer.IsKeyboardFocusWithin) SlashPopup.IsOpen = false;
    }

    private void Composer_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSendState();
        UpdateSlashHints();
        _gaugeTimer.Stop();
        _gaugeTimer.Start();
    }

    private void Composer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (SlashPopup.IsOpen && SlashList.Items.Count > 0)
        {
            switch (e.Key)
            {
                case Key.Down:
                    SlashList.SelectedIndex = (SlashList.SelectedIndex + 1) % SlashList.Items.Count;
                    SlashList.ScrollIntoView(SlashList.SelectedItem);
                    e.Handled = true;
                    return;
                case Key.Up:
                    SlashList.SelectedIndex = SlashList.SelectedIndex <= 0 ? SlashList.Items.Count - 1 : SlashList.SelectedIndex - 1;
                    SlashList.ScrollIntoView(SlashList.SelectedItem);
                    e.Handled = true;
                    return;
                case Key.Tab:
                    AcceptSlashHint();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    SlashPopup.IsOpen = false;
                    e.Handled = true;
                    return;
            }
        }

        switch (e.Key)
        {
            case Key.Enter when modifiers == ModifierKeys.Control && _gate is not null:
                Answer(true);
                e.Handled = true;
                break;
            case Key.Enter when modifiers == ModifierKeys.Control:
                // Ctrl+Enter any time: on this chat's tasks, after whatever is there.
                AddToTasks();
                e.Handled = true;
                break;
            case Key.Enter when modifiers == ModifierKeys.None:
                // With the hint list open, Enter picks the highlighted command.
                if (SlashPopup.IsOpen && SlashList.SelectedItem is not null && !Composer.Text.Contains(' '))
                    AcceptSlashHint();
                else
                    Send();
                e.Handled = true;
                break;
            case Key.Escape when _gate is not null:
                Answer(false);
                e.Handled = true;
                break;
            case Key.V when modifiers == ModifierKeys.Control:
                if (PasteAttachments()) e.Handled = true;
                break;
            case Key.Up when modifiers == ModifierKeys.None && Composer.Text.Length == 0:
                // Recall the last thing you sent, like a shell (not a huge one: that is what the chip is for).
                if (_session?.Entries.OfType<MessageEntryVM>().LastOrDefault(m => m.IsUser) is { } last && !PastedText.IsLarge(last.Text))
                {
                    Composer.Text = last.Text;
                    Composer.CaretIndex = Composer.Text.Length;
                    e.Handled = true;
                }
                break;
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e) => Send();

    private void Send()
    {
        if (_session is not { } session) return;
        var text = Composer.Text.Trim();
        // While the agent works, Enter queues what you typed on this chat's tasks, with its files and large pastes: it starts
        // by itself after.
        if (session.Running && (text.Length > 0 || _attachments.Count > 0))
        {
            AddToTasks();
            return;
        }
        if ((text.Length == 0 && _attachments.Count == 0) || session.Running) return;
        var payload = _attachments.Select(c => c.Attachment).ToList();
        Composer.Clear();
        _attachments.Clear();
        SlashPopup.IsOpen = false;
        _pinned = true;
        _model.Send(text, session, payload);
        ScrollIfPinned();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_session is { } session) _model.Host.StopSession(session.Id);
    }

    private void AddTask_Click(object sender, RoutedEventArgs e) => AddToTasks();

    /// <summary>"Add to tasks": what is typed — and the files, pictures and large pastes in the box — goes on this chat's
    /// tasks, after whatever is there, and runs as the chat's next prompt once the work before it is done (at once when the
    /// chat is free).</summary>
    private void AddToTasks()
    {
        if (_session is not { } session) return;
        var text = Composer.Text.Trim();
        if (text.Length == 0 && _attachments.Count == 0) return;
        var payload = _attachments.Select(c => c.Attachment).ToList();
        if (_model.Host.AddToTasks(session.Id, text, attachments: payload) is null) return;
        _attachments.Clear();
        Composer.Clear();
        SlashPopup.IsOpen = false;
        _pinned = true;
        ScrollIfPinned();
    }

    /// <summary>Replace the draft with <paramref name="text"/> and attach <paramref name="files"/> (Ludworks Browser's "Send
    /// to AI"); nothing is sent.</summary>
    public void Prefill(string text, IReadOnlyList<string> files)
    {
        if (_session is null) return;
        Composer.Text = text;
        Composer.CaretIndex = Composer.Text.Length;
        foreach (var path in files) Stage(path);
        FocusComposer();
    }

    /// <summary>Drop <paramref name="text"/> into the composer (the Prompts timeline's "Add to composer"), after what is there.</summary>
    public void InsertText(string text)
    {
        if (Composer.Text.Length > 0 && !Composer.Text.EndsWith('\n')) Composer.Text += "\n\n";
        Composer.Text += text;
        FocusComposer();
    }

    /// <summary>Scroll the transcript to an entry and flash it (the Prompts timeline's "Jump to it"): the windowed transcript
    /// moves its window to the entry when it is outside (see <see cref="ShowEntry"/>). False when the chat no longer has it.</summary>
    public bool ScrollToEntry(string entryId) => ShowEntry(entryId);

    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Suggestion suggestion || _session is not { } session) return;
        if (suggestion.OpensFolder)
        {
            _model.ChooseProject();
            return;
        }
        _model.Send(suggestion.Text, session);
    }

    private void InsertMention(string path)
    {
        var mention = "@" + (path.Contains(' ') ? $"\"{path}\"" : path) + " ";
        if (Composer.Text.Length > 0 && !Composer.Text.EndsWith(' ')) Composer.Text += " ";
        Composer.Text += mention;
        _model.Code.ShowChat = true;
        FocusComposer();
    }

    // MARK: - Slash hints

    /// <summary>Commands and skills matching what's typed, while the draft is a bare "/word".</summary>
    private void UpdateSlashHints()
    {
        var text = Composer.Text.Trim().ToLowerInvariant();
        if (_session is null || !text.StartsWith('/') || text.Contains(' ') || text.Contains('\n') || text.Length >= 24 || !Composer.IsKeyboardFocused)
        {
            SlashPopup.IsOpen = false;
            return;
        }
        if (_slashSkillsRevision != _model.Host.SkillsRevision || _slashSkills.Count == 0)
        {
            _slashSkillsRevision = _model.Host.SkillsRevision;
            _slashSkills = _model.Host.Skills(_session);
        }
        var disabled = _model.Config.DisabledSkills;
        var matches = SlashCommand.Catalog.Where(c => c.Usage.StartsWith(text, StringComparison.Ordinal)).ToList();
        matches.AddRange(_slashSkills
            .Where(s => s.UserInvocable && !s.Shadowed && !disabled.Contains(s.Id) && ("/" + s.Slug).StartsWith(text, StringComparison.Ordinal))
            .Take(8)
            .Select(s => new SlashCommand.Info("/" + s.Slug + (s.ArgumentHint is { } hint ? " " + hint : ""),
                $"{(s.Kind == SkillKind.Skill ? "Skill" : s.Kind.Label())}: {TextUtil.Prefix(s.Description, 70)}")));
        SlashList.ItemsSource = matches;
        SlashList.SelectedIndex = matches.Count > 0 ? 0 : -1;
        SlashPopup.IsOpen = matches.Count > 0;
    }

    private void AcceptSlashHint()
    {
        if (SlashList.SelectedItem is not SlashCommand.Info info) return;
        var word = info.Usage.Split(' ')[0];
        Composer.Text = word + " ";
        Composer.CaretIndex = Composer.Text.Length;
        SlashPopup.IsOpen = false;
    }

    private void SlashList_Click(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is SlashCommand.Info info)
        {
            SlashList.SelectedItem = info;
            AcceptSlashHint();
            FocusComposer();
        }
    }

    // MARK: - Gate

    private void Allow_Click(object sender, RoutedEventArgs e) => Answer(true);
    private void Deny_Click(object sender, RoutedEventArgs e) => Answer(false);

    private void Answer(bool allow)
    {
        if (_gate is null || _session is null) return;
        _model.Host.AnswerGate(_session.Id, _gate.Id, allow);
        FocusComposer();
    }

    // MARK: - Menus

    private void ModelMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_model.Spark.Boxes.Any(b => b.Status is null)) _ = _model.Spark.RefreshAsync();
        Menus.Open(Menus.Model(_model, _session), ModelMenuButton, PlacementMode.Top);
    }

    private void Thinking_Click(object sender, RoutedEventArgs e)
    {
        if (_session is { } session) Menus.Open(Menus.Thinking(_model, session), ThinkingButton, PlacementMode.Top);
    }

    private void Skills_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not { } session) return;
        SkillsPopup.Child = new SkillPickerView(_model, session, () => SkillsPopup.IsOpen = false);
        SkillsPopup.Closed += OnSkillsClosed;
        SkillsPopup.IsOpen = true;
    }

    private void OnSkillsClosed(object? sender, EventArgs e)
    {
        SkillsPopup.Closed -= OnSkillsClosed;
        SkillsPopup.Child = null;
        UpdateSkillsButton();
    }

    // MARK: - Attachments

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Attach files",
            Multiselect = true,
            Filter = "All files|*.*|Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|Text and code|*.txt;*.md;*.cs;*.py;*.js;*.ts;*.json;*.log",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        foreach (var path in dialog.FileNames) Stage(path);
        FocusComposer();
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ComposerChip chip) _attachments.Remove(chip);
    }

    /// <summary>Read a file and queue it; images are re-encoded to PNG so any vision model can read them.</summary>
    private void Stage(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                InsertMention(_model.Code.RelativePath(path));
                return;
            }
            var info = new FileInfo(path);
            if (!info.Exists) return;
            if (info.Length > MaxAttachmentBytes)
            {
                _session?.Note($"{info.Name} is too large to attach ({info.Length / 1024 / 1024} MB). Mention its path instead so the agent can read the part it needs.");
                return;
            }
            var data = File.ReadAllBytes(path);
            if (FileFilter.IsImage(path) && ImageTools.ToAttachmentPng(data) is { } png)
            {
                Add(new MessageAttachment(AttachmentKind.Image, Path.ChangeExtension(info.Name, ".png"), png));
            }
            else
            {
                Add(new MessageAttachment(AttachmentKind.File, info.Name, data));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _session?.Note($"Couldn't attach {Path.GetFileName(path)}: {ex.Message}", MessageRole.Error);
        }
    }

    private void Add(MessageAttachment attachment)
    {
        if (_attachments.Any(c => c.Name == attachment.Name && c.Attachment.Data.AsSpan().SequenceEqual(attachment.Data))) return;
        var chip = new ComposerChip(attachment);
        _attachments.Add(chip);
        _ = ReadAsync(chip);
    }

    /// <summary>Read what is in a file off the UI thread (a PDF page by page, an Office file from its XML, a picture with
    /// OCR), so the chip can say what will be sent and the message goes out without the wait.</summary>
    private async Task ReadAsync(ComposerChip chip)
    {
        if (chip.Attachment.Digest is not null)
        {
            RefreshChipDeliveries();
            return;
        }
        var original = chip.Attachment;
        var digested = await Task.Run(() => AttachmentReader.Digested(original));
        // Edited or removed meanwhile: the newer state stands.
        if (!ReferenceEquals(chip.Attachment, original)) return;
        chip.Attachment = digested;
        RefreshChipDeliveries();
    }

    /// <summary>Each chip's "sent as …" for the route in use now, with the parts a long file will go in.</summary>
    private void RefreshChipDeliveries()
    {
        if (_attachments.Count == 0) return;
        // This chat's own model and window, as PrepareAttachmentsAsync will use them (not the app's primary).
        var profile = _model.Host.ChatRoute(_session);
        var route = _model.Host.AttachmentRouteFor(profile);
        var readable = _attachments.Count(c => c.Attachment.Kind == AttachmentKind.File && c.Attachment.Digest?.Readable == true);
        var budget = new AttachmentBudget(_model.Host.ContextLimit(profile), Math.Max(1, readable));
        foreach (var chip in _attachments)
        {
            if (chip.IsReading)
            {
                chip.Delivery = "";
                continue;
            }
            var parts = chip.Attachment.Kind == AttachmentKind.File ? chip.PartCount(budget) : 1;
            chip.Delivery = AttachmentWire.Delivery(chip.Attachment, route, parts).Label();
        }
    }

    // MARK: - Large pastes

    /// <summary>A paste over 400 lines or 20,000 characters goes in as a "Pasted text" chip, not as text in the box.</summary>
    private void OnComposerPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText, autoConvert: true)) return;
        if (e.DataObject.GetData(DataFormats.UnicodeText, autoConvert: true) is not string text || !PastedText.IsLarge(text)) return;
        e.CancelCommand();
        AddPasted(text);
    }

    private void OnComposerDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.UnicodeText, autoConvert: true) is string text && PastedText.IsLarge(text))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnComposerDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.UnicodeText, autoConvert: true) is not string text || !PastedText.IsLarge(text)) return;
        e.Handled = true;
        AddPasted(text);
    }

    /// <summary>Queue a large block of text as an attachment.</summary>
    internal void AddPasted(string text)
    {
        var names = _attachments.Select(c => c.Name).Concat(SentNames());
        Add(PastedText.Attachment(text, names));
        FocusComposer();
    }

    /// <summary>Names of the attachments this chat already sent, so a new paste gets a name of its own.</summary>
    private IEnumerable<string> SentNames() =>
        _session?.Entries.OfType<MessageEntryVM>().SelectMany(m => m.Attachments).Select(a => a.Name) ?? [];

    private void EditPasted_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ComposerChip { IsPasted: true } chip) return;
        var current = chip.Attachment.Digest?.Text ?? TextUtil.Utf8NoBom.GetString(chip.Attachment.Data);
        if (TextViewerWindow.Edit(Window.GetWindow(this), "Pasted text", current) is not { } edited || edited == current) return;
        if (edited.Trim().Length == 0)
        {
            _attachments.Remove(chip);
            return;
        }
        chip.Attachment = AttachmentReader.Pasted(edited, chip.Name);
        RefreshChipDeliveries();
    }

    private void ShowAllText_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MessageEntryVM message)
            TextViewerWindow.View(Window.GetWindow(this), $"Your message · {AttachmentFormat.Count(AttachmentFormat.LineCount(message.Text), "line", "lines")}", message.Text);
    }

    /// <summary>Ctrl+V with an image or files on the clipboard attaches them instead of pasting text.</summary>
    private bool PasteAttachments()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                foreach (var path in Clipboard.GetFileDropList().Cast<string>()) Stage(path);
                return true;
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image && ImageTools.ToAttachmentPng(image) is { } png)
            {
                var index = _attachments.Count(a => a.Name.StartsWith("pasted-image", StringComparison.Ordinal)) + 1;
                Add(new MessageAttachment(AttachmentKind.Image, $"pasted-image-{index}.png", png));
                return true;
            }
        }
        catch (ExternalException)
        {
            // Another app holds the clipboard; fall through to a normal paste.
        }
        return false;
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        DropHint.Visibility = Visibility.Visible;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && _session is { Running: false } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        DropHint.Visibility = Visibility.Collapsed;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        DropHint.Visibility = Visibility.Collapsed;
        if (_session is not { Running: false } || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        foreach (var path in paths) Stage(path);
        FocusComposer();
    }

    // MARK: - Transcript interactions

    private void ToolHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ToolEntryVM { HasOutput: true, IsFinished: true } tool)
        {
            _pinned = false;
            tool.IsExpanded = !tool.IsExpanded;
        }
    }

    private void Compaction_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CompactionEntryVM note)
        {
            _pinned = false;
            note.IsExpanded = !note.IsExpanded;
        }
    }

    private void ToolImage_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BitmapSource image) ImageViewerWindow.Show(Window.GetWindow(this), image);
    }

    private static ToolEntryVM? ToolOf(object sender) => (sender as FrameworkElement)?.DataContext as ToolEntryVM;

    private void CopyToolOutput_Click(object sender, RoutedEventArgs e) => Copy(ToolOf(sender)?.Output);
    private void CopyToolPreview_Click(object sender, RoutedEventArgs e) => Copy(ToolOf(sender)?.Preview);

    private void CopyCompaction_Click(object sender, RoutedEventArgs e) =>
        Copy(((sender as FrameworkElement)?.DataContext as CompactionEntryVM)?.Summary);

    private static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch (Exception) { }
    }

    /// <summary>The file a read/write/edit card is about, resolved against the chat's project.</summary>
    private string? ToolPath(ToolEntryVM? tool)
    {
        if (tool is null || tool.Name is not ("read_file" or "write_file" or "edit") || tool.Preview.Length == 0) return null;
        var preview = tool.Preview.Trim().Trim('"');
        try
        {
            if (Path.IsPathRooted(preview)) return Path.GetFullPath(preview);
            // (A chat without a project works in its scratch folder.)
            return _session?.WorkingFolder is { } root ? Path.GetFullPath(Path.Combine(root, preview)) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private void OpenToolFile_Click(object sender, RoutedEventArgs e)
    {
        if (ToolPath(ToolOf(sender)) is { } path) Reveal(path);
    }

    private void RevealToolFile_Click(object sender, RoutedEventArgs e)
    {
        if (ToolPath(ToolOf(sender)) is { } path) ShellIntegration.RevealInExplorer(path);
    }

    private void Reveal(string path)
    {
        _model.Mode = WorkspaceMode.Code;
        if (_model.Project is null && _session?.WorkspacePath is { } root) _model.OpenProject(root, activateSession: false);
        _model.Code.Reveal(path);
    }

    private static ChangedFileChip? ChipOf(object sender) => (sender as FrameworkElement)?.DataContext as ChangedFileChip;

    private void ChangedFile_Click(object sender, RoutedEventArgs e)
    {
        if (ChipOf(sender) is { } chip) Reveal(chip.Path);
    }

    private void RevealChangedFile_Click(object sender, RoutedEventArgs e)
    {
        if (ChipOf(sender) is { } chip) ShellIntegration.RevealInExplorer(chip.Path);
    }

    private void CopyChangedFile_Click(object sender, RoutedEventArgs e) => Copy(ChipOf(sender)?.Path);

    private void MentionChangedFile_Click(object sender, RoutedEventArgs e)
    {
        if (ChipOf(sender) is { } chip) InsertMention(_model.Code.RelativePath(chip.Path));
    }
}

/// <summary>An opener on an empty chat.</summary>
public sealed record Suggestion(string Glyph, string Text, bool OpensFolder = false);

/// <summary>A changed-file chip under the transcript.</summary>
public sealed record ChangedFileChip(string Path, string Name, Brush Tint, string Tooltip);

/// <summary>The "Working… / Thinking… 12s" row at the end of the transcript.</summary>
public sealed partial class ThinkingFooter : ObservableObject
{
    private SessionVM? _session;
    private readonly DispatcherTimer _timer;
    private double _phase;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _headline = "Working…";
    [ObservableProperty] private string? _tail;
    [ObservableProperty] private double _pulse = 1;

    public ThinkingFooter()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher.CurrentDispatcher);
    }

    public SessionVM? Session
    {
        get => _session;
        set
        {
            if (_session is not null)
            {
                _session.PropertyChanged -= OnChanged;
                _session.Entries.CollectionChanged -= OnEntries;
            }
            _session = value;
            if (_session is not null)
            {
                _session.PropertyChanged += OnChanged;
                _session.Entries.CollectionChanged += OnEntries;
            }
            Refresh();
        }
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionVM.Running) or nameof(SessionVM.Stopping) or nameof(SessionVM.Activity)
            or nameof(SessionVM.Reasoning) or nameof(SessionVM.RunningTool) or nameof(SessionVM.Retry))
            Refresh();
    }

    private void OnEntries(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var session = _session;
        // A running tool card already shows its own spinner.
        IsVisible = session is { Running: true } && session.RunningTool is null;
        if (IsVisible) _timer.Start(); else _timer.Stop();
        UpdateText();
    }

    private void Tick()
    {
        _phase += 0.2;
        Pulse = 0.35 + 0.65 * Math.Abs(Math.Sin(_phase));
        UpdateText();
    }

    private void UpdateText()
    {
        if (_session is not { } session) return;
        if (session.Stopping) Headline = "Stopping…";
        else if (session.Retry is { } retry)
        {
            var wait = (int)Math.Ceiling((retry.NextAttempt - DateTimeOffset.Now).TotalSeconds);
            var when = wait > 0 ? $"retrying in {wait}s" : "retrying now";
            Headline = $"Model unavailable ({retry.Reason}) — {when} · attempt {retry.Attempt + 1}. Stop to give up.";
        }
        else if (session.Activity is { } activity) Headline = activity;
        else if (session.Reasoning.Length > 0 && session.ReasoningStarted is { } started)
            Headline = $"Thinking… {(int)(DateTimeOffset.Now - started).TotalSeconds}s";
        else Headline = "Working…";
        var reasoning = session.Reasoning;
        Tail = reasoning.Length == 0 ? null : TextUtil.Suffix(reasoning, 400).Replace('\n', ' ').Trim();
    }
}

/// <summary>The "Files changed" chips at the end of the transcript.</summary>
public sealed partial class FilesFooter : ObservableObject
{
    private SessionVM? _session;

    public ObservableCollection<ChangedFileChip> Files { get; } = [];
    [ObservableProperty] private bool _isVisible;

    public SessionVM? Session
    {
        get => _session;
        set
        {
            if (_session is not null) _session.ChangedFiles.CollectionChanged -= OnChanged;
            _session = value;
            if (_session is not null) _session.ChangedFiles.CollectionChanged += OnChanged;
            Rebuild();
        }
    }

    private void OnChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        Files.Clear();
        if (_session is { } session)
        {
            foreach (var change in session.ChangedFiles)
            {
                var name = session.WorkspacePath is { } root && change.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    ? Path.GetRelativePath(root, change.Path)
                    : Path.GetFileName(change.Path);
                var (key, fallback, label) = change.Kind switch
                {
                    FileChangeKind.Created => ("SystemFillColorSuccessBrush", Colors.SeaGreen, "created"),
                    FileChangeKind.Deleted => ("SystemFillColorCriticalBrush", Colors.IndianRed, "deleted"),
                    _ => ("SystemFillColorCautionBrush", Colors.DarkOrange, "modified"),
                };
                Files.Add(new ChangedFileChip(change.Path, name, ThemeService.Brush(key, fallback), $"{change.Path} — {label}"));
            }
        }
        IsVisible = Files.Count > 0;
    }
}
