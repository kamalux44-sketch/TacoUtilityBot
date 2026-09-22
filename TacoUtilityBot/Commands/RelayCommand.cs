using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _guildOperations = new();

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
        await context.DeferAsync().ConfigureAwait(false);
        var guild = context.Guild;
        if (guild is null)
        {
            await EditResponseAsync(context, "このコマンドはサーバー内で実行してください。").ConfigureAwait(false);
            return;
        }

        if (receiveChannel.Type != ChannelType.Voice || transmitChannel.Type != ChannelType.Voice)
        {
            await EditResponseAsync(context, "リレー元とリレー先にはボイスチャンネルを指定してください。").ConfigureAwait(false);
            return;
        }

        if (receiveChannel.GuildId != guild.Id || transmitChannel.GuildId != guild.Id)
        {
            await EditResponseAsync(context, "指定したVCは、このサーバーに存在する必要があります。").ConfigureAwait(false);
            return;
        }

        var operation = _guildOperations.GetOrAdd(guild.Id, _ => new SemaphoreSlim(1, 1));
        await operation.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_relay.TryGetState(guild.Id, out VoiceRelayState? existingState) && existingState?.IsRunning == true)
            {
                await EditResponseAsync(context, "このサーバーでは既にリレーが稼働中です。").ConfigureAwait(false);
                return;
            }

            _relay.Start(guild.Id, receiveChannel.Id, transmitChannel.Id);
            await _receiver.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await _receiver.ConnectToReceiveChannelAsync(guild.Id, receiveChannel.Id).ConfigureAwait(false);
            await _transmitter.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await _transmitter.ConnectToTransmitChannelAsync(guild.Id, transmitChannel.Id).ConfigureAwait(false);
            await EditResponseAsync(context, $"音声リレーを開始しました。\n受信VC: {receiveChannel.Name}\n送信VC: {transmitChannel.Name}").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to start voice relay for guild {GuildId}.", guild.Id);
            await StopServicesAsync(guild.Id).ConfigureAwait(false);
            await EditResponseAsync(context, $"リレー開始に失敗しました: {exception.Message}").ConfigureAwait(false);
        }
        finally
        {
            operation.Release();
        }
    }

    [SlashCommand("stop", "このサーバーのVC音声リレーを停止します。")]
    public async Task StopAsync(InteractionContext context)
    {
        await context.DeferAsync().ConfigureAwait(false);
        if (context.Guild is null)
        {
            await EditResponseAsync(context, "このコマンドはサーバー内で実行してください。").ConfigureAwait(false);
            return;
        }

        var operation = _guildOperations.GetOrAdd(context.Guild.Id, _ => new SemaphoreSlim(1, 1));
        await operation.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopServicesAsync(context.Guild.Id).ConfigureAwait(false);
            await EditResponseAsync(context, "このサーバーの音声リレーを停止しました。").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to stop voice relay for guild {GuildId}.", context.Guild.Id);
            await EditResponseAsync(context, $"リレー停止に失敗しました: {exception.Message}").ConfigureAwait(false);
        }
        finally
        {
            operation.Release();
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
        try
        {
            await _transmitter.DisconnectAsync(guildId).ConfigureAwait(false);
        }
        finally
        {
            await _receiver.DisconnectAsync(guildId).ConfigureAwait(false);
        }
    }

    private static Task EditResponseAsync(InteractionContext context, string message)
    {
        return context.EditResponseAsync(new DiscordWebhookBuilder().WithContent(message));
    }
}