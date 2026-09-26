using System.Windows.Controls;
using System.Windows.Documents;
using DownloadManagerApplet.Services.Updates;

namespace DownloadManagerApplet.Tests;

public class ReleaseNotesMarkdownTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n\r\n ")]
    public void Parse_EmptyInput_ReturnsNoBlocks(string? input)
    {
        Assert.Empty(ReleaseNotesMarkdown.Parse(input));
    }

    [Fact]
    public void Parse_Headings_CaptureLevelAndStripClosingHashes()
    {
        var blocks = ReleaseNotesMarkdown.Parse("## What's new in 1.2.0\n### Fixes ##\n#NotAHeading");

        Assert.Equal(3, blocks.Count);
        Assert.Equal((NotesBlockKind.Heading, 2, "What's new in 1.2.0"), (blocks[0].Kind, blocks[0].Level, Text(blocks[0])));
        Assert.Equal((NotesBlockKind.Heading, 3, "Fixes"), (blocks[1].Kind, blocks[1].Level, Text(blocks[1])));
        Assert.Equal((NotesBlockKind.Paragraph, "#NotAHeading"), (blocks[2].Kind, Text(blocks[2])));
    }

    [Fact]
    public void Parse_Bullets_TrackNestingAndContinuationLines()
    {
        var blocks = ReleaseNotesMarkdown.Parse("- one\n  continued\n* two\n  - nested\n+ three");

        Assert.Equal(4, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(NotesBlockKind.Bullet, b.Kind));
        Assert.Equal("one continued", Text(blocks[0]));
        Assert.Equal(0, blocks[1].Level);
        Assert.Equal(1, blocks[2].Level);
        Assert.Equal("nested", Text(blocks[2]));
    }

    [Fact]
    public void Parse_NumberedList_KeepsNumberAsMarker()
    {
        var blocks = ReleaseNotesMarkdown.Parse("1. first\n2) second");

        Assert.Equal(["1.", "2."], blocks.Select(b => b.Marker));
        Assert.All(blocks, b => Assert.Equal(NotesBlockKind.Numbered, b.Kind));
    }

    [Fact]
    public void Parse_ParagraphLines_JoinUntilBlankLine()
    {
        var blocks = ReleaseNotesMarkdown.Parse("line one\r\nline two\r\n\r\nnext");

        Assert.Equal(["line one line two", "next"], blocks.Select(Text));
    }

    [Fact]
    public void Parse_FencedCode_IsVerbatimAndNotInlineParsed()
    {
        var blocks = ReleaseNotesMarkdown.Parse("```powershell\n**not bold**\n  indented\n```\nafter");

        Assert.Equal(NotesBlockKind.Code, blocks[0].Kind);
        Assert.Equal("**not bold**\n  indented", blocks[0].Spans.Single().Text);
        Assert.Equal("after", Text(blocks[1]));
    }

    [Fact]
    public void Parse_UnclosedFence_TakesRestAsCode()
    {
        var blocks = ReleaseNotesMarkdown.Parse("```\ncode");

        Assert.Equal(NotesBlockKind.Code, Assert.Single(blocks).Kind);
    }

    [Fact]
    public void Parse_RuleAndQuote()
    {
        var blocks = ReleaseNotesMarkdown.Parse("> quoted\n> more\n\n---\n***");

        Assert.Equal([NotesBlockKind.Quote, NotesBlockKind.Rule, NotesBlockKind.Rule], blocks.Select(b => b.Kind));
        Assert.Equal("quoted more", Text(blocks[0]));
    }

    [Fact]
    public void ParseInline_BoldItalicCode()
    {
        var spans = ReleaseNotesMarkdown.ParseInline("Open **Settings > General**, run `dotnet test`, then *restart*.");

        Assert.Equal(
            [
                new NotesSpan("Open "),
                new NotesSpan("Settings > General", Bold: true),
                new NotesSpan(", run "),
                new NotesSpan("dotnet test", Code: true),
                new NotesSpan(", then "),
                new NotesSpan("restart", Italic: true),
                new NotesSpan("."),
            ],
            spans);
    }

    [Fact]
    public void ParseInline_CodeSpanContentIsLiteral()
    {
        var spans = ReleaseNotesMarkdown.ParseInline("`**x**` and ``a ` b``");

        Assert.Equal([new NotesSpan("**x**", Code: true), new NotesSpan(" and "), new NotesSpan("a ` b", Code: true)], spans);
    }

    [Fact]
    public void ParseInline_BoldItalicCombined()
    {
        var spans = ReleaseNotesMarkdown.ParseInline("***both*** and **bold with *italic* inside**");

        Assert.Equal(new NotesSpan("both", Bold: true, Italic: true), spans[0]);
        Assert.Contains(new NotesSpan("italic", Bold: true, Italic: true), spans);
        Assert.Contains(new NotesSpan("bold with ", Bold: true), spans);
    }

    [Theory]
    [InlineData("snake_case_name stays")]
    [InlineData("2 * 3 * 4")]
    [InlineData("unclosed **bold")]
    [InlineData("stray ` backtick")]
    [InlineData("[not a link]")]
    public void ParseInline_NonMarkup_StaysLiteral(string input)
    {
        var spans = ReleaseNotesMarkdown.ParseInline(input);

        Assert.Equal(new NotesSpan(input), Assert.Single(spans));
    }

    [Fact]
    public void ParseInline_Escapes_AreRemovedAndLiteral()
    {
        Assert.Equal(new NotesSpan("*not italic*"), Assert.Single(ReleaseNotesMarkdown.ParseInline(@"\*not italic\*")));
    }

    [Fact]
    public void ParseInline_Links_RenderAsLabelOnly()
    {
        var spans = ReleaseNotesMarkdown.ParseInline("See [the **docs**](https://example.com/x) or <https://example.com> ![logo](a.png)");

        Assert.Equal("See the docs or https://example.com logo", string.Concat(spans.Select(s => s.Text)));
        Assert.Contains(new NotesSpan("docs", Bold: true), spans);
    }

    [Fact]
    public void Parse_TruncatedMarkup_DoesNotThrow()
    {
        // GitHubReleaseSource cuts notes at 4000 chars, which can split markup.
        var blocks = ReleaseNotesMarkdown.Parse("- **Save to** list `Choose fol...");

        Assert.Equal("Save to list `Choose fol...", Text(Assert.Single(blocks)));
    }

    [Fact]
    public void Parse_PathologicalInput_Completes()
    {
        var input = string.Concat(Enumerable.Repeat("**[`_*", 700));

        var blocks = ReleaseNotesMarkdown.Parse(input);

        Assert.Single(blocks);
    }

    [Fact]
    public void Parse_ReleaseNotesLayout_ProducesExpectedStructure()
    {
        const string notes = """
            ## What's new in 1.2.0: The Obsessive Compulsive Update

            ### Downloads sort themselves
            - Videos go to your Windows **Videos** folder.
            - Folder paths can use `{site}` and `%USERPROFILE%`.

            ## Files
            - `AtraTechDownloadSolutions.exe`: the installer
            """;

        var blocks = ReleaseNotesMarkdown.Parse(notes);

        Assert.Equal(
            [NotesBlockKind.Heading, NotesBlockKind.Heading, NotesBlockKind.Bullet, NotesBlockKind.Bullet, NotesBlockKind.Heading, NotesBlockKind.Bullet],
            blocks.Select(b => b.Kind));
        Assert.Contains(new NotesSpan("Videos", Bold: true), blocks[2].Spans);
        Assert.Contains(new NotesSpan("{site}", Code: true), blocks[3].Spans);
    }

    [Fact]
    public void Render_BuildsStyledElements()
    {
        var blocks = ReleaseNotesMarkdown.Parse("## Title\n- **bold** item\n1. `code`\n\n```\nx\n```\n---\n> q\n\npara");

        var result = RunSta(() =>
        {
            var elements = ReleaseNotesRenderer.Render(blocks);
            var heading = Assert.IsType<TextBlock>(elements[0]);
            var bullet = Assert.IsType<Grid>(elements[1]);
            var bulletRun = (Run)((TextBlock)bullet.Children[1]).Inlines.FirstInline;
            var numbered = Assert.IsType<Grid>(elements[2]);
            return (elements.Count, heading.FontWeight, ((TextBlock)bullet.Children[0]).Text, bulletRun.FontWeight, ((TextBlock)numbered.Children[0]).Text);
        });

        Assert.Equal((7, System.Windows.FontWeights.SemiBold, "•", System.Windows.FontWeights.SemiBold, "1."), result);
    }

    private static string Text(NotesBlock block) => string.Concat(block.Spans.Select(s => s.Text));

    private static T RunSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw new AggregateException(error);
        }
        return result;
    }
}
