using System.Text.Json.Nodes;

namespace Vision.Core.Tests;

public sealed class LudworksRequestTests : IDisposable
{
    private readonly TempDirectory _temp = new("vision-ludworks");

    public void Dispose() => _temp.Dispose();

    /// <summary>The stand-in for %TEMP%\Ludworks Browser handoffs.</summary>
    private string Root => _temp[LudworksRequest.HandoffFolderName];

    private string Handoff => _temp[$"{LudworksRequest.HandoffFolderName}/1730000000000-example"];

    /// <summary>A request as Ludworks Browser's send.js writes it, for a snapshot with control.</summary>
    private JsonObject Request() => new()
    {
        ["app"] = "Ludworks Browser",
        ["version"] = "0.4.2",
        ["created"] = "2026-11-02T10:15:00.000Z",
        ["harness"] = "vision",
        ["narrative"] = "Why does the checkout button do nothing?",
        ["page"] = new JsonObject { ["title"] = "Checkout", ["url"] = "https://shop.example/checkout" },
        ["mode"] = "snapshot",
        ["snapshot"] = new JsonObject
        {
            ["dir"] = Handoff,
            ["screenshot"] = Path.Combine(Handoff, "screenshot.png"),
            ["html"] = Path.Combine(Handoff, "page.html"),
            ["assets"] = Path.Combine(Handoff, "page_files"),
            ["text"] = Path.Combine(Handoff, "page.txt"),
        },
        ["control"] = new JsonObject { ["tabId"] = 7, ["client"] = "vision", ["cli"] = @"C:\Program Files\Ludworks Browser\lwb.exe", ["mcp"] = false },
        ["prompt"] = "Why does the checkout button do nothing?\n\nThe page: Checkout — https://shop.example/checkout",
    };

    private string WriteRequest(JsonObject request, string? folder = null)
    {
        var dir = Directory.CreateDirectory(folder ?? Handoff).FullName;
        var path = Path.Combine(dir, "request.json");
        File.WriteAllText(path, request.ToJsonString());
        return path;
    }

    // MARK: - The argument

    [Fact]
    public void TheArgumentNamesTheFile()
    {
        Assert.Equal(@"C:\Temp\h\request.json",
            LudworksRequest.FindArgument([@"C:\Temp\h", @"--ludworks-request=C:\Temp\h\request.json"]));
        // Quoted by a shell that kept the quotes; the last one wins.
        Assert.Equal(@"C:\b.json", LudworksRequest.FindArgument([@"--ludworks-request=""C:\a.json""", @"--ludworks-request=""C:\b.json"""]));
        Assert.Null(LudworksRequest.FindArgument([@"C:\Temp\h", "--activate"]));
        Assert.Null(LudworksRequest.FindArgument(["--ludworks-request="]));
    }

    // MARK: - Parsing

    [Fact]
    public void ARequestFromTheBrowserReadsBack()
    {
        var (request, problem) = LudworksRequest.Load(WriteRequest(Request()), Root);

        Assert.Null(problem);
        Assert.NotNull(request);
        Assert.Equal("Ludworks Browser", request!.App);
        Assert.Equal("0.4.2", request.Version);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 10, 15, 0, TimeSpan.Zero), request.Created);
        Assert.Equal("vision", request.Harness);
        Assert.Equal("Why does the checkout button do nothing?", request.Narrative);
        Assert.Equal(new LudworksPage("Checkout", "https://shop.example/checkout"), request.Page);
        Assert.Equal("snapshot", request.Mode);
        Assert.Equal(Path.Combine(Handoff, "screenshot.png"), request.Snapshot!.Screenshot);
        Assert.Equal(Path.Combine(Handoff, "page_files"), request.Snapshot.Assets);
        Assert.Equal(new LudworksControl(7, "vision", @"C:\Program Files\Ludworks Browser\lwb.exe", false), request.Control);
        Assert.StartsWith("Why does the checkout button", request.Prompt);
    }

    [Fact]
    public void AUrlRequestNeedsNoSnapshotOrControl()
    {
        var json = Request();
        json["mode"] = "url";
        json["snapshot"] = null;
        json["control"] = null;
        json.Remove("version");
        json.Remove("created");
        json.Remove("narrative");

        var (request, problem) = LudworksRequest.Parse(json.ToJsonString(), Root);

        Assert.Null(problem);
        Assert.Null(request!.Snapshot);
        Assert.Null(request.Control);
        Assert.Null(request.Version);
        Assert.Null(request.Created);
        Assert.Equal("", request.Narrative);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"a prompt\"")]
    public void WhatIsNotARequestObjectIsRefused(string json)
    {
        var (request, problem) = LudworksRequest.Parse(json, Root);
        Assert.Null(request);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    public static TheoryData<string, Action<JsonObject>> Broken => new()
    {
        { "another app", r => r["app"] = "Some Other Browser" },
        { "no app", r => r.Remove("app") },
        { "no prompt", r => r.Remove("prompt") },
        { "a blank prompt", r => r["prompt"] = "  \n " },
        { "a prompt that is not text", r => r["prompt"] = 42 },
        { "a prompt far too long", r => r["prompt"] = new string('x', LudworksRequest.MaxPromptChars + 1) },
        { "no page", r => r.Remove("page") },
        { "a page without an address", r => r["page"] = new JsonObject { ["title"] = "Checkout" } },
        { "an unknown mode", r => r["mode"] = "video" },
        { "a snapshot without one", r => r["snapshot"] = null },
        { "a snapshot without its folder", r => r["snapshot"]!.AsObject().Remove("dir") },
        { "control without a tab", r => r["control"]!.AsObject().Remove("tabId") },
        { "control with a tab that is text", r => r["control"]!["tabId"] = "7" },
        { "control without a client", r => r["control"]!["client"] = "" },
    };

    [Theory]
    [MemberData(nameof(Broken))]
    public void ABrokenRequestIsRefusedWithAReason(string what, Action<JsonObject> breakIt)
    {
        var json = Request();
        breakIt(json);
        var (request, problem) = LudworksRequest.Parse(json.ToJsonString(), Root);
        Assert.True(request is null, $"{what} was accepted");
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Theory]
    [InlineData("dir")]
    [InlineData("screenshot")]
    [InlineData("html")]
    [InlineData("assets")]
    [InlineData("text")]
    public void ASnapshotPathOutsideTheHandoffFolderIsRefused(string field)
    {
        var json = Request();
        json["snapshot"]![field] = @"C:\Windows\System32\drivers\etc\hosts";
        var (request, problem) = LudworksRequest.Parse(json.ToJsonString(), Root);
        Assert.Null(request);
        Assert.Contains(field, problem);
    }

    [Fact]
    public void ASnapshotPathThatClimbsOutIsRefused()
    {
        var json = Request();
        json["snapshot"]!["screenshot"] = Path.Combine(Handoff, "..", "..", "secret.png");
        Assert.Null(LudworksRequest.Parse(json.ToJsonString(), Root).Request);
    }

    // MARK: - Where requests may come from

    [Fact]
    public void OnlyPathsStrictlyInsideTheRootAreInside()
    {
        var root = Path.GetFullPath(Root);
        Assert.True(LudworksRequest.IsInside(Path.Combine(root, "a", "request.json"), root));
        Assert.True(LudworksRequest.IsInside(Path.Combine(root, "a", "request.json"), root + Path.DirectorySeparatorChar));
        Assert.True(LudworksRequest.IsInside(Path.Combine(root.ToUpperInvariant(), "a", "request.json"), root)); // Windows paths ignore case
        Assert.True(LudworksRequest.IsInside(Path.Combine(root, "a", "..", "b", "request.json"), root));

        Assert.False(LudworksRequest.IsInside(root, root)); // the root itself
        Assert.False(LudworksRequest.IsInside(Path.Combine(root, ".."), root));
        Assert.False(LudworksRequest.IsInside(Path.Combine(root, "a", "..", "..", "request.json"), root));
        Assert.False(LudworksRequest.IsInside(root + " evil" + Path.DirectorySeparatorChar + "request.json", root)); // a sibling sharing the prefix
        Assert.False(LudworksRequest.IsInside(Path.Combine("a", "request.json"), root)); // relative
        Assert.False(LudworksRequest.IsInside(@"\\?\" + Path.Combine(root, "a", "request.json"), root));
        Assert.False(LudworksRequest.IsInside(@"\\server\share\request.json", root));
        Assert.False(LudworksRequest.IsInside("", root));
        Assert.False(LudworksRequest.IsInside(null, root));
    }

    [Fact]
    public void ARequestOutsideTheHandoffFolderIsNotRead()
    {
        var outside = WriteRequest(Request(), _temp["elsewhere"]);
        var (request, problem) = LudworksRequest.Load(outside, Root);
        Assert.Null(request);
        Assert.Contains(Root, problem);
    }

    [Fact]
    public void AMissingRequestSaysSo()
    {
        Directory.CreateDirectory(Handoff);
        var (request, problem) = LudworksRequest.Load(Path.Combine(Handoff, "request.json"), Root);
        Assert.Null(request);
        Assert.Contains("no longer there", problem);
    }

    [Fact]
    public void AnOversizedRequestIsNotRead()
    {
        var path = WriteRequest(Request());
        File.WriteAllText(path, new string(' ', (int)LudworksRequest.MaxFileBytes + 1));
        var (request, problem) = LudworksRequest.Load(path, Root);
        Assert.Null(request);
        Assert.Contains("too large", problem);
    }

    [Fact]
    public void AFolderLinkOutOfTheHandoffFolderIsNotFollowed()
    {
        var target = WriteRequest(Request(), _temp["elsewhere"]);
        Directory.CreateDirectory(Root);
        var link = Path.Combine(Root, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, Path.GetDirectoryName(target)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // creating links needs Developer Mode or elevation on Windows; nothing to test without one
        }
        var throughLink = Path.Combine(link, "request.json");
        Assert.True(LudworksRequest.IsInside(throughLink, Root)); // by its text it is inside…
        Assert.False(LudworksRequest.IsInsideOnDisk(throughLink, Root)); // …but on disk it is not
        Assert.Null(LudworksRequest.Load(throughLink, Root).Request);
    }

    // MARK: - The screenshot

    [Fact]
    public void TheScreenshotIsAttachedOnlyWhenItIsThere()
    {
        var (request, _) = LudworksRequest.Load(WriteRequest(Request()), Root);
        Assert.Null(request!.ScreenshotToAttach(Root));

        var screenshot = Path.Combine(Handoff, "screenshot.png");
        File.WriteAllBytes(screenshot, [0x89, 0x50, 0x4E, 0x47]);
        Assert.Equal(screenshot, request.ScreenshotToAttach(Root));
    }
}
