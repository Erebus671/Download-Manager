using System.IO;
using System.Linq;
using DownloadManagerApplet.Services;
using DownloadManagerApplet.ViewModels;
using DotNetTestKit;

namespace DownloadManagerApplet.Tests;

public class MainViewModelTests : TestBase
{
    [Fact]
    public void AddDownload_DisambiguatesFileName_WhenTwoDownloadsDeriveTheSameName()
    {
        var viewModel = NewViewModel();

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
        var viewModel = NewViewModel();

        viewModel.NewDownloadUrl = "not a url";
        viewModel.AddDownloadCommand.Execute(null);

        Assert.Empty(viewModel.Queue);
    }

    [Fact]
    public void AddExternalDownloads_QueuesValidUrlsOnly()
    {
        var viewModel = NewViewModel();

        var added = viewModel.AddExternalDownloads(["https://example.com/a.zip", "ftp://example.com/b.zip", "garbage"]);

        Assert.Equal(1, added);
        Assert.Single(viewModel.Queue.Concat(viewModel.History));
    }

    private MainViewModel NewViewModel()
    {
        var state = TestStates.InFolder(Temp.Path);

        var store = new JsonAppStore(Path.Combine(Temp.Path, "state.json"), NullLoggingService.Instance);
        var orchestrator = new DownloadOrchestrator(new ImmediateSuccessEngine(), NullLoggingService.Instance, () => 2, () => 3);
        return new MainViewModel(state, store, orchestrator, NullLoggingService.Instance, uiDispatcher: () => null);
    }
}
