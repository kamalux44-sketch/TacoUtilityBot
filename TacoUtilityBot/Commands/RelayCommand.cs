using DSharpPlus.SlashCommands;
using Microsoft.Extensions.Logging;
using TacoUtilityBot.Models;
using TacoUtilityBot.Services;

namespace TacoUtilityBot.Commands;

[SlashCommandGroup("relay", "VC音声リレーを操作します。")]
public sealed class RelayCommand : ApplicationCommandModule
{
    private readonly AudioRelayService _relay;
    private readonly ReceiverBotService _receiver;
    private readonly TransmitterBotService _transmitter;
    private readonly VoiceRelayState _state;
    private readonly ILogger<RelayCommand> _logger;

    public RelayCommand(
        AudioRelayService relay,
        ReceiverBotService receiver,
        TransmitterBotService transmitter,
        VoiceRelayState state,
        ILogger<RelayCommand> logger)
    {
        _relay = relay;
        _receiver = receiver;
        _transmitter = transmitter;
        _state = state;
        _logger = logger;
    }

    [SlashCommand("start", "VC AからVC Bへの音声リレーを開始します。")]
    public async Task StartAsync(InteractionContext context)
    {
        if (_state.IsRunning)
        {
            await context.CreateResponseAsync("リレーは既に稼働中です。").ConfigureAwait(false);
            return;
        }

        try
        {
            _relay.Start();
            await _receiver.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await _receiver.ConnectToReceiveChannelAsync().ConfigureAwait(false);
            await _transmitter.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await _transmitter.ConnectToTransmitChannelAsync().ConfigureAwait(false);
            await context.CreateResponseAsync("音声リレーを開始しました。").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to start voice relay.");
            await StopServicesAsync().ConfigureAwait(false);
            await context.CreateResponseAsync($"リレー開始に失敗しました: {exception.Message}").ConfigureAwait(false);
        }
    }

    [SlashCommand("stop", "VC音声リレーを停止します。")]
    public async Task StopAsync(InteractionContext context)
    {
        try
        {
            await StopServicesAsync().ConfigureAwait(false);
            await context.CreateResponseAsync("音声リレーを停止しました。").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to stop voice relay.");
            await context.CreateResponseAsync($"リレー停止に失敗しました: {exception.Message}").ConfigureAwait(false);
        }
    }

    [SlashCommand("status", "VC音声リレーの状態を表示します。")]
    public Task StatusAsync(InteractionContext context)
    {
        var status = _state.IsRunning ? "🟢 稼働中" : "⚪ 停止中";
        var message = $"🎙️ VC Relay Status\n状態: {status}\n受信VC: {_state.ReceiveChannelId?.ToString() ?? "未接続"}\n送信VC: {_state.TransmitChannelId?.ToString() ?? "未接続"}\n受信パケット: {_state.ReceivedPackets}\n送信パケット: {_state.TransmittedPackets}\nキュー: {_state.QueueLength}";
        return context.CreateResponseAsync(message);
    }

    private async Task StopServicesAsync()
    {
        await _transmitter.DisconnectAsync().ConfigureAwait(false);
        await _receiver.DisconnectAsync().ConfigureAwait(false);
        _relay.Stop();
    }
}
