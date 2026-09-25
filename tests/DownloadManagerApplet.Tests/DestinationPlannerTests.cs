using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using DownloadManagerApplet.Models;
using DownloadManagerApplet.Services;
using DotNetTestKit;

namespace DownloadManagerApplet.Tests;

/// <summary>Planner and engine together: the final name and folder come from the server's response.</summary>
public class DestinationPlannerTests : TestBase
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("file body");

    [Fact]
    public async Task ContentDisposition_RenamesAndMovesAutomaticDownload()
    {
        var (state, planner, engine) = Setup(r => r.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "\"clip.mp4\"" });
        var changed = 0;
        planner.DestinationChanged += _ => changed++;
        var item = Add(state, planner, "https://example.com/get?id=5", folder: null);
        Assert.Equal(Path.Combine(Temp.Path, "Other"), item.DestinationFolder);

        await engine.DownloadAsync(item, new Progress<DownloadProgress>(), CancellationToken.None);

        Assert.Equal("clip.mp4", item.FileName);
        Assert.Equal(Path.Combine(Temp.Path, "Videos"), item.DestinationFolder);
        Assert.Equal(FileCategory.Videos, item.Category);
        Assert.False(item.ResolveOnResponse);
        Assert.Equal(1, changed);
        Assert.Equal(Body, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task UserChosenFolder_KeepsFolderButTakesServerName()
    {
        var chosen = Path.Combine(Temp.Path, "Mine");
        var (state, planner, engine) = Setup(r => r.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = "Résumé.pdf" });
        var item = Add(state, planner, "https://example.com/dl", folder: chosen);

        await engine.DownloadAsync(item, new Progress<DownloadProgress>(), CancellationToken.None);

        Assert.Equal("Résumé.pdf", item.FileName);
        Assert.Equal(chosen, item.DestinationFolder);
        Assert.True(File.Exists(Path.Combine(chosen, "Résumé.pdf")));
    }

    [Fact]
    public async Task LegacyItem_IsNotRenamed()
    {
        var (state, _, engine) = Setup(r => r.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "other.bin" });
        var item = new DownloadItem { Url = "https://example.com/x.zip", FileName = "x.zip", DestinationFolder = Temp.Path };
        state.Downloads.Add(item);

        await engine.DownloadAsync(item, new Progress<DownloadProgress>(), CancellationToken.None);

        Assert.Equal("x.zip", item.FileName);
        Assert.True(File.Exists(Path.Combine(Temp.Path, "x.zip")));
    }

    [Fact]
    public async Task ServerPathTraversal_IsReducedToAPlainName()
    {
        var (state, planner, engine) = Setup(r => r.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "\"..\\\\..\\\\evil.exe\"" });
        var item = Add(state, planner, "https://example.com/get", folder: null);

        await engine.DownloadAsync(item, new Progress<DownloadProgress>(), CancellationToken.None);

        Assert.Equal("evil.exe", item.FileName);
        Assert.Equal(Path.Combine(Temp.Path, "Other"), item.DestinationFolder);
    }

    [Fact]
    public async Task RedirectedUrl_SuppliesNameWhenOriginalHasNoExtension()
    {
        var (state, planner, engine) = Setup(r => r.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://cdn.example.com/files/song.mp3"));
        var item = Add(state, planner, "https://example.com/latest", folder: null);

        await engine.DownloadAsync(item, new Progress<DownloadProgress>(), CancellationToken.None);

        Assert.Equal("song.mp3", item.FileName);
        Assert.Equal(Path.Combine(Temp.Path, "Audio"), item.DestinationFolder);
    }

    [Fact]
    public async Task SizeRule_AppliesOnceSizeIsKnown()
    {
        var big = Path.Combine(Temp.Path, "Big");
        var (state, planner, engine) = Setup(_ => { });
        state.Settings.DestinationRules.Add(new DestinationRule
        {
            Name = "Big",
            Folder = big,
            Conditions = { new RuleCondition { Field = RuleField.Size, Operator = RuleOperator.GreaterThan, Value = "1", Unit = SizeUnit.KB } }
        });
        planner = new DestinationPlanner(state, NullLoggingService.Instance, uiDispatcher: () => null);
        engine = new HttpDownloadEngine(new HttpClient(new BodyHandler(new byte[4096], _ => { })), NullLoggingService.Instance, planner);
        var item = Add(state, planner, "https://example.com/data.bin", folder: null);
        Assert.Equal(Path.Combine(Temp.Path, "Other"), item.DestinationFolder);

        await engine.DownloadAsync(item, new Progress<DownloadProgress>(), CancellationToken.None);

        Assert.Equal(big, item.DestinationFolder);
        Assert.Equal("Big", item.RuleName);
    }

    [Fact]
    public void MakeUniqueFileName_SkipsPartFilesAndOtherItems_ButNotItself()
    {
        var state = TestStates.InFolder(Temp.Path);
        var planner = new DestinationPlanner(state, NullLoggingService.Instance, uiDispatcher: () => null);
        File.WriteAllText(Path.Combine(Temp.Path, "a.zip.part"), "x");
        var self = new DownloadItem { Url = "https://e.com/b.zip", FileName = "b.zip", DestinationFolder = Temp.Path };
        state.Downloads.Add(self);

        Assert.Equal("a (1).zip", planner.MakeUniqueFileName(Temp.Path, "a.zip", null));
        Assert.Equal("b.zip", planner.MakeUniqueFileName(Temp.Path, "b.zip", self));
        Assert.Equal("b (1).zip", planner.MakeUniqueFileName(Temp.Path, "b.zip", null));
    }

    [Fact]
    public async Task Rename_BeforeResponse_KeepsUserNameOverServerName()
    {
        var (state, planner, engine) = Setup(r => r.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "server.pdf" });
        var item = Add(state, planner, "https://example.com/get", folder: null);

        Assert.Null(planner.Rename(item, "mine.pdf"));
        await engine.DownloadAsync(item, new Progress<DownloadProgress>(), CancellationToken.None);

        Assert.Equal("mine.pdf", item.FileName);
        Assert.Equal(Path.Combine(Temp.Path, "Documents"), item.DestinationFolder);
        Assert.Equal(Body, File.ReadAllBytes(item.FullPath));
        Assert.Null(item.PartFileName);
    }

    [Fact]
    public async Task Rename_Unfinished_KeepsPartName_ThenCompletesUnderNewName()
    {
        var (state, planner, engine) = Setup(_ => { });
        var item = Add(state, planner, "https://example.com/a.zip", folder: Temp.Path);
        item.Status = DownloadStatus.Paused;
        File.WriteAllText(item.PartFilePath, "partial");

        Assert.Null(planner.Rename(item, "b.zip"));
        Assert.Equal(Path.Combine(Temp.Path, "a.zip.part"), item.PartFilePath);

        await engine.DownloadAsync(item, new Progress<DownloadProgress>(), CancellationToken.None);

        Assert.Equal(Body, File.ReadAllBytes(Path.Combine(Temp.Path, "b.zip")));
        Assert.False(File.Exists(Path.Combine(Temp.Path, "a.zip.part")));
    }

    [Fact]
    public void Rename_Completed_MovesFile()
    {
        var state = TestStates.InFolder(Temp.Path);
        var planner = new DestinationPlanner(state, NullLoggingService.Instance, uiDispatcher: () => null);
        var item = new DownloadItem { Url = "https://e.com/a.txt", FileName = "a.txt", DestinationFolder = Temp.Path, Status = DownloadStatus.Completed };
        state.Downloads.Add(item);
        File.WriteAllText(item.FullPath, "done");
        var changed = 0;
        planner.DestinationChanged += _ => changed++;

        Assert.Null(planner.Rename(item, "notes.pdf"));

        Assert.Equal("done", File.ReadAllText(Path.Combine(Temp.Path, "notes.pdf")));
        Assert.False(File.Exists(Path.Combine(Temp.Path, "a.txt")));
        Assert.Equal(FileCategory.Documents, item.Category);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Rename_RejectsConflictsBadNamesAndCanceled()
    {
        var state = TestStates.InFolder(Temp.Path);
        var planner = new DestinationPlanner(state, NullLoggingService.Instance, uiDispatcher: () => null);
        var item = new DownloadItem { Url = "https://e.com/a.txt", FileName = "a.txt", DestinationFolder = Temp.Path, Status = DownloadStatus.Paused };
        state.Downloads.Add(item);
        File.WriteAllText(Path.Combine(Temp.Path, "taken.txt"), "x");

        Assert.Contains("already exists", planner.Rename(item, "taken.txt"));
        Assert.Contains("isn't allowed", planner.Rename(item, "bad:name.txt"));
        Assert.Contains("isn't allowed", planner.Rename(item, "   "));
        item.Status = DownloadStatus.Canceled;
        Assert.Contains("Canceled", planner.Rename(item, "ok.txt"));
        Assert.Equal("a.txt", item.FileName);
        Assert.False(item.UserNamed);
    }

    [Theory]
    [InlineData("  report.pdf  ", "report.pdf")]
    [InlineData("a<b>c.txt", "a_b_c.txt")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("...", null)]
    [InlineData("", null)]
    public void SanitizeFileName(string input, string? expected)
    {
        Assert.Equal(expected, DestinationPlanner.SanitizeFileName(input));
    }

    private (AppState State, DestinationPlanner Planner, HttpDownloadEngine Engine) Setup(Action<HttpResponseMessage> shape)
    {
        var state = TestStates.InFolder(Temp.Path);
        var planner = new DestinationPlanner(state, NullLoggingService.Instance, uiDispatcher: () => null);
        var engine = new HttpDownloadEngine(new HttpClient(new BodyHandler(Body, shape)), NullLoggingService.Instance, planner);
        return (state, planner, engine);
    }

    private static DownloadItem Add(AppState state, DestinationPlanner planner, string url, string? folder)
    {
        var item = planner.CreateItem(url, new Uri(url), folder);
        state.Downloads.Add(item);
        return item;
    }

    private sealed class BodyHandler(byte[] body, Action<HttpResponseMessage> shape) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request };
            shape(response);
            return Task.FromResult(response);
        }
    }
}
