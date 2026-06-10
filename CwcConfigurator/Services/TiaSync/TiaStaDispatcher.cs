using System.Threading;

namespace CwcConfigurator.Services.TiaSync;

public sealed class TiaStaDispatcher : IDisposable
{
    private static readonly Lazy<TiaStaDispatcher> SharedInstance = new(() => new TiaStaDispatcher());

    private readonly object _gate = new();
    private readonly Queue<Action> _highQueue = new();
    private readonly Queue<Action> _normalQueue = new();
    private readonly SemaphoreSlim _pending = new(0, int.MaxValue);
    private readonly Thread _workerThread;
    private readonly ManualResetEventSlim _workerReady = new();
    private int _workerThreadId;
    private bool _disposed;

    public static TiaStaDispatcher Shared => SharedInstance.Value;

    private TiaStaDispatcher()
    {
        _workerThread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "CWC TIA STA Worker"
        };
        _workerThread.SetApartmentState(ApartmentState.STA);
        _workerThread.Start();
        _workerReady.Wait();
    }

    public T Invoke<T>(Func<T> func)
    {
        return InvokeAsync(func, highPriority: true).GetAwaiter().GetResult();
    }

    public Task<T> InvokeAsync<T>(Func<T> func, bool highPriority = true)
    {
        ArgumentNullException.ThrowIfNull(func);

        if (Thread.CurrentThread.ManagedThreadId == _workerThreadId)
        {
            return Task.FromResult(func());
        }

        var tcs = new TaskCompletionSource<T>();
        Enqueue(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }, highPriority);

        return tcs.Task;
    }

    public Task InvokeAsync(Action action, bool highPriority = true)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeAsync(() =>
        {
            action();
            return true;
        }, highPriority);
    }

    public int PendingHighPriority
    {
        get
        {
            lock (_gate)
            {
                return _highQueue.Count;
            }
        }
    }

    private void RunLoop()
    {
        _workerThreadId = Thread.CurrentThread.ManagedThreadId;
        _workerReady.Set();

        while (!_disposed)
        {
            try
            {
                _pending.Wait();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            Action? work = null;
            lock (_gate)
            {
                if (_highQueue.Count > 0)
                {
                    work = _highQueue.Dequeue();
                }
                else if (_normalQueue.Count > 0)
                {
                    work = _normalQueue.Dequeue();
                }
            }

            try
            {
                work?.Invoke();
            }
            catch
            {
            }
        }
    }

    private void Enqueue(Action action, bool highPriority)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TiaStaDispatcher));
        }

        lock (_gate)
        {
            if (highPriority)
            {
                _highQueue.Enqueue(action);
            }
            else
            {
                _normalQueue.Enqueue(action);
            }
        }

        _pending.Release();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _pending.Release();
        }
        catch
        {
        }

        _workerReady.Dispose();
        _pending.Dispose();
    }
}
