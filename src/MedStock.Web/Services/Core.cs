namespace MedStock.Web.Services;

[Flags]
public enum Topic { None = 0, Stock = 1, Orders = 2, Purchasing = 4, Alerts = 8, Activity = 16, All = 31 }

public enum ActivityKind { Order, Shipment, Receipt, Transfer, Alert, System }

public sealed record ActivityEvent(long Id, DateTime Utc, ActivityKind Kind, string Message, Topic Topics, string Severity);

/// <summary>
/// Singleton in-process event hub. Services publish, Blazor components subscribe (and marshal to their
/// renderer with InvokeAsync). Real-time push to the browser is the Blazor Server SignalR circuit.
/// </summary>
public sealed class InventoryEventHub(TimeProvider clock)
{
    private const int Capacity = 100;
    private readonly LinkedList<ActivityEvent> _recent = new();
    private readonly Lock _lock = new();
    private long _seq;

    public event Action<ActivityEvent>? Published;

    /// <summary>When true (startup seeding) events are not published.</summary>
    public bool Muted { get; set; }

    public void Publish(ActivityKind kind, string message, Topic topics, string severity = "info")
    {
        if (Muted) return;
        ActivityEvent e;
        lock (_lock)
        {
            e = new ActivityEvent(++_seq, clock.GetUtcNow().UtcDateTime, kind, message, topics | Topic.Activity, severity);
            _recent.AddFirst(e);
            while (_recent.Count > Capacity) _recent.RemoveLast();
        }
        var handlers = Published;
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList().Cast<Action<ActivityEvent>>())
        {
            try { h(e); } catch { /* a faulty or disposed subscriber must never break the publisher */ }
        }
    }

    public IReadOnlyList<ActivityEvent> Recent(int count)
    {
        lock (_lock) return _recent.Take(count).ToList();
    }

    public void Clear()
    {
        lock (_lock) _recent.Clear();
    }
}

/// <summary>Serializes all database writes (SQLite allows a single writer) so multi-step domain operations stay atomic.</summary>
public sealed class WriteGate
{
    private readonly SemaphoreSlim _sem = new(1, 1);

    public async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        await _sem.WaitAsync();
        try { return await action(); }
        finally { _sem.Release(); }
    }
}

/// <summary>Runtime switch for the background simulator (on/off, plus temporary suspension during a data reset).</summary>
public sealed class SimulationControl
{
    private int _suspended;
    public bool Enabled { get; private set; }
    public bool Active => Enabled && Volatile.Read(ref _suspended) == 0;
    public event Action? Changed;

    public SimulationControl(IConfiguration cfg) => Enabled = cfg.GetValue("Simulation:Enabled", true);

    public void SetEnabled(bool enabled)
    {
        if (Enabled == enabled) return;
        Enabled = enabled;
        Changed?.Invoke();
    }

    public IDisposable Suspend()
    {
        Interlocked.Increment(ref _suspended);
        return new Releaser(this);
    }

    private sealed class Releaser(SimulationControl owner) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref owner._suspended);
        }
    }
}

public sealed record OpResult(bool Ok, string? Error = null)
{
    public static OpResult Success() => new(true);
    public static OpResult Fail(string error) => new(false, error);
}

public sealed record OpResult<T>(bool Ok, T? Value, string? Error = null)
{
    public static OpResult<T> Success(T value) => new(true, value);
    public static OpResult<T> Fail(string error) => new(false, default, error);
}
