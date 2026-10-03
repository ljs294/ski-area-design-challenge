#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MountainPlanner.Acquisition.Picker
{
    /// <summary>A monotonic clock and a delay, injected so rate limits are testable without waiting.</summary>
    public interface IPickerClock
    {
        TimeSpan Now { get; }
        Task Delay(TimeSpan delay, CancellationToken ct);
    }

    /// <summary>The real clock: a stopwatch and <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
    public sealed class SystemPickerClock : IPickerClock
    {
        public static readonly SystemPickerClock Instance = new SystemPickerClock();
        readonly Stopwatch _watch = Stopwatch.StartNew();
        public TimeSpan Now => _watch.Elapsed;
        public Task Delay(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
    }

    /// <summary>
    /// Spaces requests to one service at least <see cref="Interval"/> apart (Nominatim: at most one
    /// request per second, 0.3 §6). Callers queue in order; a cancelled caller gives up its turn.
    /// </summary>
    public sealed class RateGate
    {
        public readonly TimeSpan Interval;
        readonly IPickerClock _clock;
        readonly SemaphoreSlim _turn = new SemaphoreSlim(1, 1);
        TimeSpan? _last;

        public RateGate(TimeSpan interval, IPickerClock clock)
        {
            Interval = interval;
            _clock = clock;
        }

        /// <summary>Waits until a request may start and records its start time.</summary>
        public async Task WaitAsync(CancellationToken ct)
        {
            await _turn.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_last.HasValue)
                {
                    var wait = _last.Value + Interval - _clock.Now;
                    if (wait > TimeSpan.Zero) await _clock.Delay(wait, ct).ConfigureAwait(false);
                }
                _last = _clock.Now;
            }
            finally
            {
                _turn.Release();
            }
        }
    }
}
