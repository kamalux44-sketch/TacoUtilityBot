namespace TacoUtilityBot.Models;

public sealed class VoiceRelayState
{
    public bool IsRunning { get; internal set; }

    public ulong? ReceiveChannelId { get; internal set; }

    public ulong? TransmitChannelId { get; internal set; }

    public long ReceivedPackets { get; internal set; }

    public long TransmittedPackets { get; internal set; }

    public int QueueLength { get; internal set; }

    internal void IncrementReceivedPackets() => ReceivedPackets++;

    internal void IncrementTransmittedPackets() => TransmittedPackets++;

    internal void Reset()
    {
        IsRunning = false;
        ReceiveChannelId = null;
        TransmitChannelId = null;
        ReceivedPackets = 0;
        TransmittedPackets = 0;
        QueueLength = 0;
    }
}
