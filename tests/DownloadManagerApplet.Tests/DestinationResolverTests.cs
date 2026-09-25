using System.IO;
using DownloadManagerApplet.Models;
using DownloadManagerApplet.Services;
using DotNetTestKit;

namespace DownloadManagerApplet.Tests;

public class DestinationResolverTests : TestBase
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("movie.MKV", null, FileCategory.Videos)]
    [InlineData("photo.jpeg", null, FileCategory.Images)]
    [InlineData("song.flac", null, FileCategory.Audio)]
    [InlineData("report.pdf", null, FileCategory.Documents)]
    [InlineData("setup.exe", null, FileCategory.Other)]
    [InlineData("stream", "video/mp4", FileCategory.Videos)]
    [InlineData("download", "audio/mpeg", FileCategory.Audio)]
    [InlineData("blob.bin", "application/octet-stream", FileCategory.Other)]
    public void Categorize_UsesExtensionThenMimeType(string fileName, string? mime, FileCategory expected)
    {
        Assert.Equal(expected, DestinationResolver.Categorize(new AppSettings(), fileName, mime));
    }

    [Fact]
    public void Resolve_UsesCategoryFolder_WhenNoRuleMatches()
    {
        var state = TestStates.InFolder(Temp.Path);

        var choice = Resolve(state.Settings, Facts("https://example.com/clip.mp4"));

        Assert.Equal(Path.Combine(Temp.Path, "Videos"), choice.Folder);
        Assert.Equal(FileCategory.Videos, choice.Category);
        Assert.Null(choice.RuleName);
    }

    [Fact]
    public void Resolve_UsesDefaultFolder_WhenSortingIsOff()
    {
        var state = TestStates.InFolder(Temp.Path);
        state.Settings.SortByFileType = false;

        var choice = Resolve(state.Settings, Facts("https://example.com/clip.mp4"));

        Assert.Equal(Path.Combine(Temp.Path, "Other"), choice.Folder);
    }

    [Fact]
    public void Resolve_FirstMatchingRuleWins()
    {
        var state = TestStates.InFolder(Temp.Path);
        state.Settings.DestinationRules.Add(Rule("First", Path.Combine(Temp.Path, "A"), Cond(RuleField.Extension, RuleOperator.Is, "zip")));
        state.Settings.DestinationRules.Add(Rule("Second", Path.Combine(Temp.Path, "B"), Cond(RuleField.Extension, RuleOperator.Is, "zip")));

        var choice = Resolve(state.Settings, Facts("https://example.com/a.zip"));

        Assert.Equal("First", choice.RuleName);
        Assert.Equal(Path.Combine(Temp.Path, "A"), choice.Folder);
    }

    [Fact]
    public void Resolve_SkipsRuleWithUnusableFolder()
    {
        var state = TestStates.InFolder(Temp.Path);
        state.Settings.DestinationRules.Add(Rule("Broken", @"relative\folder", Cond(RuleField.Extension, RuleOperator.Is, "zip")));

        var choice = Resolve(state.Settings, Facts("https://example.com/a.zip"));

        Assert.Null(choice.RuleName);
        Assert.Equal(Path.Combine(Temp.Path, "Other"), choice.Folder);
    }

    [Fact]
    public void Resolve_FallsBackToDownloads_WhenCategoryFolderUnusable()
    {
        var settings = new AppSettings { DefaultDownloadFolder = "not a path" };
        settings.Categories.Single(c => c.Category == FileCategory.Videos).Folder = "also not a path";

        var choice = Resolve(settings, Facts("https://example.com/clip.mp4"));

        Assert.Equal(KnownFolders.Downloads, choice.Folder);
    }

    [Fact]
    public void Matches_AllRequiresEveryCondition_AnyRequiresOne()
    {
        var rule = Rule("r", @"C:\x",
            Cond(RuleField.Extension, RuleOperator.Is, "iso"),
            new RuleCondition { Field = RuleField.Size, Operator = RuleOperator.GreaterThan, Value = "1", Unit = SizeUnit.GB });
        var smallIso = Facts("https://example.com/a.iso", size: 10 * 1024 * 1024);
        var bigIso = Facts("https://example.com/a.iso", size: 2L * 1024 * 1024 * 1024);

        Assert.False(DestinationResolver.Matches(rule, smallIso));
        Assert.True(DestinationResolver.Matches(rule, bigIso));

        rule.MatchAll = false;
        Assert.True(DestinationResolver.Matches(rule, smallIso));
    }

    [Theory]
    [InlineData("https://github.com/x/y.zip", RuleOperator.Is, "github.com", true)]
    [InlineData("https://www.github.com/x/y.zip", RuleOperator.Is, "github.com", true)]
    [InlineData("https://objects.github.com/y.zip", RuleOperator.Is, "github.com", true)]
    [InlineData("https://notgithub.com/y.zip", RuleOperator.Is, "github.com", false)]
    [InlineData("https://notgithub.com/y.zip", RuleOperator.IsNot, "github.com", true)]
    [InlineData("https://cdn3.example.org/y.zip", RuleOperator.Matches, "cdn*.example.org", true)]
    public void Evaluate_Site(string url, RuleOperator op, string value, bool expected)
    {
        Assert.Equal(expected, DestinationResolver.Evaluate(Cond(RuleField.Site, op, value), Facts(url)));
    }

    [Theory]
    [InlineData("Release-v2.ZIP", RuleOperator.Matches, "release-*.zip", true)]
    [InlineData("notes.txt", RuleOperator.Matches, "*.zip", false)]
    [InlineData("notes.txt", RuleOperator.NotMatches, "*.zip", true)]
    [InlineData("a(1).zip", RuleOperator.Matches, "a(?).zip", true)]
    public void Evaluate_FileNameWildcard(string name, RuleOperator op, string value, bool expected)
    {
        Assert.Equal(expected, DestinationResolver.Evaluate(Cond(RuleField.FileName, op, value), Facts("https://example.com/" + name)));
    }

    [Fact]
    public void Evaluate_SizeIsFalse_WhenSizeUnknown()
    {
        var condition = new RuleCondition { Field = RuleField.Size, Operator = RuleOperator.LessThan, Value = "5", Unit = SizeUnit.MB };

        Assert.False(DestinationResolver.Evaluate(condition, Facts("https://example.com/a.bin")));
        Assert.True(DestinationResolver.Evaluate(condition, Facts("https://example.com/a.bin", size: 1024)));
    }

    [Fact]
    public void Evaluate_MimeTypeWildcardAndExtensionList()
    {
        var facts = Facts("https://example.com/a.iso", mime: "video/webm");

        Assert.True(DestinationResolver.Evaluate(Cond(RuleField.MimeType, RuleOperator.Matches, "video/*"), facts));
        Assert.True(DestinationResolver.Evaluate(Cond(RuleField.Extension, RuleOperator.Is, ".img, ISO"), facts));
        Assert.False(DestinationResolver.Evaluate(Cond(RuleField.Extension, RuleOperator.IsNot, "iso"), facts));
    }

    [Fact]
    public void ExpandFolder_ReplacesPlaceholdersSafely()
    {
        var facts = new DownloadFacts(new Uri("https://www.twitch.tv/x"), "a.mp4", Channel: "bad:name?");

        var folder = DestinationResolver.ExpandFolder(Path.Combine(Temp.Path, "{site}", "{channel}", "{date}", "{category}"), facts, FileCategory.Videos, Now);

        Assert.Equal(Path.Combine(Temp.Path, "twitch.tv", "bad_name_", "2026-09-25", "Videos"), folder);
    }

    [Fact]
    public void ExpandFolder_UsesFallbackForMissingChannel_AndRejectsRelativePaths()
    {
        var facts = Facts("https://example.com/a.mp4");

        Assert.EndsWith("Unknown channel", DestinationResolver.ExpandFolder(Path.Combine(Temp.Path, "{channel}"), facts, FileCategory.Videos, Now));
        Assert.Null(DestinationResolver.ExpandFolder(@"Streams\{channel}", facts, FileCategory.Videos, Now));
    }

    [Fact]
    public void Validate_ReportsEachProblem()
    {
        var rule = new DestinationRule
        {
            Conditions = { new RuleCondition { Field = RuleField.Size, Operator = RuleOperator.GreaterThan, Value = "lots" } }
        };

        var errors = DestinationResolver.Validate(rule);

        Assert.Contains(errors, e => e.Contains("name"));
        Assert.Contains(errors, e => e.Contains("number"));
        Assert.Contains(errors, e => e.Contains("folder"));
        Assert.Empty(DestinationResolver.Validate(Rule("ok", @"D:\ISOs", Cond(RuleField.Extension, RuleOperator.Is, "iso"))));
    }

    [Fact]
    public void Describe_JoinsConditions()
    {
        var rule = Rule("r", @"D:\ISOs",
            Cond(RuleField.Extension, RuleOperator.Is, "iso"),
            new RuleCondition { Field = RuleField.Size, Operator = RuleOperator.GreaterThan, Value = "1", Unit = SizeUnit.GB });

        Assert.Equal("Extension is iso and size > 1 GB", DestinationResolver.Describe(rule));
    }

    [Fact]
    public void Normalize_RestoresMissingCategoriesInOrder()
    {
        var settings = new AppSettings
        {
            Categories = [new CategoryFolder { Category = FileCategory.Audio, Folder = @"D:\Music", Extensions = ["MP3", "mp3", ".ogg"] }],
            DestinationRules = null!
        };

        DestinationResolver.Normalize(settings);

        Assert.Equal(DestinationResolver.SortedCategories, settings.Categories.Select(c => c.Category));
        var audio = settings.Categories.Single(c => c.Category == FileCategory.Audio);
        Assert.Equal(@"D:\Music", audio.Folder);
        Assert.Equal(["mp3", "ogg"], audio.Extensions);
        Assert.NotNull(settings.DestinationRules);
    }

    private static DestinationChoice Resolve(AppSettings settings, DownloadFacts facts) =>
        DestinationResolver.Resolve(settings, facts, Now, NullLoggingService.Instance);

    private static DownloadFacts Facts(string url, long? size = null, string? mime = null)
    {
        var uri = new Uri(url);
        return new DownloadFacts(uri, Path.GetFileName(uri.LocalPath), size, mime);
    }

    private static RuleCondition Cond(RuleField field, RuleOperator op, string value) => new() { Field = field, Operator = op, Value = value };

    private static DestinationRule Rule(string name, string folder, params RuleCondition[] conditions) =>
        new() { Name = name, Folder = folder, Conditions = conditions.ToList() };
}
