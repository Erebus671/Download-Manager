using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using DownloadManagerApplet.Services.Browser;

namespace DownloadManagerApplet.Services;

/// <summary>
/// Request sent from another process to the running instance.
/// Version 1: URLs from a secondary launch; empty <see cref="Urls"/> = activate only.
/// Version 2: a <see cref="BrowserRequest"/> from the native host.
/// </summary>
public sealed record InstanceMessage(int Version, IReadOnlyList<string> Urls, BrowserRequest? Browser = null)
{
    public const int CurrentVersion = 1;
    public const int BrowserVersion = 2;
    public const int MaxUrls = 100;
    public const int MaxUrlLength = 8192;

    public static InstanceMessage FromUrls(IEnumerable<string> urls) => new(CurrentVersion, urls.ToList());

    public static InstanceMessage FromBrowser(BrowserRequest request) => new(BrowserVersion, [], request);
}

/// <summary>
/// Wire format: 4-byte little-endian length, then UTF-8 JSON.
/// Reply to version 1: one byte, 1 = accepted. Reply to version 2: a length-prefixed <see cref="BrowserResponse"/>.
/// </summary>
public static class InstanceMessageCodec
{
    public const int MaxPayloadBytes = 1024 * 1024;
    public const byte Accepted = 1;
    public const byte Rejected = 0;

    private sealed record Dto(int Version, List<string>? Urls, BrowserRequest? Browser = null);

    public static Task WriteAsync(Stream stream, InstanceMessage message, CancellationToken cancellationToken) =>
        WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new Dto(message.Version, message.Urls.ToList(), message.Browser), BrowserProtocol.PipeJson), cancellationToken);

    public static Task WriteBrowserReplyAsync(Stream stream, BrowserResponse response, CancellationToken cancellationToken) =>
        WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(response, BrowserProtocol.PipeJson), cancellationToken);

    /// <exception cref="InvalidDataException">Truncated, oversized, or malformed reply.</exception>
    public static async Task<BrowserResponse> ReadBrowserReplyAsync(Stream stream, CancellationToken cancellationToken)
    {
        var payload = await ReadFrameAsync(stream, cancellationToken);
        try
        {
            return JsonSerializer.Deserialize<BrowserResponse>(payload, BrowserProtocol.PipeJson)
                   ?? throw new InvalidDataException("Reply is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Reply is not valid JSON.", ex);
        }
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length > MaxPayloadBytes)
        {
            throw new InvalidDataException($"Message is {payload.Length} bytes; limit is {MaxPayloadBytes}.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxPayloadBytes)
        {
            throw new InvalidDataException($"Declared length {length} is outside 1..{MaxPayloadBytes}.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return payload;
    }

    /// <exception cref="InvalidDataException">Truncated, oversized, malformed, or unsupported message.</exception>
    public static async Task<InstanceMessage> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var payload = await ReadFrameAsync(stream, cancellationToken);

        Dto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<Dto>(payload, BrowserProtocol.PipeJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Payload is not valid JSON.", ex);
        }

        if (dto is null)
        {
            throw new InvalidDataException("Payload is empty.");
        }

        if (dto.Version == InstanceMessage.BrowserVersion)
        {
            if (BrowserProtocol.Validate(dto.Browser) is { } problem)
            {
                throw new InvalidDataException($"Invalid browser request: {problem}");
            }

            return InstanceMessage.FromBrowser(dto.Browser!);
        }

        if (dto.Version != InstanceMessage.CurrentVersion || dto.Browser is not null)
        {
            throw new InvalidDataException($"Unsupported version {dto.Version}.");
        }

        var urls = dto.Urls ?? [];
        if (urls.Count > InstanceMessage.MaxUrls)
        {
            throw new InvalidDataException($"{urls.Count} URLs exceeds the limit of {InstanceMessage.MaxUrls}.");
        }

        if (urls.Any(u => u is null || u.Length > InstanceMessage.MaxUrlLength))
        {
            throw new InvalidDataException("A URL is null or too long.");
        }

        return new InstanceMessage(dto.Version, urls);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("Message was truncated.", ex);
        }
    }

    internal static string Describe(InstanceMessage message) => message.Browser is { } browser
        ? $"v{message.Version}, {BrowserProtocol.Describe(browser)}"
        : $"v{message.Version}, {message.Urls.Count} URL(s)";
}
