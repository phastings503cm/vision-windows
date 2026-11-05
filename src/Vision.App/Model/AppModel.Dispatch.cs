using System.IO;
using System.Windows.Threading;
using Vision.Core;

namespace Vision.App.Model;

// Chats other apps hand over, through the dispatch folder in the data folder (DispatchInbox, AgentHost.Dispatch.cs).
public sealed partial class AppModel
{
    private DispatcherTimer? _dispatchTimer;
    private bool _dispatchBusy;

    /// <summary>Starts what other apps hand over, and keeps the list of what they can pick from up to date.</summary>
    private void StartDispatchWatch()
    {
        var inbox = new DispatchInbox(AppPaths.Root);
        try
        {
            inbox.Prepare();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        _ = Task.Run(() => inbox.Prune());
        _dispatchTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => _ = PumpDispatchAsync(inbox), Dispatcher.CurrentDispatcher);
        _dispatchTimer.Start();
        _ = PumpDispatchAsync(inbox);
    }

    private async Task PumpDispatchAsync(DispatchInbox inbox)
    {
        if (_dispatchBusy) return;
        _dispatchBusy = true;
        try
        {
            await Host.PumpDispatchAsync(inbox);
        }
        finally
        {
            _dispatchBusy = false;
        }
    }
}
