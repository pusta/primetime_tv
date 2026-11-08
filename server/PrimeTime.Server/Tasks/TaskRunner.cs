using PrimeTime.Server.Data;
using PrimeTime.Server.Jellyfin;

namespace PrimeTime.Server.Tasks;

public enum TaskStartResult { Started, Busy, NotReady, UnknownTask }

public sealed record RunningTaskInfo(
    string Key,
    long RunId,
    string Trigger,
    DateTimeOffset StartedAt,
    string? Message,
    int? Processed,
    int? Total);

/// <summary>
/// Runs background tasks one at a time. Changes that a running task must not race
/// (switching servers, changing the library selection) go through <see cref="RunExclusiveAsync"/>.
/// </summary>
public sealed class TaskRunner
{
    public const int MinIntervalMinutes = 15;
    public const int MaxIntervalMinutes = 30 * 24 * 60;

    private readonly IReadOnlyDictionary<string, IScheduledTask> _tasks;
    private readonly ScheduledTaskStore _store;
    private readonly ILogger<TaskRunner> _logger;
    private readonly CancellationToken _appStopping;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateLock = new();
    private RunningTaskInfo? _current;
    private CancellationTokenSource? _currentCts;
    private int _exclusiveWaiters;

    public TaskRunner(
        IEnumerable<IScheduledTask> tasks,
        ScheduledTaskStore store,
        IHostApplicationLifetime lifetime,
        ILogger<TaskRunner> logger)
    {
        _tasks = tasks.ToDictionary(t => t.Key);
        _store = store;
        _logger = logger;
        _appStopping = lifetime.ApplicationStopping;
    }

    public IEnumerable<IScheduledTask> Tasks => _tasks.Values;

    public IScheduledTask? Find(string key) => _tasks.GetValueOrDefault(key);

    public RunningTaskInfo? Current
    {
        get { lock (_stateLock) { return _current; } }
    }

    /// <summary>Registers tasks in the database and cleans up after an unclean shutdown.</summary>
    public void Initialize()
    {
        foreach (var task in _tasks.Values)
        {
            _store.EnsureTask(task.Key, task.DefaultIntervalMinutes);
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var key in _store.MarkAbandonedRunsInterrupted(now))
        {
            if (_tasks.ContainsKey(key))
            {
                _store.SetNextRun(key, now);
                _logger.LogWarning("Task {Task} was interrupted by a restart and will run again", key);
            }
        }
    }

    public TaskStartResult TryStart(string key, string trigger)
    {
        if (!_tasks.TryGetValue(key, out var task))
        {
            return TaskStartResult.UnknownTask;
        }

        if (task.NotReadyReason() is not null)
        {
            return TaskStartResult.NotReady;
        }

        if (Volatile.Read(ref _exclusiveWaiters) > 0 || !_gate.Wait(0))
        {
            return TaskStartResult.Busy;
        }

        try
        {
            var startedAt = DateTimeOffset.UtcNow;
            var runId = _store.StartRun(key, trigger, startedAt);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_appStopping);
            lock (_stateLock)
            {
                _current = new RunningTaskInfo(key, runId, trigger, startedAt, "Starting…", null, null);
                _currentCts = cts;
            }

            _ = Task.Run(() => ExecuteAsync(task, runId, cts));
            return TaskStartResult.Started;
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    /// <summary>Asks the running task to stop. Returns false if that task isn't running.</summary>
    public bool RequestCancel(string key)
    {
        lock (_stateLock)
        {
            if (_current?.Key != key)
            {
                return false;
            }

            _currentCts?.Cancel();
            return true;
        }
    }

    /// <summary>Cancels any running task, then runs <paramref name="action"/> with no task running.</summary>
    public async Task RunExclusiveAsync(Action action)
    {
        Interlocked.Increment(ref _exclusiveWaiters);
        try
        {
            lock (_stateLock)
            {
                _currentCts?.Cancel();
            }

            await _gate.WaitAsync();
            try
            {
                action();
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _exclusiveWaiters);
        }
    }

    /// <summary>
    /// Saves a new schedule. The next run counts from the last finished run, but a shorter
    /// interval never makes a scan fire the moment you save: it waits at least one interval.
    /// </summary>
    public void UpdateSchedule(string key, bool enabled, int intervalMinutes)
    {
        var now = DateTimeOffset.UtcNow;
        var interval = TimeSpan.FromMinutes(intervalMinutes);
        var next = (_store.LastFinishedAt(key) ?? now) + interval;
        if (next <= now)
        {
            next = now + interval;
        }

        _store.UpdateSchedule(key, enabled, intervalMinutes, next);
    }

    private async Task ExecuteAsync(IScheduledTask task, long runId, CancellationTokenSource cts)
    {
        string status;
        string? summary = null, error = null;
        try
        {
            var context = new TaskRunContext(runId, UpdateProgress);
            var outcome = await task.RunAsync(context, cts.Token);
            (status, summary, error) = (outcome.Status, outcome.Summary, outcome.Error);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            status = _appStopping.IsCancellationRequested ? TaskRunStatus.Interrupted : TaskRunStatus.Cancelled;
        }
        catch (JellyfinException ex)
        {
            status = TaskRunStatus.Failed;
            error = ex.Message;
            _logger.LogWarning("Task {Task} failed: {Message}", task.Key, ex.Message);
        }
        catch (Exception ex)
        {
            status = TaskRunStatus.Failed;
            error = "Something unexpected went wrong. Check the server logs.";
            _logger.LogError(ex, "Task {Task} failed", task.Key);
        }

        try
        {
            var finishedAt = DateTimeOffset.UtcNow;
            _store.FinishRun(task.Key, runId, status, summary, error, finishedAt);

            // An interrupted run keeps its old due time, so it runs again after the restart.
            if (status != TaskRunStatus.Interrupted && _store.Get(task.Key) is { } schedule)
            {
                _store.SetNextRun(task.Key, finishedAt + TimeSpan.FromMinutes(schedule.IntervalMinutes));
            }

            _logger.LogInformation("Task {Task} finished: {Status}. {Summary}", task.Key, status, summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Couldn't record the result of task {Task}", task.Key);
        }
        finally
        {
            lock (_stateLock)
            {
                _current = null;
                _currentCts = null;
            }

            cts.Dispose();
            _gate.Release();
        }
    }

    private void UpdateProgress(string message, int? processed, int? total)
    {
        lock (_stateLock)
        {
            if (_current is not null)
            {
                _current = _current with { Message = message, Processed = processed, Total = total };
            }
        }
    }
}
