using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using DownloadManagerApplet.Services.Browser;

namespace DownloadManagerApplet.NativeHost;

public enum NativeReadStatus
{
    Message,
    EndOfStream,
    TooLarge,
    Malformed
}

public sealed record NativeReadResult(NativeReadStatus Status, BrowserRequest? Request, string? Error);

/// <summary>Browser Native Messaging framing: 4-byte native-endian (little-endian on Windows) length, then UTF-8 JSON.</summary>
public static class NativeMessagingCodec
{
    /// <summary>Browsers cap host-to-browser messages at 1 MB; the same cap is applied inbound.</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    public static async Task<NativeReadResult> ReadAsync(Stream input, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var headerRead = await ReadFullyAsync(input, header, cancellationToken).ConfigureAwait(false);
        if (headerRead == 0)
        {
            return new NativeReadResult(NativeReadStatus.EndOfStream, null, null);
        }

        if (headerRead < header.Length)
        {
            return new NativeReadResult(NativeReadStatus.EndOfStream, null, "Stream ended inside a message header.");
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > MaxMessageBytes)
        {
            await DiscardAsync(input, length, cancellationToken).ConfigureAwait(false);
            return new NativeReadResult(NativeReadStatus.TooLarge, null, $"Message of {length} bytes exceeds {MaxMessageBytes}.");
        }

        var body = new byte[length];
        if (await ReadFullyAsync(input, body, cancellationToken).ConfigureAwait(false) < body.Length)
        {
            return new NativeReadResult(NativeReadStatus.EndOfStream, null, "Stream ended inside a message body.");
        }

        try
        {
            var request = JsonSerializer.Deserialize<BrowserRequest>(body, BrowserProtocol.ExtensionJson);
            var error = BrowserProtocol.Validate(request);
            return error is null
                ? new NativeReadResult(NativeReadStatus.Message, request, null)
                : new NativeReadResult(NativeReadStatus.Malformed, null, error);
        }
        catch (JsonException ex)
        {
            return new NativeReadResult(NativeReadStatus.Malformed, null, $"Invalid JSON: {ex.Message}");
        }
    }

    public static async Task WriteAsync(Stream output, BrowserResponse response, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(response, BrowserProtocol.ExtensionJson);
        if (body.Length > MaxMessageBytes)
        {
            body = JsonSerializer.SerializeToUtf8Bytes(BrowserResponse.Failed(BrowserRejectReason.AppError, "Reply too large."), BrowserProtocol.ExtensionJson);
        }

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length);
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadFullyAsync(Stream input, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static async Task DiscardAsync(Stream input, long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        while (length > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            length -= read;
        }
    }

    /// <summary>Test helper: frames a raw JSON string.</summary>
    internal static byte[] Frame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var framed = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(framed, (uint)body.Length);
        body.CopyTo(framed, 4);
        return framed;
    }
}
