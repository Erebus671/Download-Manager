using DownloadManagerApplet.NativeHost;
using DownloadManagerApplet.Services;
using DownloadManagerApplet.Services.Browser;

var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DownloadManagerApplet");
var stateReader = new StateFileReader(Path.Combine(appData, "state.json"));
var log = new HostLog(Path.Combine(appData, "logs"));
log.MinimumLevel = stateReader.Read().LogLevel;

var caller = CallerCheck.Describe(args);
if (!CallerCheck.IsAllowed(args))
{
    log.Error($"Refused caller {caller}");
    return 2;
}

log.Debug($"Started by {caller}");

try
{
    var launcher = new AppLauncher(Path.Combine(AppContext.BaseDirectory, AppLauncher.AppExeName), log);
    var handler = new NativeHostHandler(new PipeAppChannel(InstanceNames.PipeName), launcher, stateReader, log);
    await using var input = Console.OpenStandardInput();
    await using var output = Console.OpenStandardOutput();
    return await HostLoop.RunAsync(input, output, handler, log, CancellationToken.None);
}
catch (Exception ex)
{
    log.Error($"Native host failed: {ex}");
    return 1;
}

namespace DownloadManagerApplet.NativeHost
{
    /// <summary>Reads requests until the browser closes stdin (one per launch for sendNativeMessage).</summary>
    public static class HostLoop
    {
        public static async Task<int> RunAsync(Stream input, Stream output, NativeHostHandler handler, IHostLog log, CancellationToken cancellationToken)
        {
            while (true)
            {
                var read = await NativeMessagingCodec.ReadAsync(input, cancellationToken).ConfigureAwait(false);
                switch (read.Status)
                {
                    case NativeReadStatus.EndOfStream:
                        if (read.Error is not null)
                        {
                            log.Warn(read.Error);
                        }

                        return 0;

                    case NativeReadStatus.TooLarge:
                    case NativeReadStatus.Malformed:
                        log.Warn($"Rejected browser message: {read.Error}");
                        await NativeMessagingCodec.WriteAsync(output, BrowserResponse.Failed(BrowserRejectReason.Invalid, read.Error ?? "Invalid message."), cancellationToken).ConfigureAwait(false);
                        continue;
                }

                var request = read.Request!;
                log.Debug($"Request: {BrowserProtocol.Describe(request)}");
                BrowserResponse response;
                try
                {
                    response = await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.Error($"Handling {request.Kind} failed: {ex}");
                    response = BrowserResponse.Failed(BrowserRejectReason.AppError, "The browser connector hit an error; see its log.");
                }

                log.Debug($"Reply: ok={response.Ok} accepted={response.Accepted} reason={response.Reason} appRunning={response.AppRunning}");
                await NativeMessagingCodec.WriteAsync(output, response, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Chromium passes the calling origin ("chrome-extension://id/"); Firefox passes the manifest path and the add-on ID.
    /// Browsers already enforce the manifest allow-lists; this is defense in depth.
    /// </summary>
    public static class CallerCheck
    {
        public static bool IsAllowed(IReadOnlyList<string> args)
        {
            if (args.Count == 0)
            {
                return false;
            }

            const string chromePrefix = "chrome-extension://";
            if (args[0].StartsWith(chromePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var id = args[0][chromePrefix.Length..].TrimEnd('/');
                return BrowserExtensionIds.Chromium.Contains(id, StringComparer.Ordinal);
            }

            return args.Count > 1 && string.Equals(args[1], BrowserExtensionIds.Firefox, StringComparison.Ordinal);
        }

        public static string Describe(IReadOnlyList<string> args) => args.Count switch
        {
            0 => "no caller arguments",
            _ when args[0].StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) => args[0],
            1 => "unknown caller",
            _ => $"Firefox add-on {args[1]}"
        };
    }
}
