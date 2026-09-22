using DSharpPlus;
using DSharpPlus.Entities;
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
    private readonly ILogger<RelayCommand> _logger;

    public RelayCommand(
        AudioRelayService relay,
        ReceiverBotService receiver,
        TransmitterBotService transmitter,
        ILogger<RelayCommand> logger)
    {
        _relay = relay;
        _receiver = receiver;
        _transmitter = transmitter;
        _logger = logger;
    }

    [SlashCommand("start", "指定したVC間の音声リレーを開始します。")]
    public async Task StartAsync(
        InteractionContext context,
        [Option("receive_channel", "音声を受信するVC（リレー元）")] DiscordChannel receiveChannel,
        [Option("transmit_channel", "音声を送信するVC（リレー先）")] DiscordChannel transmitChannel)
    {
        var guild = context.Guild;
        if (guild is null)
        {
            await context.CreateResponseAsync("このコマンドはサーバー内で実行してください。").ConfigureAwait(false);
            return;
        }

        if (receiveChannel.Type != ChannelType.Voice || transmitChannel.Type != ChannelType.Voice)
        {
            await context.CreateResponseAsync("リレー元とリレー先にはボイスチャンネルを指定してください。").ConfigureAwait(false);
            return;
        }

        if (receiveChannel.GuildId != guild.Id || transmitChannel.GuildId != guild.Id)
        {
            await context.CreateResponseAsync("指定したVCは、このサーバーに存在する必要があります。").ConfigureAwait(false);
            return;
        }

        try
        {
            _relay.Start(guild.Id, receiveChannel.Id, transmitChannel.Id);
            await _receiver.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await _receiver.ConnectToReceiveChannelAsync(guild.Id, receiveChannel.Id).ConfigureAwait(false);
            await _transmitter.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await _transmitter.ConnectToTransmitChannelAsync(guild.Id, transmitChannel.Id).ConfigureAwait(false);
            await context.CreateResponseAsync($"音声リレーを開始しました。\n受信VC: {receiveChannel.Name}\n送信VC: {transmitChannel.Name}").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to start voice relay for guild {GuildId}.", guild.Id);
            await StopServicesAsync(guild.Id).ConfigureAwait(false);
            await context.CreateResponseAsync($"リレー開始に失敗しました: {exception.Message}").ConfigureAwait(false);
        }
    }

    [SlashCommand("stop", "このサーバーのVC音声リレーを停止します。")]
    public async Task StopAsync(InteractionContext context)
    {
        if (context.Guild is null)
        {
            await context.CreateResponseAsync("このコマンドはサーバー内で実行してください。").ConfigureAwait(false);
            return;
        }

        try
        {
            await StopServicesAsync(context.Guild.Id).ConfigureAwait(false);
            await context.CreateResponseAsync("このサーバーの音声リレーを停止しました。").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to stop voice relay for guild {GuildId}.", context.Guild.Id);
            await context.CreateResponseAsync($"リレー停止に失敗しました: {exception.Message}").ConfigureAwait(false);
        }
    }

    [SlashCommand("status", "このサーバーのVC音声リレー状態を表示します。")]
    public Task StatusAsync(InteractionContext context)
    {
        if (context.Guild is null)
        {
            return context.CreateResponseAsync("このコマンドはサーバー内で実行してください。");
        }

        if (!_relay.TryGetState(context.Guild.Id, out VoiceRelayState? state) || state is null)
        {
            return context.CreateResponseAsync("このサーバーのリレーは停止中です。");
        }

        var status = state.IsRunning ? "🟢 稼働中" : "⚪ 停止中";
        var message = $"🎙️ VC Relay Status\n状態: {status}\n受信VC: {state.ReceiveChannelId?.ToString() ?? "未接続"}\n送信VC: {state.TransmitChannelId?.ToString() ?? "未接続"}\n受信パケット: {state.ReceivedPackets}\n送信パケット: {state.TransmittedPackets}\nキュー: {state.QueueLength}";
        return context.CreateResponseAsync(message);
    }

    private async Task StopServicesAsync(ulong guildId)
    {
        _relay.Stop(guildId);
        await _transmitter.DisconnectAsync(guildId).ConfigureAwait(false);
        await _receiver.DisconnectAsync(guildId).ConfigureAwait(false);
    }
}