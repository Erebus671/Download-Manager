using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using DownloadManagerApplet.Models;

namespace DownloadManagerApplet.Services;

public sealed class HttpDownloadEngine : IDownloadEngine
{
    private const int BufferSize = 81920;
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromMilliseconds(250);

    private readonly HttpClient _httpClient;
    private readonly ILoggingService _log;
    private readonly DestinationPlanner? _planner;

    public HttpDownloadEngine(HttpClient httpClient, ILoggingService log, DestinationPlanner? planner = null)
    {
        _httpClient = httpClient;
        _log = log;
        _planner = planner;
    }

    public async Task DownloadAsync(DownloadItem item, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        var resumeOffset = File.Exists(item.PartFilePath) ? new FileInfo(item.PartFilePath).Length : 0L;

        using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
        if (resumeOffset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeOffset, null);
        }

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            _log.Warn($"{item.FileName}: server rejected resume range at offset {resumeOffset}; restarting from scratch");
            File.Delete(item.PartFilePath);
            resumeOffset = 0;
            await DownloadFromScratchAsync(item, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        var serverHonoredRange = response.StatusCode == HttpStatusCode.PartialContent;
        if (resumeOffset > 0 && !serverHonoredRange)
        {
            _log.Warn($"{item.FileName}: server does not support resume (got {(int)response.StatusCode}); restarting from scratch");
            resumeOffset = 0;
        }

        response.EnsureSuccessStatusCode();
        if (resumeOffset == 0)
        {
            PlanDestination(item, response, cancellationToken);
        }

        Directory.CreateDirectory(item.DestinationFolder);

        var contentLength = response.Content.Headers.ContentLength;
        item.TotalBytes = serverHonoredRange && contentLength.HasValue
            ? resumeOffset + contentLength.Value
            : contentLength;

        var fileMode = resumeOffset > 0 ? FileMode.Append : FileMode.Create;
        {
            await using var fileStream = new FileStream(item.PartFilePath, fileMode, FileAccess.Write, FileShare.Read, BufferSize, useAsync: true);
            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            await CopyWithProgressAsync(responseStream, fileStream, item, resumeOffset, progress, cancellationToken).ConfigureAwait(false);
        }

        Finalize(item);
    }

    private async Task DownloadFromScratchAsync(DownloadItem item, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        PlanDestination(item, response, cancellationToken);
        Directory.CreateDirectory(item.DestinationFolder);

        item.TotalBytes = response.Content.Headers.ContentLength;

        {
            await using var fileStream = new FileStream(item.PartFilePath, FileMode.Create, FileAccess.Write, FileShare.Read, BufferSize, useAsync: true);
            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            await CopyWithProgressAsync(responseStream, fileStream, item, 0, progress, cancellationToken).ConfigureAwait(false);
        }

        Finalize(item);
    }

    /// <summary>Lets the planner settle the final name and folder; removes an empty .part left at the first-guess path.</summary>
    private void PlanDestination(DownloadItem item, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (_planner is null || !item.ResolveOnResponse)
        {
            return;
        }

        var oldPart = item.PartFilePath;
        _planner.ApplyResponse(item, response, cancellationToken);
        if (!string.Equals(oldPart, item.PartFilePath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPart))
        {
            try
            {
                File.Delete(oldPart);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Debug($"Could not remove unused {oldPart}: {ex.Message}");
            }
        }
    }

    private static async Task CopyWithProgressAsync(
        Stream source,
        Stream destination,
        DownloadItem item,
        long startingOffset,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        var totalReceived = startingOffset;
        var stopwatch = Stopwatch.StartNew();
        var bytesSinceLastReport = 0L;
        var lastReportElapsed = TimeSpan.Zero;

        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);

            totalReceived += bytesRead;
            bytesSinceLastReport += bytesRead;
            item.BytesReceived = totalReceived;

            var elapsed = stopwatch.Elapsed;
            var sinceLastReport = elapsed - lastReportElapsed;
            if (sinceLastReport >= ProgressReportInterval)
            {
                var speed = bytesSinceLastReport / sinceLastReport.TotalSeconds;
                progress.Report(new DownloadProgress(totalReceived, item.TotalBytes, speed));
                bytesSinceLastReport = 0;
                lastReportElapsed = elapsed;
            }
        }

        progress.Report(new DownloadProgress(totalReceived, item.TotalBytes, 0));
    }

    private static void Finalize(DownloadItem item)
    {
        // Locked so a rename can't land between the move and the status change (see DestinationPlanner.Rename).
        lock (item)
        {
            // Ensure the destination isn't left over from a previous failed run before the atomic rename.
            if (File.Exists(item.FullPath))
            {
                File.Delete(item.FullPath);
            }

            File.Move(item.PartFilePath, item.FullPath);
            item.PartFileName = null;
            item.Status = DownloadStatus.Completed;
            item.CompletedAt = DateTimeOffset.Now;
        }
    }
}
