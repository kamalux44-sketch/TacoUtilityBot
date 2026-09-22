namespace TacoUtilityBot.Configuration;

public sealed class VoiceRelayConfig
{
    public string ReceiverToken { get; set; } = string.Empty;

    public string TransmitterToken { get; set; } = string.Empty;

    public ulong GuildId { get; set; }

    public ulong ReceiveChannelId { get; set; }

    public ulong TransmitChannelId { get; set; }

    public int MaxQueueSize { get; set; } = 100;
}
