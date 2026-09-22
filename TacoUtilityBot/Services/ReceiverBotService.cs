using DSharpPlus;
using DSharpPlus.AsyncEvents;
using DSharpPlus.Entities;
using DSharpPlus.VoiceNext;
using DSharpPlus.VoiceNext.EventArgs;
using Microsoft.Extensions.Logging;
using TacoUtilityBot.Configuration;
using TacoUtilityBot.Models;

namespace TacoUtilityBot.Services;

public sealed class ReceiverBotService : IAsyncDisposable
{
    private readonly VoiceRelayConfig _config;
    private readonly AudioRelayService _relay;
    private readonly ILogger<ReceiverBotService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DiscordClient _client;
    private readonly Dictionary<ulong, ReceiverConnection> _connections = new();
    private readonly object _sync = new();
    private bool _started;

    public ReceiverBotService(
        VoiceRelayConfig config,
        AudioRelayService relay,
        ILogger<ReceiverBotService> logger,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _relay = relay;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _client = new DiscordClient(new DiscordConfiguration
        {
            Token = config.ReceiverToken,
            TokenType = TokenType.Bot,
            Intents = DiscordIntents.AllUnprivileged | DiscordIntents.GuildVoiceStates,
            LoggerFactory = _loggerFactory
        });
        _client.UseVoiceNext(new VoiceNextConfiguration
        {
            EnableIncoming = true
        });
    }

    public DiscordClient Client => _client;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        ValidateToken();
        cancellationToken.ThrowIfCancellationRequested();
        await _client.ConnectAsync(new DiscordActivity("Voice Relay")).ConfigureAwait(false);
        _started = true;
        _logger.LogInformation("ReceiverBot connected.");
    }

    public async Task ConnectToReceiveChannelAsync(ulong guildId, ulong channelId, CancellationToken cancellationToken = default)
    {
        var channel = await _client.GetChannelAsync(channelId).ConfigureAwait(false);
        if (channel.Type != ChannelType.Voice || channel.GuildId != guildId)
        {
            throw new InvalidOperationException("受信チャンネルは指定したサーバーのボイスチャンネルである必要があります。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await DisconnectAsync(guildId).ConfigureAwait(false);
        var connection = await _client.GetVoiceNext().ConnectAsync(channel).ConfigureAwait(false);
        AsyncEventHandler<VoiceNextConnection, VoiceReceiveEventArgs> handler =
            (voiceConnection, eventArgs) => OnVoiceReceivedAsync(guildId, voiceConnection, eventArgs);
        connection.VoiceReceived += handler;

        lock (_sync)
        {
            _connections[guildId] = new ReceiverConnection(connection, handler);
        }

        _logger.LogInformation("ReceiverBot joined receive channel {ChannelId} in guild {GuildId}.", channel.Id, guildId);
    }

    public Task DisconnectAsync(ulong guildId)
    {
        ReceiverConnection? receiverConnection;
        lock (_sync)
        {
            _connections.Remove(guildId, out receiverConnection);
        }

        if (receiverConnection is not null)
        {
            try
            {
                receiverConnection.Connection.VoiceReceived -= receiverConnection.Handler;
                receiverConnection.Connection.Disconnect();
            }
            finally
            {
                try
                {
                    receiverConnection.Connection.Dispose();
                }
                catch (Exception exception)
                {
                    _logger.LogDebug(exception, "VoiceNext receiver connection disposal failed for guild {GuildId}.", guildId);
                }
            }
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        ulong[] guildIds;
        lock (_sync)
        {
            guildIds = _connections.Keys.ToArray();
        }

        foreach (var guildId in guildIds)
        {
            await DisconnectAsync(guildId).ConfigureAwait(false);
        }

        await _client.DisconnectAsync().ConfigureAwait(false);
        _client.Dispose();
    }

    private Task OnVoiceReceivedAsync(ulong guildId, VoiceNextConnection connection, VoiceReceiveEventArgs eventArgs)
    {
        if (eventArgs.PcmData.Length == 0)
        {
            return Task.CompletedTask;
        }

        _relay.TryEnqueue(guildId, new AudioPacket
        {
            PcmData = eventArgs.PcmData.ToArray(),
            UserId = eventArgs.User?.Id,
            Ssrc = eventArgs.SSRC,
            Timestamp = DateTimeOffset.UtcNow
        });

        return Task.CompletedTask;
    }

    private void ValidateToken()
    {
        if (string.IsNullOrWhiteSpace(_config.ReceiverToken))
        {
            throw new InvalidOperationException("Discord:ReceiverToken is not configured.");
        }
    }

    private sealed record ReceiverConnection(
        VoiceNextConnection Connection,
        AsyncEventHandler<VoiceNextConnection, VoiceReceiveEventArgs> Handler);
}