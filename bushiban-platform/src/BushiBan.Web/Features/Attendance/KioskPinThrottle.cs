using System.Collections.Concurrent;

namespace BushiBan.Web.Features.Attendance;

/// <summary>
/// Slows down PIN guessing on the attendance kiosk: after <see cref="MaxFailures"/> wrong PINs within a minute,
/// that kiosk (signed-in account + device address) must wait until the oldest failure is a minute old.
/// In-memory, so it resets when the app restarts; that's fine for a guessing brake.
/// </summary>
public class KioskPinThrottle
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Queue<DateTime>> _failures = new();
    private readonly TimeProvider _time;

    public KioskPinThrottle() : this(TimeProvider.System) { }
    public KioskPinThrottle(TimeProvider time) => _time = time;

    /// <summary>How long the kiosk must wait before trying another PIN, or null when it may try now.</summary>
    public TimeSpan? RetryAfter(string kioskKey)
    {
        if (!_failures.TryGetValue(kioskKey, out var queue)) return null;
        lock (queue)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            Prune(queue, now);
            if (queue.Count < MaxFailures) return null;
            return queue.Peek() + Window - now;
        }
    }

    public void RecordFailure(string kioskKey)
    {
        var queue = _failures.GetOrAdd(kioskKey, _ => new Queue<DateTime>());
        lock (queue)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            Prune(queue, now);
            queue.Enqueue(now);
        }
    }

    private static void Prune(Queue<DateTime> queue, DateTime now)
    {
        while (queue.Count > 0 && now - queue.Peek() >= Window) queue.Dequeue();
    }
}
