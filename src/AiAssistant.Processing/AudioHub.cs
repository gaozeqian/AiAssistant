using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Processing;

/// <summary>
/// Single-microphone fan-out hub for KWS, VAD and ASR subscribers (ADR-0007).
/// The device is opened only when the first subscriber arrives and closed when the last one leaves.
/// Pre-roll audio is kept exclusively in bounded memory and never persisted.
/// </summary>
public sealed class AudioHub(IAudioCaptureFactory captureFactory, ILogger<AudioHub> logger) : IAudioHub
{
    private const int SubscriberQueueCapacity = 64;
    private const int PreRollSamples = 16_000 * 2; // 2 seconds at 16 kHz
    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, Subscriber> _subscribers = [];
    private readonly Queue<AudioFrame> _preRoll = new();
    private IAudioCapture? _capture;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private int _preRollSampleCount;
    private bool _disposed;

    public CapabilityState Capability => _disposed
        ? CapabilityState.Unavailable(CapabilityKind.AudioCapture, "AudioHub has been disposed")
        : captureFactory.Capability;

    public int ActiveSubscriberCount
    {
        get
        {
            lock (_sync)
            {
                return _subscribers.Count;
            }
        }
    }

    public async IAsyncEnumerable<AudioFrame> SubscribeAsync(
        AudioSubscription subscription,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(SubscriberQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (subscription.IncludePreRollRingBuffer)
            {
                foreach (var frame in _preRoll)
                {
                    channel.Writer.TryWrite(frame);
                }
            }

            _subscribers[id] = new Subscriber(subscription, channel);
            EnsurePumpLocked();
        }

        try
        {
            await foreach (var frame in channel.Reader.ReadAllAsync(ct))
            {
                yield return frame;
            }
        }
        finally
        {
            await RemoveSubscriberAsync(id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Subscriber[] subscribers;
        CancellationTokenSource? pumpCts;
        Task? pumpTask;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            subscribers = _subscribers.Values.ToArray();
            _subscribers.Clear();
            _preRoll.Clear();
            _preRollSampleCount = 0;
            pumpCts = _pumpCts;
            pumpTask = _pumpTask;
            pumpCts?.Cancel();
            _pumpCts = null;
            _pumpTask = null;
            _capture = null;
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.Channel.Writer.TryComplete();
        }

        if (pumpTask is not null)
        {
            try
            {
                await pumpTask;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }
        }
    }

    private void EnsurePumpLocked()
    {
        if (_pumpTask is not null || _subscribers.Count == 0)
        {
            return;
        }

        var capture = captureFactory.Create();
        var cts = new CancellationTokenSource();
        _capture = capture;
        _pumpCts = cts;
        _pumpTask = Task.Run(() => PumpAsync(capture, cts), CancellationToken.None);
    }

    private async Task PumpAsync(IAudioCapture capture, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            await foreach (var frame in capture.ReadFramesAsync(ct))
            {
                // Copy samples because platform capture buffers are reused after the callback returns.
                var owned = frame with { Samples = frame.Samples.ToArray() };
                lock (_sync)
                {
                    if (_disposed || _subscribers.Count == 0)
                    {
                        break;
                    }

                    AppendPreRollLocked(owned);
                    foreach (var subscriber in _subscribers.Values)
                    {
                        subscriber.Channel.Writer.TryWrite(owned);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal stop after last unsubscribe
        }
        catch (Exception ex)
        {
            logger.LogWarning("AudioHub capture stopped with {ExceptionType}.", ex.GetType().Name);
        }
        finally
        {
            try
            {
                await capture.DisposeAsync();
            }
            catch
            {
                // Best-effort device release
            }

            lock (_sync)
            {
                if (ReferenceEquals(_pumpCts, cts))
                {
                    _pumpCts = null;
                    _pumpTask = null;
                    _capture = null;
                    foreach (var subscriber in _subscribers.Values)
                    {
                        subscriber.Channel.Writer.TryComplete();
                    }
                }
            }

            cts.Dispose();
        }
    }

    private async Task RemoveSubscriberAsync(Guid id)
    {
        Task? pumpToStop = null;
        lock (_sync)
        {
            if (!_subscribers.Remove(id, out var subscriber))
            {
                return;
            }

            subscriber.Channel.Writer.TryComplete();
            if (_subscribers.Count == 0)
            {
                _preRoll.Clear();
                _preRollSampleCount = 0;
                pumpToStop = _pumpTask;
                _pumpCts?.Cancel();
                _pumpTask = null;
                _pumpCts = null;
                _capture = null;
            }
        }

        if (pumpToStop is not null)
        {
            try
            {
                await pumpToStop;
            }
            catch (OperationCanceledException)
            {
                // Expected when the last subscriber leaves
            }
        }
    }

    private void AppendPreRollLocked(AudioFrame frame)
    {
        _preRoll.Enqueue(frame);
        _preRollSampleCount += frame.Samples.Length;
        while (_preRollSampleCount > PreRollSamples && _preRoll.TryDequeue(out var removed))
        {
            _preRollSampleCount -= removed.Samples.Length;
        }
    }

    private sealed record Subscriber(
        AudioSubscription Subscription,
        Channel<AudioFrame> Channel);
}
