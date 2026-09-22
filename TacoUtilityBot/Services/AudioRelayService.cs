using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TacoUtilityBot.Configuration;
using TacoUtilityBot.Models;

namespace TacoUtilityBot.Services;

public sealed class AudioRelayService
{
    private readonly VoiceRelayConfig _config;
    private readonly ILogger<AudioRelayService> _logger;
    private readonly ConcurrentDictionary<ulong, RelaySession> _sessions = new();

    public AudioRelayService(VoiceRelayConfig config, ILogger<AudioRelayService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public void StopAll()
    {
        foreach (var guildId in _sessions.Keys)
        {
            Stop(guildId);
        }
    }

    public VoiceRelayState Start(ulong guildId, ulong receiveChannelId, ulong transmitChannelId)
    {
        var session = _sessions.GetOrAdd(
            guildId,
            _ => new RelaySession(guildId, _config.MaxQueueSize, _logger));

        lock (session.Sync)
        {
            if (session.State.IsRunning)
            {
                throw new InvalidOperationException("このサーバーでは既にリレーが稼働中です。");
            }

            session.Channel = CreateChannel();
            session.Queued = 0;
            session.State.ReceiveChannelId = receiveChannelId;
            session.State.TransmitChannelId = transmitChannelId;
            session.State.QueueLength = 0;
            session.State.IsRunning = true;
            _logger.LogInformation("Voice relay queue started for guild {GuildId}.", guildId);
            return session.State;
        }
    }

    public bool TryGetState(ulong guildId, out VoiceRelayState? state)
    {
        if (_sessions.TryGetValue(guildId, out var session))
        {
            state = session.State;
            return true;
        }

        state = null;
        return false;
    }

    public void Stop(ulong guildId)
    {
        if (!_sessions.TryGetValue(guildId, out var session))
        {
            return;
        }

        lock (session.Sync)
        {
            session.State.IsRunning = false;
            session.Channel.Writer.TryComplete();
            while (session.Channel.Reader.TryRead(out _))
            {
            }

            Interlocked.Exchange(ref session.Queued, 0);
            session.State.QueueLength = 0;
            session.State.ReceiveChannelId = null;
            session.State.TransmitChannelId = null;
            _logger.LogInformation("Voice relay queue stopped for guild {GuildId}.", guildId);
        }
    }

    public bool TryEnqueue(ulong guildId, AudioPacket packet)
    {
        if (!_sessions.TryGetValue(guildId, out var session) || !session.State.IsRunning)
        {
            return false;
        }

        while (!session.Channel.Writer.TryWrite(packet))
        {
            if (!session.Channel.Reader.TryRead(out _))
            {
                return false;
            }

            DecrementQueueLength(session);
        }

        session.State.IncrementReceivedPackets();
        Interlocked.Increment(ref session.Queued);
        session.State.QueueLength = Volatile.Read(ref session.Queued);
        return true;
    }

    public async ValueTask<AudioPacket> DequeueAsync(ulong guildId, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(guildId, out var session))
        {
            throw new InvalidOperationException("このサーバーのリレーセッションが存在しません。");
        }

        var packet = await session.Channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        DecrementQueueLength(session);
        session.State.IncrementTransmittedPackets();
        return packet;
    }

    private static void DecrementQueueLength(RelaySession session)
    {
        var length = Interlocked.Decrement(ref session.Queued);
        session.State.QueueLength = Math.Max(0, length);
    }

    private Channel<AudioPacket> CreateChannel()
    {
        return Channel.CreateBounded<AudioPacket>(new BoundedChannelOptions(Math.Max(1, _config.MaxQueueSize))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    private sealed class RelaySession
    {
        public RelaySession(ulong guildId, int maxQueueSize, ILogger logger)
        {
            State = new VoiceRelayState { GuildId = guildId };
            Channel = System.Threading.Channels.Channel.CreateBounded<AudioPacket>(new BoundedChannelOptions(Math.Max(1, maxQueueSize))
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        }

        public object Sync { get; } = new();

        public VoiceRelayState State { get; }

        public Channel<AudioPacket> Channel { get; set; }

        public int Queued;
    }
}