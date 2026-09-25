using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DownloadManagerApplet.Models;

namespace DownloadManagerApplet.Services;

/// <summary>What is known about a download when its folder is chosen. Size and MIME type are null until the server responds.</summary>
public sealed record DownloadFacts(
    Uri Url,
    string FileName,
    long? Size = null,
    string? MimeType = null,
    DownloadSource Source = DownloadSource.Manual,
    string? VideoSite = null,
    string? Channel = null);

public sealed record DestinationChoice(string Folder, FileCategory Category, string? RuleName);

/// <summary>Picks a download folder: custom rules first (first match wins), then the file-type category, then the default folder.</summary>
public static class DestinationResolver
{
    public static readonly IReadOnlyList<FileCategory> SortedCategories =
        [FileCategory.Videos, FileCategory.Images, FileCategory.Audio, FileCategory.Documents];

    private static readonly IReadOnlyDictionary<FileCategory, string> DefaultExtensions = new Dictionary<FileCategory, string>
    {
        [FileCategory.Videos] = "mp4 mkv webm avi mov wmv flv m4v mpg mpeg ts 3gp",
        [FileCategory.Images] = "jpg jpeg png gif webp bmp tif tiff svg heic heif avif ico raw cr2 nef arw dng",
        [FileCategory.Audio] = "mp3 m4a aac flac wav ogg opus wma aiff mid",
        [FileCategory.Documents] = "pdf doc docx xls xlsx ppt pptx odt ods odp rtf txt csv md epub"
    };

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static string DisplayName(FileCategory category) => category == FileCategory.Other ? "Other files" : category.ToString();

    public static List<CategoryFolder> CreateDefaultCategories() =>
        SortedCategories.Select(c => new CategoryFolder
        {
            Category = c,
            Folder = KnownFolders.For(c),
            Extensions = ParseExtensions(DefaultExtensions[c])
        }).ToList();

    public static List<string> DefaultExtensionsFor(FileCategory category) =>
        DefaultExtensions.TryGetValue(category, out var list) ? ParseExtensions(list) : new List<string>();

    /// <summary>Makes a loaded settings object safe: one entry per sorted category, in order, with tidy extension lists.</summary>
    public static void Normalize(AppSettings settings)
    {
        var loaded = settings.Categories ?? new List<CategoryFolder>();
        settings.Categories = SortedCategories.Select(c =>
        {
            var entry = loaded.FirstOrDefault(e => e.Category == c);
            return new CategoryFolder
            {
                Category = c,
                Folder = string.IsNullOrWhiteSpace(entry?.Folder) ? KnownFolders.For(c) : entry.Folder,
                Extensions = entry?.Extensions is null ? DefaultExtensionsFor(c) : ParseExtensions(string.Join(' ', entry.Extensions))
            };
        }).ToList();

        settings.DestinationRules ??= new List<DestinationRule>();
        foreach (var rule in settings.DestinationRules)
        {
            rule.Conditions ??= new List<RuleCondition>();
            rule.Name ??= string.Empty;
            rule.Folder ??= string.Empty;
        }

        if (string.IsNullOrWhiteSpace(settings.DefaultDownloadFolder))
        {
            settings.DefaultDownloadFolder = KnownFolders.Downloads;
        }
    }

    /// <summary>Splits "mp4, .MKV webm" into ["mp4", "mkv", "webm"]; duplicates dropped, order kept.</summary>
    public static List<string> ParseExtensions(string text) =>
        text.Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim().TrimStart('.', '*').ToLowerInvariant())
            .Where(e => e.Length > 0)
            .Distinct()
            .ToList();

    public static FileCategory Categorize(AppSettings settings, string fileName, string? mimeType = null)
    {
        var ext = ExtensionOf(fileName);
        if (ext.Length > 0)
        {
            foreach (var category in settings.Categories)
            {
                if (category.Extensions.Contains(ext))
                {
                    return category.Category;
                }
            }
        }

        // No listed extension: fall back to the server's media type.
        var type = mimeType?.Split('/')[0].ToLowerInvariant();
        return type switch
        {
            "video" => FileCategory.Videos,
            "image" => FileCategory.Images,
            "audio" => FileCategory.Audio,
            _ => FileCategory.Other
        };
    }

    public static string CategoryFolder(AppSettings settings, FileCategory category) =>
        category == FileCategory.Other
            ? settings.DefaultDownloadFolder
            : settings.Categories.FirstOrDefault(c => c.Category == category)?.Folder ?? settings.DefaultDownloadFolder;

    public static DestinationChoice Resolve(AppSettings settings, DownloadFacts facts, DateTimeOffset now, ILoggingService log)
    {
        var category = Categorize(settings, facts.FileName, facts.MimeType);

        foreach (var rule in settings.DestinationRules)
        {
            if (!Matches(rule, facts))
            {
                continue;
            }

            var folder = ExpandFolder(rule.Folder, facts, category, now);
            if (folder is not null && IsUsableFolder(folder))
            {
                log.Debug($"{facts.FileName}: rule '{rule.Name}' matched; saving to {folder}");
                return new DestinationChoice(folder, category, rule.Name);
            }

            log.Warn($"{facts.FileName}: rule '{rule.Name}' matched but its folder '{rule.Folder}' is not usable; trying the next rule");
        }

        if (settings.SortByFileType)
        {
            var folder = CategoryFolder(settings, category);
            if (IsUsableFolder(folder))
            {
                return new DestinationChoice(folder, category, null);
            }

            log.Warn($"{DisplayName(category)} folder '{folder}' is not usable; using the default download folder");
        }

        return new DestinationChoice(DefaultFolder(settings, log), category, null);
    }

    /// <summary>The default download folder, or the Windows Downloads folder when the setting is not usable.</summary>
    public static string DefaultFolder(AppSettings settings, ILoggingService log)
    {
        if (IsUsableFolder(settings.DefaultDownloadFolder))
        {
            return settings.DefaultDownloadFolder;
        }

        log.Warn($"Default download folder '{settings.DefaultDownloadFolder}' is not usable; using {KnownFolders.Downloads}");
        return KnownFolders.Downloads;
    }

    public static bool Matches(DestinationRule rule, DownloadFacts facts)
    {
        if (rule.Conditions.Count == 0)
        {
            return false;
        }

        return rule.MatchAll
            ? rule.Conditions.All(c => Evaluate(c, facts))
            : rule.Conditions.Any(c => Evaluate(c, facts));
    }

    public static bool Evaluate(RuleCondition condition, DownloadFacts facts)
    {
        var value = condition.Value.Trim();
        switch (condition.Field)
        {
            case RuleField.Extension:
            {
                var listed = ParseExtensions(value).Contains(ExtensionOf(facts.FileName));
                return condition.Operator == RuleOperator.IsNot ? !listed : listed;
            }

            case RuleField.FileName:
            {
                var match = WildcardMatch(facts.FileName, value);
                return condition.Operator is RuleOperator.NotMatches or RuleOperator.IsNot ? !match : match;
            }

            case RuleField.Site:
            {
                var host = SiteOf(facts.Url);
                var site = value.ToLowerInvariant().TrimStart('.');
                site = site.StartsWith("www.", StringComparison.Ordinal) ? site[4..] : site;
                return condition.Operator switch
                {
                    RuleOperator.Is => host == site || host.EndsWith("." + site, StringComparison.Ordinal),
                    RuleOperator.IsNot => !(host == site || host.EndsWith("." + site, StringComparison.Ordinal)),
                    RuleOperator.Matches => WildcardMatch(host, site),
                    RuleOperator.NotMatches => !WildcardMatch(host, site),
                    _ => false
                };
            }

            case RuleField.Size:
            {
                if (facts.Size is not { } size || !TryParseSize(value, condition.Unit, out var limit))
                {
                    return false;
                }

                return condition.Operator switch
                {
                    RuleOperator.GreaterThan => size > limit,
                    RuleOperator.LessThan => size < limit,
                    _ => false
                };
            }

            case RuleField.MimeType:
            {
                var mime = facts.MimeType?.ToLowerInvariant();
                var expected = value.ToLowerInvariant();
                return condition.Operator switch
                {
                    RuleOperator.Is => mime == expected,
                    RuleOperator.IsNot => mime != expected,
                    RuleOperator.Matches => mime is not null && WildcardMatch(mime, expected),
                    RuleOperator.NotMatches => mime is null || !WildcardMatch(mime, expected),
                    _ => false
                };
            }

            case RuleField.Source:
            {
                var same = Enum.TryParse<DownloadSource>(value, ignoreCase: true, out var source) && source == facts.Source;
                return condition.Operator == RuleOperator.IsNot ? !same : same;
            }

            case RuleField.VideoSite:
            {
                var same = facts.VideoSite is not null && string.Equals(facts.VideoSite, value, StringComparison.OrdinalIgnoreCase);
                return condition.Operator == RuleOperator.IsNot ? !same : same;
            }

            default:
                return false;
        }
    }

    /// <summary>Replaces placeholders and environment variables; null if the result is not a full path.</summary>
    public static string? ExpandFolder(string template, DownloadFacts facts, FileCategory category, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(template.Trim())
            .Replace("{site}", SafeSegment(SiteOf(facts.Url), "unknown-site"), StringComparison.OrdinalIgnoreCase)
            .Replace("{channel}", SafeSegment(facts.Channel, "Unknown channel"), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{category}", SafeSegment(DisplayName(category), "Other files"), StringComparison.OrdinalIgnoreCase);

        try
        {
            return Path.IsPathFullyQualified(expanded) ? Path.GetFullPath(expanded) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>A full path whose drive or share exists. The folder itself is created when the download starts.</summary>
    public static bool IsUsableFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return false;
        }

        try
        {
            if (!Path.IsPathFullyQualified(folder))
            {
                return false;
            }

            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            return !string.IsNullOrEmpty(root) && Directory.Exists(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static IReadOnlyList<RuleOperator> OperatorsFor(RuleField field) => field switch
    {
        RuleField.Extension or RuleField.Source or RuleField.VideoSite => [RuleOperator.Is, RuleOperator.IsNot],
        RuleField.FileName => [RuleOperator.Matches, RuleOperator.NotMatches],
        RuleField.Site or RuleField.MimeType => [RuleOperator.Is, RuleOperator.IsNot, RuleOperator.Matches],
        RuleField.Size => [RuleOperator.GreaterThan, RuleOperator.LessThan],
        _ => [RuleOperator.Is]
    };

    public static string FieldLabel(RuleField field) => field switch
    {
        RuleField.Extension => "Extension",
        RuleField.FileName => "File name (wildcard)",
        RuleField.Site => "Site (domain)",
        RuleField.Size => "Size",
        RuleField.MimeType => "Type (MIME)",
        RuleField.Source => "Source",
        RuleField.VideoSite => "Video site",
        _ => field.ToString()
    };

    public static string OperatorLabel(RuleOperator op) => op switch
    {
        RuleOperator.Is => "is",
        RuleOperator.IsNot => "is not",
        RuleOperator.Matches => "matches",
        RuleOperator.NotMatches => "doesn't match",
        RuleOperator.GreaterThan => ">",
        RuleOperator.LessThan => "<",
        _ => op.ToString()
    };

    /// <summary>"Extension is iso and size > 1 GB".</summary>
    public static string Describe(DestinationRule rule)
    {
        if (rule.Conditions.Count == 0)
        {
            return "No conditions";
        }

        var joiner = rule.MatchAll ? " and " : " or ";
        var parts = rule.Conditions.Select((c, i) =>
        {
            var field = c.Field switch
            {
                RuleField.FileName => "Name",
                RuleField.Site => "Site",
                RuleField.MimeType => "Type",
                _ => FieldLabel(c.Field)
            };
            if (i > 0)
            {
                field = field.ToLowerInvariant();
            }

            var value = c.Field == RuleField.Size ? $"{c.Value.Trim()} {c.Unit}" : c.Value.Trim();
            return $"{field} {OperatorLabel(c.Operator)} {value}";
        });
        return string.Join(joiner, parts);
    }

    /// <summary>Problems that would stop the rule from working, in plain language; empty when valid.</summary>
    public static List<string> Validate(DestinationRule rule)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            errors.Add("Give the rule a name.");
        }

        if (rule.Conditions.Count == 0)
        {
            errors.Add("Add at least one condition.");
        }

        for (var i = 0; i < rule.Conditions.Count; i++)
        {
            var c = rule.Conditions[i];
            var label = $"Condition {i + 1} ({FieldLabel(c.Field)})";
            if (!OperatorsFor(c.Field).Contains(c.Operator))
            {
                errors.Add($"{label}: choose a comparison.");
            }

            if (string.IsNullOrWhiteSpace(c.Value))
            {
                errors.Add($"{label}: enter a value.");
            }
            else if (c.Field == RuleField.Size && !TryParseSize(c.Value, c.Unit, out _))
            {
                errors.Add($"{label}: enter a number, like 1.5.");
            }
            else if (c.Field == RuleField.Extension && ParseExtensions(c.Value).Count == 0)
            {
                errors.Add($"{label}: enter one or more extensions, like iso img.");
            }
        }

        var sample = ExpandFolder(rule.Folder, new DownloadFacts(new Uri("https://example.com/file"), "file"), FileCategory.Other, DateTimeOffset.Now);
        if (string.IsNullOrWhiteSpace(rule.Folder))
        {
            errors.Add("Choose a folder to save to.");
        }
        else if (sample is null)
        {
            errors.Add("The folder must be a full path, like D:\\ISOs.");
        }

        return errors;
    }

    public static string SiteOf(Uri url)
    {
        var host = url.IdnHost.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    private static string ExtensionOf(string fileName) => Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();

    private static bool TryParseSize(string text, SizeUnit unit, out long bytes)
    {
        bytes = 0;
        var trimmed = text.Trim();
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out var number)
            && !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return false;
        }

        if (number < 0 || double.IsNaN(number) || double.IsInfinity(number))
        {
            return false;
        }

        var multiplier = unit switch
        {
            SizeUnit.KB => 1024d,
            SizeUnit.GB => 1024d * 1024 * 1024,
            _ => 1024d * 1024
        };
        var value = number * multiplier;
        if (value > long.MaxValue)
        {
            return false;
        }

        bytes = (long)value;
        return true;
    }

    private static bool WildcardMatch(string input, string pattern)
    {
        if (pattern.Length == 0)
        {
            return false;
        }

        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        try
        {
            return Regex.IsMatch(input, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string SafeSegment(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        }

        var result = sb.ToString().Trim().TrimEnd('.');
        return result.Length == 0 || result is "." or ".." ? fallback : result;
    }
}
