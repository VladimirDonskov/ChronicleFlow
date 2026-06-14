using System.Text.Json;
using ChronicleFlow.Application;
using ChronicleFlow.Domain;

namespace ChronicleFlow.Infrastructure;

public sealed class JsonLineJobStore(string path) : IJobStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Job> _jobs = [];
    private readonly string _path = Path.GetFullPath(path);
    private StreamWriter? _writer;
    private bool _initialized;

    public async Task<Job> AddAsync(Job job, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(job.IdempotencyKey))
            {
                var existing = _jobs.Values.FirstOrDefault(
                    candidate => candidate.IdempotencyKey == job.IdempotencyKey);

                if (existing is not null)
                {
                    return existing;
                }
            }

            _jobs.Add(job.Id, job);
            await AppendAsync(job, cancellationToken);
            return job;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Job?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await ReadAsync(jobs => jobs.GetValueOrDefault(id), cancellationToken);

    public async Task<Job?> GetByIdempotencyKeyAsync(
        string key,
        CancellationToken cancellationToken) =>
        await ReadAsync(
            jobs => jobs.Values.FirstOrDefault(job => job.IdempotencyKey == key),
            cancellationToken);

    public async Task<IReadOnlyList<Job>> ListAsync(
        JobStatus? status,
        int limit,
        CancellationToken cancellationToken) =>
        await ReadAsync(
            jobs => jobs.Values
                .Where(job => status is null || job.Status == status)
                .OrderByDescending(job => job.CreatedAt)
                .Take(limit)
                .ToArray(),
            cancellationToken);

    public async Task<IReadOnlyList<Job>> GetDueAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken) =>
        await ReadAsync(
            jobs => jobs.Values
                .Where(job => job.Status == JobStatus.Queued && job.RunAt <= now)
                .OrderByDescending(job => job.Priority)
                .ThenBy(job => job.RunAt)
                .Take(limit)
                .ToArray(),
            cancellationToken);

    public async Task<Job?> UpdateAsync(
        Guid id,
        Func<Job, Job?> update,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            if (!_jobs.TryGetValue(id, out var current))
            {
                return null;
            }

            var updated = update(current);
            if (updated is null)
            {
                return null;
            }

            _jobs[id] = updated;
            await AppendAsync(updated, cancellationToken);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> ReadAsync<T>(
        Func<IReadOnlyDictionary<Guid, Job>, T> read,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            return read(_jobs);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        if (File.Exists(_path))
        {
            await foreach (var line in File.ReadLinesAsync(_path, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var job = JsonSerializer.Deserialize<Job>(line, JsonOptions)
                    ?? throw new InvalidDataException("The job log contains a null record.");
                _jobs[job.Id] = RecoverInterruptedJob(job);
            }
        }

        _writer = new StreamWriter(
            new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read),
            leaveOpen: false)
        {
            AutoFlush = true
        };
        _initialized = true;
    }

    private async Task AppendAsync(Job job, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(job, JsonOptions);
        await _writer!.WriteLineAsync(line.AsMemory(), cancellationToken);
    }

    private static Job RecoverInterruptedJob(Job job) =>
        job.Status == JobStatus.Running
            ? job with
            {
                Status = JobStatus.Queued,
                RunAt = DateTimeOffset.UtcNow,
                Error = "Worker stopped while the job was running."
            }
            : job;

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_writer is not null)
            {
                await _writer.DisposeAsync();
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
