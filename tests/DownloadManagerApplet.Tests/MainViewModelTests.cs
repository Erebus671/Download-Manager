using System.IO;
using System.Linq;
using DownloadManagerApplet.Services;
using DownloadManagerApplet.ViewModels;
using DotNetTestKit;

namespace DownloadManagerApplet.Tests;

public class MainViewModelTests
{
    [Fact]
    public void AddDownload_DisambiguatesFileName_WhenTwoDownloadsDeriveTheSameName()
    {
        using var temp = new TempDirectory();
        var viewModel = NewViewModel(temp);

        viewModel.NewDownloadUrl = "https://example.com/downloads/report.pdf";
        viewModel.AddDownloadCommand.Execute(null);
        viewModel.NewDownloadUrl = "https://another-host.example.com/files/report.pdf";
        viewModel.AddDownloadCommand.Execute(null);

        // ImmediateSuccessEngine can complete synchronously before this line runs, moving the
        // item from Queue to History - check both so the assertion doesn't race the engine.
        var fileNames = viewModel.Queue.Concat(viewModel.History).Select(vm => vm.FileName).OrderBy(n => n).ToList();
        Assert.Equal(["report (1).pdf", "report.pdf"], fileNames);
    }

    [Fact]
    public void AddDownload_IgnoresInvalidUrl()
    {
        using var temp = new TempDirectory();
        var viewModel = NewViewModel(temp);

        viewModel.NewDownloadUrl = "not a url";
        viewModel.AddDownloadCommand.Execute(null);

        Assert.Empty(viewModel.Queue);
    }

    private static MainViewModel NewViewModel(TempDirectory tempDirectory)
    {
        var state = new AppState();
        state.Settings.DefaultDownloadFolder = tempDirectory.Path;

        var store = new JsonAppStore(Path.Combine(tempDirectory.Path, "state.json"), NullLoggingService.Instance);
        var orchestrator = new DownloadOrchestrator(new ImmediateSuccessEngine(), NullLoggingService.Instance, () => 2, () => 3);
        return new MainViewModel(state, store, orchestrator, NullLoggingService.Instance);
    }
}
