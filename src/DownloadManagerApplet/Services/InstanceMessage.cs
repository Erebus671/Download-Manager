using System.Buffers.Binary;
using System.IO;
using System.Text.Json;

namespace DownloadManagerApplet.Services;

/// <summary>Request sent from a secondary process to the running instance. Empty <see cref="Urls"/> = activate only.</summary>
public sealed record InstanceMessage(int Version, IReadOnlyList<string> Urls)
{
    public const int CurrentVersion = 1;
    public const int MaxUrls = 100;
    public const int MaxUrlLength = 8192;

    public static InstanceMessage FromUrls(IEnumerable<string> urls) => new(CurrentVersion, urls.ToList());
}

/// <summary>Wire format: 4-byte little-endian length, then UTF-8 JSON. Reply: one byte, 1 = accepted.</summary>
public static class InstanceMessageCodec
{
    public const int MaxPayloadBytes = 1024 * 1024;
    public const byte Accepted = 1;
    public const byte Rejected = 0;

    private sealed record Dto(int Version, List<string>? Urls);

    public static async Task WriteAsync(Stream stream, InstanceMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dto(message.Version, message.Urls.ToList()));
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

    /// <exception cref="InvalidDataException">Truncated, oversized, malformed, or unsupported message.</exception>
    public static async Task<InstanceMessage> ReadAsync(Stream stream, CancellationToken cancellationToken)
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

        Dto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<Dto>(payload);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Payload is not valid JSON.", ex);
        }

        if (dto is null)
        {
            throw new InvalidDataException("Payload is empty.");
        }

        if (dto.Version != InstanceMessage.CurrentVersion)
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

    internal static string Describe(InstanceMessage message) => $"v{message.Version}, {message.Urls.Count} URL(s)";
}
