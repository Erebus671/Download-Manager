namespace DownloadManagerApplet.Models;

public enum FileCategory
{
    Videos,
    Images,
    Audio,
    Documents,
    Other
}

/// <summary>Where a download came from; browser and video-site sources arrive with tasks 8 and 9.</summary>
public enum DownloadSource
{
    Manual,
    Browser,
    VideoSite
}

/// <summary>Folder and extensions for one file-type category. "Other" has no entry; it uses <see cref="AppSettings.DefaultDownloadFolder"/>.</summary>
public sealed class CategoryFolder
{
    public FileCategory Category { get; set; }
    public string Folder { get; set; } = string.Empty;
    public List<string> Extensions { get; set; } = new();
}

public enum RuleField
{
    Extension,
    FileName,
    Site,
    Size,
    MimeType,
    Source,
    VideoSite
}

public enum RuleOperator
{
    Is,
    IsNot,
    Matches,
    NotMatches,
    GreaterThan,
    LessThan
}

public enum SizeUnit
{
    KB,
    MB,
    GB
}

public sealed class RuleCondition
{
    public RuleField Field { get; set; }
    public RuleOperator Operator { get; set; }
    public string Value { get; set; } = string.Empty;
    public SizeUnit Unit { get; set; } = SizeUnit.MB;
}

/// <summary>A custom destination. Rules are checked in list order; the first match wins.</summary>
public sealed class DestinationRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool MatchAll { get; set; } = true;
    public List<RuleCondition> Conditions { get; set; } = new();

    /// <summary>Absolute folder; may contain {site} {channel} {date} {category} and %ENVIRONMENT% variables.</summary>
    public string Folder { get; set; } = string.Empty;
}
