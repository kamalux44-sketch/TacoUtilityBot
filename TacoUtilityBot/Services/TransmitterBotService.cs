using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.VoiceNext;
using Microsoft.Extensions.Logging;
using TacoUtilityBot.Configuration;

namespace TacoUtilityBot.Services;

public sealed class TransmitterBotService : IAsyncDisposable
{
    private readonly VoiceRelayConfig _config;
    private readonly AudioRelayService _relay;
    private readonly ILogger<TransmitterBotService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DiscordClient _client;
    private readonly Dictionary<ulong, TransmitterConnection> _connections = new();
    private readonly object _sync = new();
    private bool _started;

    public TransmitterBotService(
        VoiceRelayConfig config,
        AudioRelayService relay,
        ILogger<TransmitterBotService> logger,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _relay = relay;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _client = new DiscordClient(new DiscordConfiguration
        {
            Token = config.TransmitterToken,
            TokenType = TokenType.Bot,
            Intents = DiscordIntents.AllUnprivileged | DiscordIntents.GuildVoiceStates,
            LoggerFactory = _loggerFactory
        });
        _client.UseVoiceNext();
    }

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
        _logger.LogInformation("TransmitterBot connected.");
    }

    public async Task ConnectToTransmitChannelAsync(ulong guildId, ulong channelId, CancellationToken cancellationToken = default)
    {
        var channel = await _client.GetChannelAsync(channelId).ConfigureAwait(false);
        if (channel.Type != ChannelType.Voice || channel.GuildId != guildId)
        {
            throw new InvalidOperationException("送信チャンネルは指定したサーバーのボイスチャンネルである必要があります。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await DisconnectAsync(guildId).ConfigureAwait(false);
        var connection = await _client.GetVoiceNext().ConnectAsync(channel).ConfigureAwait(false);
        var sink = connection.GetTransmitSink(20);
        var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sendTask = SendLoopAsync(guildId, sink, sendCts.Token);

        lock (_sync)
        {
            _connections[guildId] = new TransmitterConnection(connection, sendCts, sendTask);
        }

        _logger.LogInformation("TransmitterBot joined transmit channel {ChannelId} in guild {GuildId}.", channel.Id, guildId);
    }

    public async Task DisconnectAsync(ulong guildId)
    {
        TransmitterConnection? transmitterConnection;
        lock (_sync)
        {
            _connections.Remove(guildId, out transmitterConnection);
        }

        if (transmitterConnection is null)
        {
            return;
        }

        await transmitterConnection.Cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await transmitterConnection.SendTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        transmitterConnection.Cancellation.Dispose();
        transmitterConnection.Connection.Disconnect();
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

    private async Task SendLoopAsync(ulong guildId, VoiceTransmitSink sink, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var packet = await _relay.DequeueAsync(guildId, cancellationToken).ConfigureAwait(false);
                await sink.WriteAsync(packet.PcmData, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to send audio for guild {GuildId}.", guildId);
        }
    }

    private void ValidateToken()
    {
        if (string.IsNullOrWhiteSpace(_config.TransmitterToken))
        {
            throw new InvalidOperationException("Discord:TransmitterToken is not configured.");
        }
    }

    private sealed record TransmitterConnection(
        VoiceNextConnection Connection,
        CancellationTokenSource Cancellation,
        Task SendTask);
}