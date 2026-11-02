using System.Text.Json;

namespace Vision.Core;

// MARK: - Ludworks Browser's "Send to AI"
//
// Ludworks Browser hands a page to Vision by writing request.json (and request.md) into a folder under
// %TEMP%\Ludworks Browser handoffs\ and starting Vision.exe <folder> --ludworks-request=<folder>\request.json.
// Vision opens the folder as a project, starts a new chat in it and puts the request's prompt in the composer with
// the page's screenshot attached; the user sends it. Only files under the handoff folder are read: the argument
// comes from whoever started Vision, and nothing outside that folder is the browser's to hand over.

/// <summary>The page that was sent: its title and address.</summary>
public sealed record LudworksPage(string Title, string Url);

/// <summary>Where the browser saved a snapshot of the page (paths are absolute, each under the handoff folder).</summary>
public sealed record LudworksSnapshot(string Dir, string? Screenshot, string? Html, string? Assets, string? Text);

/// <summary>The tab the user lets the app control, and how to reach the browser's agent tools.</summary>
public sealed record LudworksControl(long TabId, string Client, string? Cli, bool Mcp);

/// <summary>A request.json from Ludworks Browser (desktop/src/main/agent/send.js writes it).</summary>
public sealed record LudworksRequest(
    string App,
    string? Version,
    DateTimeOffset? Created,
    string Harness,
    string Narrative,
    LudworksPage Page,
    string Mode,
    LudworksSnapshot? Snapshot,
    LudworksControl? Control,
    string Prompt)
{
    /// <summary>The command-line argument that names the file: <c>--ludworks-request=&lt;path&gt;</c>.</summary>
    public const string Argument = "--ludworks-request=";

    /// <summary>The <c>app</c> every request names.</summary>
    public const string AppName = "Ludworks Browser";

    /// <summary>The folder under %TEMP% the browser writes handoffs into.</summary>
    public const string HandoffFolderName = "Ludworks Browser handoffs";

    /// <summary>A request is a prompt and a few paths; anything bigger is not one.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    /// <summary>Longer than any prompt the browser writes (its page text goes in a file, not the prompt).</summary>
    public const int MaxPromptChars = 200_000;

    /// <summary>%TEMP%\Ludworks Browser handoffs.</summary>
    public static string DefaultHandoffRoot => Path.Combine(Path.GetTempPath(), HandoffFolderName);

    /// <summary>The screenshot to attach, when the request has one and it is still there, under
    /// <paramref name="handoffRoot"/> (<see cref="DefaultHandoffRoot"/> when null) with no link on the way.</summary>
    public string? ScreenshotToAttach(string? handoffRoot = null) =>
        Snapshot?.Screenshot is { } path && File.Exists(path) && IsInsideOnDisk(path, handoffRoot ?? DefaultHandoffRoot) ? path : null;

    /// <summary>The file named by the last <c>--ludworks-request=</c> argument; null when there is none (or it is empty).</summary>
    public static string? FindArgument(IEnumerable<string> args)
    {
        string? found = null;
        foreach (var arg in args)
        {
            if (!arg.StartsWith(Argument, StringComparison.OrdinalIgnoreCase)) continue;
            var value = arg[Argument.Length..].Trim().Trim('"');
            found = value.Length > 0 ? value : null;
        }
        return found;
    }

    /// <summary>Whether <paramref name="path"/> is an absolute path strictly inside <paramref name="root"/> once "..",
    /// "." and doubled separators are resolved (the root itself is not inside it). Text only: see
    /// <see cref="IsInsideOnDisk"/> for links.</summary>
    public static bool IsInside(string? path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(root)) return false;
        // \\?\ and \\.\ paths skip the normalization the comparison relies on.
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;
        string full, top;
        try
        {
            full = Path.GetFullPath(path);
            top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.Length > top.Length && full.StartsWith(top, comparison);
    }

    /// <summary><see cref="IsInside"/>, and no link (symbolic link or junction) between the root and the file that could
    /// lead outside it.</summary>
    public static bool IsInsideOnDisk(string? path, string root)
    {
        if (!IsInside(path, root)) return false;
        var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        try
        {
            if (new DirectoryInfo(top).LinkTarget is not null) return false;
            for (var current = Path.GetFullPath(path!); current.Length > top.Length; current = Path.GetDirectoryName(current)!)
            {
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.Exists && info.LinkTarget is not null) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Read and check the request at <paramref name="path"/>, which must be under <paramref name="handoffRoot"/>
    /// (<see cref="DefaultHandoffRoot"/> when null). The problem says what is wrong, in a sentence for the user.</summary>
    public static (LudworksRequest? Request, string? Problem) Load(string path, string? handoffRoot = null)
    {
        var root = handoffRoot ?? DefaultHandoffRoot;
        if (!IsInsideOnDisk(path, root)) return (null, $"Vision only opens requests from {root}, and {path} is not in it.");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return (null, $"{path} is no longer there.");
            if (info.Length > MaxFileBytes) return (null, $"{info.Name} is too large to be a request from {AppName}.");
            return Parse(File.ReadAllText(path), root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"Couldn't read {path}: {ex.Message}");
        }
    }

    /// <summary>Check a request's JSON. Every path in it must be under <paramref name="handoffRoot"/>.</summary>
    public static (LudworksRequest? Request, string? Problem) Parse(string json, string handoffRoot)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            return (Read(document.RootElement, handoffRoot), null);
        }
        catch (JsonException ex)
        {
            return (null, $"The request is not valid JSON ({ex.Message}).");
        }
        catch (FormatException ex)
        {
            return (null, ex.Message);
        }
    }

    private static LudworksRequest Read(JsonElement root, string handoffRoot)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("The request is not a JSON object.");
        var app = String(root, "app");
        if (app != AppName) throw new FormatException($"The request is not from {AppName}.");
        var prompt = String(root, "prompt");
        if (string.IsNullOrWhiteSpace(prompt)) throw new FormatException("The request has no prompt.");
        if (prompt.Length > MaxPromptChars) throw new FormatException("The request's prompt is too long.");

        if (!root.TryGetProperty("page", out var pageElement) || pageElement.ValueKind != JsonValueKind.Object)
            throw new FormatException("The request doesn't say which page it is about.");
        var url = String(pageElement, "url");
        if (string.IsNullOrWhiteSpace(url)) throw new FormatException("The request doesn't say which page it is about.");
        var page = new LudworksPage(String(pageElement, "title") ?? "", url);

        var mode = String(root, "mode");
        if (mode is not ("url" or "snapshot")) throw new FormatException($"The request's mode \"{mode}\" is not url or snapshot.");
        var snapshot = ReadSnapshot(root, handoffRoot);
        if (mode == "snapshot" && snapshot is null) throw new FormatException("The request is a snapshot without one.");

        DateTimeOffset? created = DateTimeOffset.TryParse(String(root, "created"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var when) ? when : null;
        return new LudworksRequest(app, String(root, "version"), created, String(root, "harness") ?? "", String(root, "narrative") ?? "",
            page, mode, snapshot, ReadControl(root), prompt);
    }

    private static LudworksSnapshot? ReadSnapshot(JsonElement root, string handoffRoot)
    {
        if (!root.TryGetProperty("snapshot", out var element) || element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException("The request's snapshot is not an object.");
        string? Inside(string name)
        {
            var path = String(element, name);
            if (string.IsNullOrEmpty(path)) return null;
            return IsInside(path, handoffRoot) ? Path.GetFullPath(path) : throw new FormatException($"The snapshot's {name} is outside {handoffRoot}.");
        }
        return new LudworksSnapshot(Inside("dir") ?? throw new FormatException("The request's snapshot has no folder."),
            Inside("screenshot"), Inside("html"), Inside("assets"), Inside("text"));
    }

    private static LudworksControl? ReadControl(JsonElement root)
    {
        if (!root.TryGetProperty("control", out var element) || element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("tabId", out var tab) || tab.ValueKind != JsonValueKind.Number || !tab.TryGetInt64(out var tabId) ||
            String(element, "client") is not { Length: > 0 } client)
            throw new FormatException("The request's control has no tab or client.");
        var mcp = element.TryGetProperty("mcp", out var m) && m.ValueKind == JsonValueKind.True;
        return new LudworksControl(tabId, client, String(element, "cli") is { Length: > 0 } cli ? cli : null, mcp);
    }

    /// <summary>A string property; null when missing or null, and a FormatException when it is another kind.</summary>
    private static string? String(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new FormatException($"The request's {name} is not text.");
    }
}
