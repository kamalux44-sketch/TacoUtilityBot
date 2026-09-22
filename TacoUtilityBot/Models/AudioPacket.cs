namespace TacoUtilityBot.Models;

public sealed class AudioPacket
{
    public required byte[] PcmData { get; init; }

    public ulong? UserId { get; init; }

    public uint? Ssrc { get; init; }

    public DateTimeOffset Timestamp { get; init; }
}
