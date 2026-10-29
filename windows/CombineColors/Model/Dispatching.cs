using System.Collections.Concurrent;
using System.Windows.Threading;

namespace CombineColors.Model;

/// <summary>
/// Marshals work onto the model's thread. The app wraps the WPF dispatcher; tests pump a queue, so
/// every asynchronous flow of the model can be driven deterministically without a window.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Runs <paramref name="action"/> on the model thread (later, never inline).</summary>
    void Post(Action action);
    /// <summary>Runs <paramref name="action"/> after a delay; disposing the handle cancels it.</summary>
    IDisposable Delay(TimeSpan delay, Action action);
}

public sealed class WpfDispatcher : IUiDispatcher
{
    readonly Dispatcher dispatcher;
    public WpfDispatcher(Dispatcher d) { dispatcher = d; }

    public void Post(Action action) => dispatcher.BeginInvoke(action, DispatcherPriority.Normal);

    public IDisposable Delay(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = delay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : delay };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
        return new Stopper(timer);
    }

    sealed class Stopper : IDisposable
    {
        readonly DispatcherTimer t;
        public Stopper(DispatcherTimer timer) { t = timer; }
        public void Dispose() => t.Stop();
    }
}

/// <summary>Queue-based dispatcher for tests: <see cref="Pump"/> runs what is due.</summary>
public sealed class ManualDispatcher : IUiDispatcher
{
    readonly ConcurrentQueue<Action> queue = new();
    readonly List<(DateTime due, Action action, Handle handle)> timers = new();
    readonly object gate = new();

    public void Post(Action action) => queue.Enqueue(action);

    public IDisposable Delay(TimeSpan delay, Action action)
    {
        var h = new Handle();
        lock (gate) timers.Add((DateTime.UtcNow + delay, action, h));
        return h;
    }

    /// <summary>Runs every queued action and every timer that is due. Returns true when something ran.</summary>
    public bool Pump()
    {
        bool ran = false;
        while (queue.TryDequeue(out var a)) { a(); ran = true; }
        List<Action> due;
        lock (gate)
        {
            var now = DateTime.UtcNow;
            due = timers.Where(t => !t.handle.Cancelled && t.due <= now).Select(t => t.action).ToList();
            timers.RemoveAll(t => t.handle.Cancelled || t.due <= now);
        }
        foreach (var a in due) { a(); ran = true; }
        return ran;
    }

    sealed class Handle : IDisposable
    {
        public volatile bool Cancelled;
        public void Dispose() => Cancelled = true;
    }
}
