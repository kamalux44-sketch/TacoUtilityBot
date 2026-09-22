using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TacoUtilityBot.Configuration;
using TacoUtilityBot.Models;

namespace TacoUtilityBot.Services;

public sealed class AudioRelayService
{
    private readonly VoiceRelayConfig _config;
    private readonly VoiceRelayState _state;
    private readonly ILogger<AudioRelayService> _logger;
    private readonly object _sync = new();
    private Channel<AudioPacket> _channel;
    private int _queued;

    public AudioRelayService(
        VoiceRelayConfig config,
        VoiceRelayState state,
        ILogger<AudioRelayService> logger)
    {
        _config = config;
        _state = state;
        _logger = logger;
        _channel = CreateChannel();
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_state.IsRunning)
            {
                return;
            }

            _channel = CreateChannel();
            Interlocked.Exchange(ref _queued, 0);
            _state.QueueLength = 0;
            _state.IsRunning = true;
            _logger.LogInformation("Voice relay queue started.");
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _state.IsRunning = false;
            _channel.Writer.TryComplete();
            while (_channel.Reader.TryRead(out _))
            {
            }

            Interlocked.Exchange(ref _queued, 0);
            _state.QueueLength = 0;
            _logger.LogInformation("Voice relay queue stopped.");
        }
    }

    public bool TryEnqueue(AudioPacket packet)
    {
        if (!_state.IsRunning)
        {
            return false;
        }

        while (_channel.Writer.TryWrite(packet) is false)
        {
            if (!_channel.Reader.TryRead(out _))
            {
                return false;
            }

            DecrementQueueLength();
        }

        _state.IncrementReceivedPackets();
        Interlocked.Increment(ref _queued);
        _state.QueueLength = Volatile.Read(ref _queued);
        return true;
    }

    public async ValueTask<AudioPacket> DequeueAsync(CancellationToken cancellationToken)
    {
        var packet = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        DecrementQueueLength();
        _state.IncrementTransmittedPackets();
        return packet;
    }

    private void DecrementQueueLength()
    {
        var length = Interlocked.Decrement(ref _queued);
        _state.QueueLength = Math.Max(0, length);
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
}
