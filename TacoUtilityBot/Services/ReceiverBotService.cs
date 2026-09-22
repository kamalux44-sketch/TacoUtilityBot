using DSharpPlus;
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
    private readonly VoiceRelayState _state;
    private readonly ILogger<ReceiverBotService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DiscordClient _client;
    private bool _started;
    private VoiceNextConnection? _connection;

    public ReceiverBotService(
        VoiceRelayConfig config,
        AudioRelayService relay,
        VoiceRelayState state,
        ILogger<ReceiverBotService> logger,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _relay = relay;
        _state = state;
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

    public bool IsConnected => _connection is not null;

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

    public async Task ConnectToReceiveChannelAsync(CancellationToken cancellationToken = default)
    {
        var channel = await _client.GetChannelAsync(_config.ReceiveChannelId).ConfigureAwait(false);
        if (channel.Type != ChannelType.Voice)
        {
            throw new InvalidOperationException($"Receive channel {_config.ReceiveChannelId} is not a voice channel.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _connection = await _client.GetVoiceNext().ConnectAsync(channel).ConfigureAwait(false);
        _connection.VoiceReceived += OnVoiceReceivedAsync;
        _state.ReceiveChannelId = channel.Id;
        _logger.LogInformation("ReceiverBot joined receive channel {ChannelId}.", channel.Id);
    }

    public Task DisconnectAsync()
    {
        if (_connection is not null)
        {
            _connection.VoiceReceived -= OnVoiceReceivedAsync;
            _connection.Disconnect();
            _connection = null;
        }

        _state.ReceiveChannelId = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        await _client.DisconnectAsync().ConfigureAwait(false);
        _started = false;
        _client.Dispose();
    }

    private Task OnVoiceReceivedAsync(VoiceNextConnection connection, VoiceReceiveEventArgs eventArgs)
    {
        if (eventArgs.PcmData.Length == 0)
        {
            return Task.CompletedTask;
        }

        _relay.TryEnqueue(new AudioPacket
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
}
