using System.Text;
using System.Text.RegularExpressions;

namespace DownloadManagerApplet.Services.Updates;

public enum NotesBlockKind { Heading, Paragraph, Bullet, Numbered, Quote, Code, Rule }

public sealed record NotesSpan(string Text, bool Bold = false, bool Italic = false, bool Code = false);

/// <summary>Level: heading level (1-6) or list nesting depth (0-3). Marker: list marker text.</summary>
public sealed record NotesBlock(NotesBlockKind Kind, IReadOnlyList<NotesSpan> Spans, int Level = 0, string? Marker = null);

/// <summary>Parses the Markdown subset used in GitHub release notes. Never throws; unknown syntax stays literal.</summary>
public static partial class ReleaseNotesMarkdown
{
    private const int MaxListDepth = 3;
    private const int MaxInlineDepth = 8;
    private const string EscapableChars = "\\`*_{}[]()#+-.!<>~|";

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.*?)(?:\s+#+)?\s*$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s{0,3}([-*_])(?:\s*\1){2,}\s*$")]
    private static partial Regex RuleRegex();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^(\s*)(\d{1,9})[.)]\s+(.*)$")]
    private static partial Regex NumberedRegex();

    [GeneratedRegex(@"^\s{0,3}>\s?(.*)$")]
    private static partial Regex QuoteRegex();

    [GeneratedRegex(@"^\s{0,3}(`{3,}|~{3,})")]
    private static partial Regex FenceRegex();

    public static IReadOnlyList<NotesBlock> Parse(string? markdown)
    {
        var blocks = new List<NotesBlock>();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return blocks;
        }

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var pending = new StringBuilder();
        NotesBlockKind pendingKind = NotesBlockKind.Paragraph;
        int pendingLevel = 0;
        string? pendingMarker = null;

        void Flush()
        {
            if (pending.Length > 0)
            {
                blocks.Add(new NotesBlock(pendingKind, ParseInline(pending.ToString()), pendingLevel, pendingMarker));
                pending.Clear();
            }
            pendingKind = NotesBlockKind.Paragraph;
            pendingLevel = 0;
            pendingMarker = null;
        }

        void Start(NotesBlockKind kind, string text, int level = 0, string? marker = null)
        {
            Flush();
            pendingKind = kind;
            pendingLevel = level;
            pendingMarker = marker;
            pending.Append(text.Trim());
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            var fence = FenceRegex().Match(line);
            if (fence.Success)
            {
                Flush();
                var closer = fence.Groups[1].Value;
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith(closer, StringComparison.Ordinal); i++)
                {
                    code.Add(lines[i]);
                }
                if (code.Count > 0)
                {
                    blocks.Add(new NotesBlock(NotesBlockKind.Code, [new NotesSpan(string.Join('\n', code), Code: true)]));
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }

            Match m;
            if ((m = HeadingRegex().Match(line)).Success)
            {
                Start(NotesBlockKind.Heading, m.Groups[2].Value, m.Groups[1].Length);
                Flush();
            }
            else if (RuleRegex().IsMatch(line))
            {
                Flush();
                blocks.Add(new NotesBlock(NotesBlockKind.Rule, []));
            }
            else if ((m = BulletRegex().Match(line)).Success)
            {
                Start(NotesBlockKind.Bullet, m.Groups[2].Value, IndentDepth(m.Groups[1].Value), "•");
            }
            else if ((m = NumberedRegex().Match(line)).Success)
            {
                Start(NotesBlockKind.Numbered, m.Groups[3].Value, IndentDepth(m.Groups[1].Value), m.Groups[2].Value + ".");
            }
            else if ((m = QuoteRegex().Match(line)).Success)
            {
                if (pendingKind == NotesBlockKind.Quote && pending.Length > 0)
                {
                    pending.Append(' ').Append(m.Groups[1].Value.Trim());
                }
                else
                {
                    Start(NotesBlockKind.Quote, m.Groups[1].Value);
                }
            }
            else if (pending.Length > 0)
            {
                // Lazy continuation of the current paragraph, list item, or quote.
                pending.Append(' ').Append(line.Trim());
            }
            else
            {
                Start(NotesBlockKind.Paragraph, line);
            }
        }

        Flush();
        return blocks;
    }

    private static int IndentDepth(string indent)
    {
        var width = 0;
        foreach (var c in indent)
        {
            width += c == '\t' ? 4 : 1;
        }
        return Math.Min(width / 2, MaxListDepth);
    }

    public static IReadOnlyList<NotesSpan> ParseInline(string text)
    {
        var spans = new List<NotesSpan>();
        ParseInline(text, bold: false, italic: false, depth: 0, spans);
        return Merge(spans);
    }

    private static void ParseInline(string text, bool bold, bool italic, int depth, List<NotesSpan> spans)
    {
        var plain = new StringBuilder();

        void Emit()
        {
            if (plain.Length > 0)
            {
                spans.Add(new NotesSpan(plain.ToString(), bold, italic));
                plain.Clear();
            }
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\\' && i + 1 < text.Length && EscapableChars.Contains(text[i + 1]))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                var run = CountRun(text, i, '`');
                var close = FindRun(text, i + run, '`', run);
                if (close >= 0)
                {
                    Emit();
                    var code = text[(i + run)..close];
                    if (code.Length > 1 && code[0] == ' ' && code[^1] == ' ')
                    {
                        code = code[1..^1];
                    }
                    spans.Add(new NotesSpan(code, bold, italic, Code: true));
                    i = close + run;
                    continue;
                }
                plain.Append(text, i, run);
                i += run;
                continue;
            }

            if (depth < MaxInlineDepth && (c == '*' || c == '_'))
            {
                var run = CountRun(text, i, c);
                var intraword = c == '_' && i > 0 && char.IsLetterOrDigit(text[i - 1]);
                var width = run >= 2 ? 2 : 1;
                if (!intraword && i + width < text.Length && !char.IsWhiteSpace(text[i + width]))
                {
                    var close = FindEmphasisClose(text, i + width, c, width);
                    if (close > i + width)
                    {
                        Emit();
                        ParseInline(text[(i + width)..close], bold || width == 2, italic || width == 1, depth + 1, spans);
                        i = close + width;
                        continue;
                    }
                }
                plain.Append(text, i, run);
                i += run;
                continue;
            }

            if (depth < MaxInlineDepth && (c == '[' || (c == '!' && i + 1 < text.Length && text[i + 1] == '[')))
            {
                var open = c == '!' ? i + 1 : i;
                if (TryParseLink(text, open, out var label, out var end))
                {
                    Emit();
                    ParseInline(label, bold, italic, depth + 1, spans);
                    i = end;
                    continue;
                }
            }

            if (c == '<')
            {
                var close = text.IndexOf('>', i + 1);
                if (close > i + 1)
                {
                    var inner = text[(i + 1)..close];
                    if (inner.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || inner.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        plain.Append(inner);
                        i = close + 1;
                        continue;
                    }
                }
            }

            plain.Append(c);
            i++;
        }

        Emit();
    }

    private static int CountRun(string text, int start, char c)
    {
        var n = 0;
        while (start + n < text.Length && text[start + n] == c)
        {
            n++;
        }
        return n;
    }

    private static int FindRun(string text, int start, char c, int length)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] != c)
            {
                continue;
            }
            var run = CountRun(text, i, c);
            if (run == length)
            {
                return i;
            }
            i += run - 1;
        }
        return -1;
    }

    private static int FindEmphasisClose(string text, int start, char c, int width)
    {
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\\')
            {
                i++;
                continue;
            }
            if (ch == '`')
            {
                var run = CountRun(text, i, '`');
                var close = FindRun(text, i + run, '`', run);
                i = close >= 0 ? close + run - 1 : i + run - 1;
                continue;
            }
            if (ch != c)
            {
                continue;
            }
            var len = CountRun(text, i, c);
            var afterIntraword = c == '_' && i + len < text.Length && char.IsLetterOrDigit(text[i + len]);
            if (!char.IsWhiteSpace(text[i - 1]) && !afterIntraword && (len == width || (width == 1 && len == 3) || (width == 2 && len >= 2)))
            {
                return width == 2 && len == 3 ? i + 1 : i;
            }
            i += len - 1;
        }
        return -1;
    }

    private static bool TryParseLink(string text, int open, out string label, out int end)
    {
        label = string.Empty;
        end = -1;
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '[')
            {
                depth++;
            }
            else if (text[i] == ']' && --depth == 0)
            {
                if (i + 1 >= text.Length || text[i + 1] != '(')
                {
                    return false;
                }
                var close = text.IndexOf(')', i + 2);
                if (close < 0)
                {
                    return false;
                }
                label = text[(open + 1)..i];
                end = close + 1;
                return true;
            }
        }
        return false;
    }

    private static List<NotesSpan> Merge(List<NotesSpan> spans)
    {
        var merged = new List<NotesSpan>(spans.Count);
        foreach (var s in spans)
        {
            if (s.Text.Length == 0)
            {
                continue;
            }
            if (merged.Count > 0 && merged[^1] is var last && last.Bold == s.Bold && last.Italic == s.Italic && last.Code == s.Code && !s.Code)
            {
                merged[^1] = last with { Text = last.Text + s.Text };
            }
            else
            {
                merged.Add(s);
            }
        }
        return merged;
    }
}
