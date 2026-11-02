using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Vision.App.Infrastructure;
using Vision.App.Model;
using Vision.App.Views;
using Vision.App.Views.Dialogs;
using Vision.Core;

namespace Vision.App;

public partial class App : Application
{
    private SingleInstance? _instance;

    /// <summary>The app's root state, for views that are not handed it directly.</summary>
    public static AppModel Model { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // This process is Vision, whatever its executable is called (a renamed portable copy): no machine tool clicks or
        // types into its own windows.
        Vision.Windows.TerminalGuard.SelfIsHarness = true;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashLog(args.ExceptionObject as Exception, "domain");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception, "task");
            args.SetObserved();
        };

        var args = e.Args;

        // Vision.exe --write-recovery-usb <job.json>: the elevated helper the DGX Spark guide starts to
        // erase and write a recovery USB stick. It shows no window and exits when the job is done, so
        // the main app never needs administrator rights.
        if (RecoveryUsbWriter.TryRunHelper(args) is { } helperExit)
        {
            Shutdown(helperExit);
            return;
        }

        // Vision.exe --migrate-only [<report.json>]: bring DSH's data and keys over as the first start would, report
        // what reads back, and exit without a window (scripts that upgrade many PCs; the CI upgrade test).
        if (LegacyUpgrade.ParseMigrateOnly(args) is { } migrateReport)
        {
            Shutdown(LegacyUpgrade.RunMigrateOnly(migrateReport));
            return;
        }

        var selfTest = SelfTest.Parse(args);

        // One Vision per user: a second launch ("Open with Vision" on a folder) hands its folder over.
        if (selfTest is null)
        {
            _instance = new SingleInstance(SingleInstance.AppId);
            if (!_instance.IsFirst && _instance.Forward(args.Length == 0 ? ["--activate"] : args.Select(a => a.StartsWith("--", StringComparison.Ordinal) ? a : Path.GetFullPath(a)).ToArray()))
            {
                Shutdown();
                return;
            }
        }

        if (selfTest is not null)
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeVariable, selfTest.Home);
            selfTest.Prepare();
        }
        else if (!BringLegacyDataOver(_instance!.IsFirst))
        {
            Shutdown();
            return;
        }
        FontResolver.Apply(Resources);
        // Deleting a skill is recoverable, like everything else deleted from the app.
        SkillManager.RecycleBin = ShellIntegration.MoveToRecycleBin;
        PlainChat.RecycleBin = ShellIntegration.MoveToRecycleBin;
        // Bedrock routes sign requests with the AWS CLI's sign-in.
        AwsAccounts.Install();
        // What servers refused before (a temperature, a thinking setting) is remembered across launches.
        RequestAdaptationStore.Shared.FilePath = AppPaths.RequestAdaptations;
        // Text in attached pictures and scanned PDFs, where Windows has an OCR language for the user (made off the UI thread:
        // the first call loads the recognizer).
        _ = Task.Run(() =>
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) && WindowsOcr.TryCreate() is { } ocr) AttachmentReader.Ocr = ocr;
        });
        var config = new AppConfig();
        if (selfTest is null)
        {
            try { config.BringLegacySecretsOver(); } catch (Exception ex) { WriteCrashLog(ex, "secrets"); }
        }
        ThemeService.Instance.Apply(selfTest?.Theme ?? config.Theme);
        var log = new ConversationLog();
        Model = new AppModel(config, log, Dispatcher);
        // The screen edges glow while a chat uses the computer; Ctrl+Alt+. stops it from any app.
        MachineGlowController.Install(Model, Dispatcher);

        var window = new MainWindow(Model);
        MainWindow = window;

        if (selfTest is not null)
        {
            selfTest.Run(window, Model);
            return;
        }

        window.Show();
        Model.Start();

        OpenArguments(args);

        _instance?.Listen(forwarded => Dispatcher.BeginInvoke(() => HandleForwarded(forwarded)));

        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, ShowDataFolderNotice);
        if (Model.NeedsSetup)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => window.ShowWizard()); // offers the import once it closes
        else
            _ = window.OfferExternalImportAsync();
        if (config.CheckForUpdates) _ = CheckForUpdatesAsync();
    }

    // MARK: - DSH is now Vision

    /// <summary>Set when bringing DSH's data over failed this launch (the app runs on DSH's folder meanwhile).</summary>
    private DataMigrationOutcome? _dataFolderProblem;

    /// <summary>Vision was called DSH before 0.9: bring DSH's data folder over, once, before anything opens a file
    /// (see <see cref="DataFolderMigration"/>). False when the app must not start (the user chose to quit, or a move
    /// stopped half-way and the data must not be split across two folders).</summary>
    private bool BringLegacyDataOver(bool firstInstance)
    {
        if (LegacyUpgrade.Pending() is not { } migration)
        {
            // Nothing to bring (VISION_HOME or DSH_HOME names the folder, or it was brought over already) — but the
            // folder may still be one DSH works in (DSH_HOME, a portable copy unpacked into DSH's folder), and DSH runs
            // under its own single-instance name: both open at once would overwrite each other's chats and settings.
            if (!AppPaths.MayShareWithLegacyApp()) return true;
            return WhileLegacyAppRuns(() => Dialog.Ask("Close DSH first",
                $"DSH is still open, and it works in the same data folder as Vision ({AppPaths.Root}). Close it (check the " +
                "taskbar and the notification area), then choose Try again, so the two don't overwrite each other's chats and settings.",
                new Dialog.Choice("Quit", IsCancel: true), new Dialog.Choice("Try again", IsDefault: true)) == 1);
        }
        // Another Vision is starting up and is bringing it over right now; the user will see that one.
        if (!firstInstance) return false;

        // An old DSH still open keeps writing to the folder being brought over.
        if (!WhileLegacyAppRuns(() => Dialog.Ask("Close DSH first",
                "DSH is now Vision. DSH is still open, though — close it (check the taskbar and the notification area), " +
                "then choose Try again, so Vision can bring your chats, settings and keys over.",
                new Dialog.Choice("Quit", IsCancel: true), new Dialog.Choice("Try again", IsDefault: true)) == 1))
            return false;

        var outcome = LegacyUpgrade.Run(migration);
        if (outcome.Result != DataMigrationResult.Failed) return true;
        if (outcome.DataFolder is null)
        {
            WithDialogBeforeTheWindow(() => Dialog.Info("Vision couldn't finish moving your data", outcome.Problem ?? ""));
            return false;
        }
        // Nothing moved: run on DSH's folder this time, and try again at the next start.
        AppPaths.SessionRoot = outcome.DataFolder;
        _dataFolderProblem = outcome;
        return true;
    }

    /// <summary>While an old DSH is running, ask <paramref name="tryAgain"/> (a dialog: true for Try again). False when
    /// the user chose to quit instead.</summary>
    private bool WhileLegacyAppRuns(Func<bool> tryAgain) => WithDialogBeforeTheWindow(() =>
    {
        while (SingleInstance.IsRunning(SingleInstance.LegacyAppId))
        {
            if (!tryAgain()) return false;
        }
        return true;
    });

    /// <summary>Show a dialog before the main window exists: closing it mustn't end the app, and it mustn't stay the
    /// main window.</summary>
    private T WithDialogBeforeTheWindow<T>(Func<T> show)
    {
        var previousMode = ShutdownMode;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            return show();
        }
        finally
        {
            ShutdownMode = previousMode;
            MainWindow = null; // the real one is set next
        }
    }

    private void WithDialogBeforeTheWindow(Action show) => WithDialogBeforeTheWindow(() =>
    {
        show();
        return true;
    });

    /// <summary>The one-time notice after DSH's data was brought over — or why it wasn't, this time.</summary>
    private void ShowDataFolderNotice()
    {
        try
        {
            if (_dataFolderProblem is { } failed)
            {
                Dialog.Info("Your DSH data stays where it is for now",
                    $"Vision couldn't bring DSH's data folder over ({failed.Problem}). It is working in {failed.DataFolder} " +
                    "this time — nothing is missing — and tries again the next time it starts.");
                return;
            }
            if (DataFolderMigration.PendingNotice(AppPaths.Root) is not { } done) return;
            var message = "DSH is now Vision — your chats, settings and keys came with you.";
            if (done.LegacyLeft)
                message += $"\n\nYour old data folder ({done.From}) was copied rather than moved, so it is still there. Once you've " +
                           "checked everything is here, you can delete it.";
            if (done.SkippedLinks is { Count: > 0 } links)
                message += $"\n\nThese links in it weren't recreated (they are still in the old folder): {string.Join(", ", links)}.";
            if (done.SetAside is { Length: > 0 } setAside)
                message += $"\n\nA Vision data folder was already at {AppPaths.Root}. It was kept as {setAside} — nothing in it was " +
                           "deleted, but its chats and settings aren't the ones Vision shows now.";
            Dialog.Info("DSH is now Vision", message);
            DataFolderMigration.MarkNoticeShown(AppPaths.Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The notice is a courtesy; it may show again next time.
        }
    }

    private void HandleForwarded(string[] args)
    {
        if (MainWindow is MainWindow window) window.BringToFront();
        OpenArguments(args);
    }

    /// <summary>What a launch asks for, first or forwarded: a folder to open as the project, and maybe a request from
    /// Ludworks Browser to start a chat in it.</summary>
    private void OpenArguments(string[] args)
    {
        var folder = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (LudworksRequest.FindArgument(args) is { } request)
        {
            OpenLudworksRequest(request, folder);
            return;
        }
        if (folder is not null && Directory.Exists(folder)) Model.OpenProject(folder);
    }

    /// <summary>Ludworks Browser's "Send to AI": a new chat in the handoff folder with the prompt in the composer and the
    /// page's screenshot attached. The user sends it.</summary>
    private void OpenLudworksRequest(string file, string? folder)
    {
        var (request, problem) = LudworksRequest.Load(file);
        if (request is null)
        {
            Model.Host.Banner = $"Vision couldn't open what Ludworks Browser sent. {problem}";
            if (folder is not null && Directory.Exists(folder)) Model.OpenProject(folder);
            return;
        }
        // The folder the browser named, or the one the request is in.
        var project = folder is not null && Directory.Exists(folder) ? folder : Path.GetDirectoryName(Path.GetFullPath(file))!;
        Model.OpenProject(project, activateSession: false);
        Model.NewChat();
        if (MainWindow is not MainWindow window) return;
        var screenshot = request.ScreenshotToAttach();
        window.PrefillComposer(request.Prompt, screenshot is null ? [] : [screenshot]);
        window.BringToFront();
    }

    private static async Task CheckForUpdatesAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (await UpdateChecker.CheckAsync() is { } release)
            Model.Update = new AppModel.UpdateInfo(release.Tag, release.Url);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // Leaves each task list's "resume on launch" alone: quitting mid-list picks it back up.
            Model?.Shutdown();
            // Programs the agent started (game engines, dev servers) must not outlive the app that
            // owns their consoles.
            ProcessManager.Shared.StopAll();
            Model?.Config.SaveNow();
            Model?.Spark.Dispose();
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex, "exit");
        }
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = WriteCrashLog(e.Exception, "ui");
        e.Handled = true;
        if (SelfTest.Current is { } test)
        {
            test.Fail("unhandled", e.Exception);
            return;
        }
        // Keep going: a failed redraw should not cost the user their chats. The log says what broke.
        try
        {
            Dialog.Info("Something went wrong",
                $"Vision hit an unexpected error and recovered. Details were saved to:\n{path}\n\n{e.Exception.GetBaseException().Message}");
        }
        catch (Exception)
        {
            // The dialog itself failed; the log is written.
        }
    }

    /// <summary>Write a crash report to %APPDATA%\Vision\logs and return its path.</summary>
    public static string? WriteCrashLog(Exception? exception, string source)
    {
        if (exception is null) return null;
        try
        {
            var directory = AppPaths.Ensure(AppPaths.Logs);
            var path = Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{source}.txt");
            var text = new StringBuilder()
                .AppendLine($"Vision for Windows {AppInfo.Version}")
                .AppendLine($"{Environment.OSVersion} · .NET {Environment.Version} · {(Environment.Is64BitProcess ? "64" : "32")}-bit")
                .AppendLine($"{DateTimeOffset.Now:O} ({source})")
                .AppendLine()
                .AppendLine(exception.ToString())
                .ToString();
            File.WriteAllText(path, text);
            // Keep the folder from growing without bound.
            foreach (var old in new DirectoryInfo(directory).GetFiles("crash-*.txt").OrderByDescending(f => f.CreationTimeUtc).Skip(20))
                old.Delete();
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
