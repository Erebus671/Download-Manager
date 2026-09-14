using System.Windows;
using Xunit;

namespace DownloadManagerApplet.Tests;

/// <summary>
/// Constructs a minimal WPF Application with Themes/Dark.xaml merged in, so converters
/// that call Application.Current.TryFindResource(...) resolve the same brushes the real
/// app would - without this, Application.Current is null outside a running app.
/// </summary>
public sealed class WpfApplicationFixture
{
    public WpfApplicationFixture()
    {
        if (Application.Current is null)
        {
            _ = new Application();
            Application.Current!.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/DownloadManagerApplet;component/Themes/Dark.xaml")
            });
        }
    }
}

[CollectionDefinition("WpfApplication")]
public sealed class WpfApplicationCollection : ICollectionFixture<WpfApplicationFixture>
{
}
