using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using ChronicleFlow.Application;

namespace ChronicleFlow.Api;

public sealed class JobDispatcher(
    JobService jobs,
    IEnumerable<IJobHandler> handlers,
    JobMetrics metrics,
    ILogger<JobDispatcher> logger,
    IConfiguration configuration) : BackgroundService
{
    private readonly Dictionary<string, IJobHandler> _handlers =
        handlers.ToDictionary(handler => handler.Type, StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly Channel<Guid> _queue = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(configuration.GetValue("Jobs:QueueCapacity", 1_000))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false
        });
    private readonly int _workerCount = configuration.GetValue("Jobs:WorkerCount", 4);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumers = Enumerable.Range(0, _workerCount)
            .Select(_ => ConsumeAsync(stoppingToken))
            .ToArray();

        try
        {
            await ProduceAsync(stoppingToken);
        }
        finally
        {
            _queue.Writer.TryComplete();
            await Task.WhenAll(consumers);
        }
    }

    private async Task ProduceAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));

        do
        {
            var due = await jobs.GetDueAsync(100, cancellationToken);
            foreach (var job in due)
            {
                await _queue.Writer.WriteAsync(job.Id, cancellationToken);
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            var job = await jobs.TryStartAsync(id, stoppingToken);
            if (job is null)
            {
                continue;
            }

            using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _running[id] = jobCancellation;

            try
            {
                if (!_handlers.TryGetValue(job.Type, out var handler))
                {
                    throw new InvalidOperationException($"No handler is registered for '{job.Type}'.");
                }

                metrics.Started.Add(1, new KeyValuePair<string, object?>("job.type", job.Type));
                var result = await handler.ExecuteAsync(job, jobCancellation.Token);
                await jobs.SucceedAsync(id, result, stoppingToken);
                metrics.Succeeded.Add(1, new KeyValuePair<string, object?>("job.type", job.Type));
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Job {JobId} was cancelled.", id);
            }
            catch (Exception exception)
            {
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning(
                        exception,
                        "Job {JobId} failed on attempt {Attempt}.",
                        id,
                        job.Attempt);
                }
                await jobs.FailAsync(id, exception, stoppingToken);
                metrics.Failed.Add(1, new KeyValuePair<string, object?>("job.type", job.Type));
            }
            finally
            {
                _running.TryRemove(id, out _);
            }
        }
    }
}

public sealed class JobMetrics : IDisposable
{
    private readonly Meter _meter = new("ChronicleFlow.Jobs");

    public Counter<long> Started { get; }
    public Counter<long> Succeeded { get; }
    public Counter<long> Failed { get; }

    public JobMetrics()
    {
        Started = _meter.CreateCounter<long>("chronicleflow.jobs.started");
        Succeeded = _meter.CreateCounter<long>("chronicleflow.jobs.succeeded");
        Failed = _meter.CreateCounter<long>("chronicleflow.jobs.failed");
    }

    public void Dispose() => _meter.Dispose();
}
