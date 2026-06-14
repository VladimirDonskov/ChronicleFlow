using System.Text.Json;
using ChronicleFlow.Domain;

namespace ChronicleFlow.Application;

public sealed record SubmitJob(
    string Type,
    JsonElement Payload,
    DateTimeOffset? RunAt = null,
    int Priority = 0,
    int MaxAttempts = 3,
    string? IdempotencyKey = null);

public sealed class JobService(IJobStore store, IClock clock)
{
    public async Task<Job> SubmitAsync(SubmitJob request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Type);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.MaxAttempts, 20);

        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var existing = await store.GetByIdempotencyKeyAsync(
                request.IdempotencyKey,
                cancellationToken);

            if (existing is not null)
            {
                return existing;
            }
        }

        var now = clock.UtcNow;
        var job = Job.Create(
            request.Type.Trim(),
            request.Payload,
            now,
            request.RunAt ?? now,
            request.Priority,
            request.MaxAttempts,
            request.IdempotencyKey?.Trim());

        return await store.AddAsync(job, cancellationToken);
    }

    public Task<Job?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        store.GetAsync(id, cancellationToken);

    public Task<IReadOnlyList<Job>> ListAsync(
        JobStatus? status,
        int limit,
        CancellationToken cancellationToken) =>
        store.ListAsync(status, Math.Clamp(limit, 1, 500), cancellationToken);

    public Task<IReadOnlyList<Job>> GetDueAsync(int limit, CancellationToken cancellationToken) =>
        store.GetDueAsync(clock.UtcNow, limit, cancellationToken);

    public Task<Job?> TryStartAsync(Guid id, CancellationToken cancellationToken) =>
        store.UpdateAsync(
            id,
            job => job.Status == JobStatus.Queued && job.RunAt <= clock.UtcNow
                ? job.Start(clock.UtcNow)
                : null,
            cancellationToken);

    public Task<Job?> SucceedAsync(
        Guid id,
        string? result,
        CancellationToken cancellationToken) =>
        store.UpdateAsync(
            id,
            job => job.Status == JobStatus.Running
                ? job.Succeed(clock.UtcNow, result)
                : null,
            cancellationToken);

    public Task<Job?> FailAsync(
        Guid id,
        Exception exception,
        CancellationToken cancellationToken) =>
        store.UpdateAsync(
            id,
            job => job.Status == JobStatus.Running
                ? job.Fail(clock.UtcNow, exception.Message, RetryDelay(job.Attempt))
                : null,
            cancellationToken);

    public Task<Job?> CancelAsync(Guid id, CancellationToken cancellationToken) =>
        store.UpdateAsync(id, job => job.Cancel(clock.UtcNow), cancellationToken);

    private static TimeSpan RetryDelay(int attempt)
    {
        var seconds = Math.Min(60, Math.Pow(2, Math.Max(0, attempt - 1)));
        return TimeSpan.FromSeconds(seconds);
    }
}
