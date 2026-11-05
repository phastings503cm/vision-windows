using System.IO;
using System.Windows;
using Vision.App.Model;
using Vision.Core;

namespace Vision.App;

// MARK: - A chat another app starts
//
// A request dropped in the dispatch folder, the way UltronMail does, through the running app: the catalog goes up, the chat starts in
// the background on the model it names, and the answer is written. The route points nowhere, so nothing is sent anywhere.

public sealed partial class SelfTest
{
    private async Task CaptureDispatch(Window window, AppModel model)
    {
        var host = model.Host;
        var inbox = new DispatchInbox(AppPaths.Root);
        var route = model.Config.ActiveRoute ?? throw new InvalidOperationException("no primary route");
        var selected = host.SelectedId;
        var id = Guid.NewGuid().ToString();
        inbox.Prepare();
        File.WriteAllText(Path.Combine(inbox.Inbox, id + ".json"), DispatchInbox.Encode(new DispatchRequest
        {
            Version = 1,
            Id = id,
            Source = "UltronMail",
            Title = "Email: Q3 budget",
            Instructions = "Draft a reply for me to review.",
            Context = "<untrusted_email>\nFrom: Dana\nSubject: Q3 budget\n\nCan you look at the numbers?\n</untrusted_email>",
            Model = route,
            CreatedAt = DateTimeOffset.UtcNow,
        }));

        await host.PumpDispatchAsync(inbox);
        await Settle(600);

        var vm = host.Sessions.FirstOrDefault(s => s.Title == "Email: Q3 budget") ?? throw new InvalidOperationException("the chat didn't start");
        try
        {
            if (vm.PinnedRoute != route) throw new InvalidOperationException("the chat isn't on the model it was sent to");
            if (host.SelectedId != selected) throw new InvalidOperationException("the chat took over the screen");
            if (vm.Preset == PermissionPreset.FullAccess) throw new InvalidOperationException("the chat started with full access");
            var result = File.ReadAllText(Path.Combine(inbox.Results, id + ".json"));
            if (!result.Contains("\"started\":true", StringComparison.Ordinal)) throw new InvalidOperationException("the result says: " + result);
            if (!File.ReadAllText(inbox.CatalogFile).Contains(route, StringComparison.Ordinal)) throw new InvalidOperationException("the catalog doesn't list the model");
            Note("dispatch: " + result);
            Capture(window, "dispatch-sidebar");
        }
        finally
        {
            host.StopSession(vm.Id);
            await Settle(200);
            host.DeleteSession(vm.Id);
        }
    }
}
