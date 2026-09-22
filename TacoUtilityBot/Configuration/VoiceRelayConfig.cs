namespace TacoUtilityBot.Configuration;

public sealed class VoiceRelayConfig
{
    public string ReceiverToken { get; set; } = string.Empty;

    public string TransmitterToken { get; set; } = string.Empty;

    public int MaxQueueSize { get; set; } = 100;
}
