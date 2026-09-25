using System.IO;
using DownloadManagerApplet.Models;
using DownloadManagerApplet.Services;

namespace DownloadManagerApplet.Tests;

internal static class TestStates
{
    /// <summary>A state whose category and default folders all live under <paramref name="root"/>, so tests never touch the real Documents, Videos, etc.</summary>
    public static AppState InFolder(string root)
    {
        var state = new AppState();
        state.Settings.DefaultDownloadFolder = Path.Combine(root, "Other");
        foreach (var category in state.Settings.Categories)
        {
            category.Folder = Path.Combine(root, category.Category.ToString());
        }

        return state;
    }
}

/// <summary>A store whose disk writes always fail, like a full or read-only drive.</summary>
internal sealed class FailingStore : IAppStore
{
    public AppState Load() => new();

    public bool Save(AppState state) => false;
}
