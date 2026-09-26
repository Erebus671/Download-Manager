using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using DownloadManagerApplet.Services.Updates;

namespace DownloadManagerApplet;

/// <summary>Builds WPF elements for parsed release notes. Text only: links render as their label and are not clickable.</summary>
public static class ReleaseNotesRenderer
{
    private const double IndentStep = 14;
    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas, Courier New");

    public static IReadOnlyList<UIElement> Render(IReadOnlyList<NotesBlock> blocks)
    {
        var elements = new List<UIElement>(blocks.Count);
        for (var i = 0; i < blocks.Count; i++)
        {
            var element = RenderBlock(blocks[i], isFirst: i == 0);
            if (element is not null)
            {
                elements.Add(element);
            }
        }
        return elements;
    }

    private static FrameworkElement? RenderBlock(NotesBlock block, bool isFirst)
    {
        switch (block.Kind)
        {
            case NotesBlockKind.Heading:
                var heading = Text(block.Spans, "TextBrush");
                heading.FontWeight = FontWeights.SemiBold;
                heading.FontSize = block.Level switch { 1 => 17, 2 => 15, _ => 13 };
                heading.Margin = new Thickness(0, isFirst ? 0 : block.Level <= 2 ? 10 : 8, 0, 3);
                return heading;

            case NotesBlockKind.Bullet:
            case NotesBlockKind.Numbered:
                return ListItem(block);

            case NotesBlockKind.Quote:
                var quote = Text(block.Spans, "TextDimBrush");
                var bar = new Border { BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(8, 0, 0, 0), Margin = new Thickness(0, 0, 0, 4), Child = quote };
                bar.SetResourceReference(Border.BorderBrushProperty, "AppBorderBrush");
                return bar;

            case NotesBlockKind.Code:
                var code = new TextBlock { Text = block.Spans.Count > 0 ? block.Spans[0].Text : string.Empty, FontFamily = CodeFont, FontSize = 12, TextWrapping = TextWrapping.Wrap };
                code.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                var box = new Border { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 2, 0, 6), CornerRadius = new CornerRadius(3), Child = code };
                box.SetResourceReference(Border.BackgroundProperty, "LogBackgroundBrush");
                return box;

            case NotesBlockKind.Rule:
                var rule = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 6) };
                rule.SetResourceReference(Border.BackgroundProperty, "AppBorderBrush");
                return rule;

            case NotesBlockKind.Paragraph:
                var paragraph = Text(block.Spans, "TextBrush");
                paragraph.Margin = new Thickness(0, 0, 0, 6);
                return paragraph;

            default:
                return null;
        }
    }

    private static Grid ListItem(NotesBlock block)
    {
        var grid = new Grid { Margin = new Thickness(block.Level * IndentStep, 0, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(block.Kind == NotesBlockKind.Numbered ? 22 : IndentStep) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var marker = new TextBlock { Text = block.Marker ?? "•" };
        marker.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        var body = Text(block.Spans, "TextBrush");
        Grid.SetColumn(body, 1);

        grid.Children.Add(marker);
        grid.Children.Add(body);
        return grid;
    }

    private static TextBlock Text(IReadOnlyList<NotesSpan> spans, string foregroundKey)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);
        foreach (var span in spans)
        {
            var run = new Run(span.Text);
            if (span.Bold)
            {
                run.FontWeight = FontWeights.SemiBold;
            }
            if (span.Italic)
            {
                run.FontStyle = FontStyles.Italic;
            }
            if (span.Code)
            {
                run.FontFamily = CodeFont;
                run.FontSize = 12;
                run.SetResourceReference(TextElement.BackgroundProperty, "LogBackgroundBrush");
            }
            text.Inlines.Add(run);
        }
        return text;
    }
}
