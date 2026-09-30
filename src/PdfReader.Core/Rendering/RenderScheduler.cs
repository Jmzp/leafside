namespace PdfReader.Core.Rendering;

/// <summary>
/// Runs rendering work on a dedicated background thread, most urgent first (lowest priority value).
/// Cancelled jobs never run, so jobs for pages that scrolled away cost nothing. Awaiting the returned
/// task resumes on the caller's synchronization context (the UI thread), keeping the UI thread free
/// of any rasterization.
/// </summary>
public sealed class RenderScheduler : IDisposable
{
    private readonly PriorityQueue<Job, (double Priority, long Sequence)> _queue = new();
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Thread _thread;
    private long _sequence;

    private abstract class Job(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public abstract void Run();
        public abstract void Cancel();
    }

    private sealed class Job<T>(Func<T> work, CancellationToken token) : Job(token)
    {
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Run()
        {
            if (Token.IsCancellationRequested) { Cancel(); return; }
            try { Completion.TrySetResult(work()); }
            catch (Exception ex) { Completion.TrySetException(ex); }
        }

        public override void Cancel() => Completion.TrySetCanceled(Token);
    }

    public RenderScheduler(string name = "PDF render")
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name, Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public int PendingCount { get { lock (_lock) return _queue.Count; } }

    /// <summary>How long <see cref="Dispose"/> waits for a running job before returning anyway.</summary>
    internal TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(2);

    internal bool IsShuttingDown => _shutdown.IsCancellationRequested;

    public Task<T> Schedule<T>(Func<T> work, double priority, CancellationToken token = default)
    {
        var job = new Job<T>(work, token);
        if (token.IsCancellationRequested)
        {
            job.Cancel();
            return job.Completion.Task;
        }
        // Complete the task right away on cancellation; the worker drops the job when it reaches it.
        token.Register(static state => ((Job)state!).Cancel(), job);
        lock (_lock) _queue.Enqueue(job, (priority, _sequence++));
        _signal.Release();
        return job.Completion.Task;
    }

    private void Loop()
    {
        var token = _shutdown.Token;
        try
        {
            while (true)
            {
                _signal.Wait(token);
                Job? job;
                lock (_lock)
                {
                    if (!_queue.TryDequeue(out job, out _)) continue;
                }
                job.Run();
            }
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                while (_queue.TryDequeue(out var job, out _)) job.Cancel();
            }
        }
    }

    public void Dispose()
    {
        if (_shutdown.IsCancellationRequested) return;
        _shutdown.Cancel();
        // A job that is still running (e.g. a huge page) keeps the thread alive past the timeout; it then needs
        // the token and semaphore to wind down, so they are only disposed once the thread is gone.
        if (!_thread.Join(ShutdownTimeout)) return;
        _shutdown.Dispose();
        _signal.Dispose();
    }
}
