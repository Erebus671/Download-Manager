namespace DownloadManagerApplet.Services.Browser;

/// <summary>Extension identities the native host accepts. Store IDs are added once each store assigns one.</summary>
public static class BrowserExtensionIds
{
    /// <summary>Unpacked/dev ID, fixed by the "key" in the Chromium manifest.</summary>
    public const string ChromiumDev = "appmhpafdcnnfglmahilhiffokijfpjb";

    public const string Firefox = "downloadsolutions@atratech";

    public static readonly IReadOnlyList<string> Chromium = [ChromiumDev];

    public const string GetExtensionUrl = "https://github.com/Erebus671/Download-Manager#browser-extension";
}
