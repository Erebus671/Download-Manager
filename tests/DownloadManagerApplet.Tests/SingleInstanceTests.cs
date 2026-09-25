using System.Buffers.Binary;
using System.IO;
using DownloadManagerApplet.Services;
using DotNetTestKit;

namespace DownloadManagerApplet.Tests;

public class SingleInstanceTests : TestBase
{
    private static string UniqueName() => $"AtraTech.DownloadSolutions.Tests.{Guid.NewGuid():N}";

    [Fact]
    public async Task Codec_RoundTripsUrls()
    {
        using var stream = new MemoryStream();
        var sent = InstanceMessage.FromUrls(["https://example.com/a.zip", "http://example.com/b.iso"]);

        await InstanceMessageCodec.WriteAsync(stream, sent, CancellationToken.None);
        stream.Position = 0;
        var received = await InstanceMessageCodec.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(sent.Version, received.Version);
        Assert.Equal(sent.Urls, received.Urls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(InstanceMessageCodec.MaxPayloadBytes + 1)]
    public async Task Codec_RejectsOutOfRangeLength(int declaredLength)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, declaredLength);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => InstanceMessageCodec.ReadAsync(new MemoryStream(header), CancellationToken.None));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"Version":99,"Urls":[]}""")]
    [InlineData("null")]
    public async Task Codec_RejectsBadPayload(string json)
    {
        await Assert.ThrowsAsync<InvalidDataException>(
            () => InstanceMessageCodec.ReadAsync(Frame(json), CancellationToken.None));
    }

    [Fact]
    public async Task Codec_RejectsTooManyUrls()
    {
        var urls = string.Join(',', Enumerable.Repeat("\"https://example.com/\"", InstanceMessage.MaxUrls + 1));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => InstanceMessageCodec.ReadAsync(Frame($$"""{"Version":1,"Urls":[{{urls}}]}"""), CancellationToken.None));
    }

    [Fact]
    public async Task Codec_RejectsTruncatedPayload()
    {
        var framed = Frame("""{"Version":1,"Urls":[]}""").ToArray();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => InstanceMessageCodec.ReadAsync(new MemoryStream(framed[..^3]), CancellationToken.None));
    }

    [Fact]
    public void Guard_SecondAcquirerIsNotPrimary()
    {
        var name = $@"Local\{UniqueName()}";
        using var first = new SingleInstanceGuard(name, NullLoggingService.Instance);

        // Mutex ownership is per thread and reentrant, so the competing acquirer must run on another thread.
        var secondIsPrimary = RunOnNewThread(() =>
        {
            using var second = new SingleInstanceGuard(name, NullLoggingService.Instance);
            return second.IsPrimary;
        });

        Assert.True(first.IsPrimary);
        Assert.False(secondIsPrimary);
    }

    [Fact]
    public void Guard_ReleasedOnDispose()
    {
        var name = $@"Local\{UniqueName()}";
        new SingleInstanceGuard(name, NullLoggingService.Instance).Dispose();

        var nextIsPrimary = RunOnNewThread(() =>
        {
            using var next = new SingleInstanceGuard(name, NullLoggingService.Instance);
            return next.IsPrimary;
        });

        Assert.True(nextIsPrimary);
    }

    [Fact]
    public async Task ClientAndServer_DeliverUrlsAndReply()
    {
        var pipeName = UniqueName();
        var received = new TaskCompletionSource<InstanceMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new InstancePipeServer(
            pipeName,
            (message, _) =>
            {
                received.TrySetResult(message);
                return Task.FromResult(true);
            },
            NullLoggingService.Instance);
        server.Start();

        var result = await InstancePipeClient.SendAsync(
            pipeName, InstanceMessage.FromUrls(["https://example.com/file.bin"]), NullLoggingService.Instance, TimeSpan.FromSeconds(5));

        Assert.Equal(InstanceSendResult.Accepted, result);
        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["https://example.com/file.bin"], message.Urls);
    }

    [Fact]
    public async Task ClientAndServer_ReportsRejection()
    {
        var pipeName = UniqueName();
        await using var server = new InstancePipeServer(pipeName, (_, _) => Task.FromResult(false), NullLoggingService.Instance);
        server.Start();

        var result = await InstancePipeClient.SendAsync(
            pipeName, InstanceMessage.FromUrls([]), NullLoggingService.Instance, TimeSpan.FromSeconds(5));

        Assert.Equal(InstanceSendResult.Rejected, result);
    }

    [Fact]
    public async Task Server_KeepsServingAfterMalformedClient()
    {
        var pipeName = UniqueName();
        await using var server = new InstancePipeServer(pipeName, (_, _) => Task.FromResult(true), NullLoggingService.Instance);
        server.Start();

        await using (var rogue = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut))
        {
            await rogue.ConnectAsync(5000);
            await rogue.WriteAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F });
            await rogue.FlushAsync();
        }

        var result = await InstancePipeClient.SendAsync(
            pipeName, InstanceMessage.FromUrls([]), NullLoggingService.Instance, TimeSpan.FromSeconds(5));

        Assert.Equal(InstanceSendResult.Accepted, result);
    }

    [Fact]
    public async Task Client_ReportsUnreachable_WhenNoServer()
    {
        var result = await InstancePipeClient.SendAsync(
            UniqueName(), InstanceMessage.FromUrls([]), NullLoggingService.Instance, TimeSpan.FromMilliseconds(300));

        Assert.Equal(InstanceSendResult.Unreachable, result);
    }

    private static MemoryStream Frame(string json)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(json);
        var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        stream.Write(header);
        stream.Write(payload);
        stream.Position = 0;
        return stream;
    }

    private static T RunOnNewThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.Start();
        thread.Join();
        return error is null ? result : throw new AggregateException(error);
    }
}
