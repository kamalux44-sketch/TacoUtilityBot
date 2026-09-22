using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.VoiceNext;
using Microsoft.Extensions.Logging;
using TacoUtilityBot.Configuration;
using TacoUtilityBot.Models;

namespace TacoUtilityBot.Services;

public sealed class TransmitterBotService : IAsyncDisposable
{
    private readonly VoiceRelayConfig _config;
    private readonly AudioRelayService _relay;
    private readonly VoiceRelayState _state;
    private readonly ILogger<TransmitterBotService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DiscordClient _client;
    private bool _started;
    private readonly object _sync = new();
    private VoiceNextConnection? _connection;
    private CancellationTokenSource? _sendCts;
    private Task? _sendTask;

    public TransmitterBotService(
        VoiceRelayConfig config,
        AudioRelayService relay,
        VoiceRelayState state,
        ILogger<TransmitterBotService> logger,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _relay = relay;
        _state = state;
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

    public bool IsConnected => _connection is not null;

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

    public async Task ConnectToTransmitChannelAsync(CancellationToken cancellationToken = default)
    {
        var channel = await _client.GetChannelAsync(_config.TransmitChannelId).ConfigureAwait(false);
        if (channel.Type != ChannelType.Voice)
        {
            throw new InvalidOperationException($"Transmit channel {_config.TransmitChannelId} is not a voice channel.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _connection = await _client.GetVoiceNext().ConnectAsync(channel).ConfigureAwait(false);
        var sink = _connection.GetTransmitSink(20);
        _state.TransmitChannelId = channel.Id;
        lock (_sync)
        {
            _sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _sendTask = SendLoopAsync(sink, _sendCts.Token);
        }

        _logger.LogInformation("TransmitterBot joined transmit channel {ChannelId}.", channel.Id);
    }

    public async Task DisconnectAsync()
    {
        CancellationTokenSource? sendCts;
        Task? sendTask;
        lock (_sync)
        {
            sendCts = _sendCts;
            sendTask = _sendTask;
            _sendCts = null;
            _sendTask = null;
        }

        if (sendCts is not null)
        {
            await sendCts.CancelAsync().ConfigureAwait(false);
            if (sendTask is not null)
            {
                try
                {
                    await sendTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            sendCts.Dispose();
        }

        if (_connection is not null)
        {
            _connection.Disconnect();
            _connection = null;
        }

        _state.TransmitChannelId = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        await _client.DisconnectAsync().ConfigureAwait(false);
        _started = false;
        _client.Dispose();
    }

    private async Task SendLoopAsync(VoiceTransmitSink sink, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var packet = await _relay.DequeueAsync(cancellationToken).ConfigureAwait(false);
                await sink.WriteAsync(packet.PcmData, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to send audio.");
        }
    }

    private void ValidateToken()
    {
        if (string.IsNullOrWhiteSpace(_config.TransmitterToken))
        {
            throw new InvalidOperationException("Discord:TransmitterToken is not configured.");
        }
    }
}
