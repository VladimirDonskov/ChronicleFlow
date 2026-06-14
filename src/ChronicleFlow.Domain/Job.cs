using System.Text.Json;

namespace ChronicleFlow.Domain;

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public sealed record Job
{
    public required Guid Id { get; init; }
    public required string Type { get; init; }
    public required JsonElement Payload { get; init; }
    public required JobStatus Status { get; init; }
    public required int Priority { get; init; }
    public required int Attempt { get; init; }
    public required int MaxAttempts { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset RunAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? Result { get; init; }
    public string? Error { get; init; }

    public static Job Create(
        string type,
        JsonElement payload,
        DateTimeOffset now,
        DateTimeOffset runAt,
        int priority,
        int maxAttempts,
        string? idempotencyKey) =>
        new()
        {
            Id = Guid.NewGuid(),
            Type = type,
            Payload = payload.Clone(),
            Status = JobStatus.Queued,
            Priority = priority,
            Attempt = 0,
            MaxAttempts = maxAttempts,
            CreatedAt = now,
            RunAt = runAt,
            IdempotencyKey = idempotencyKey
        };

    public Job Start(DateTimeOffset now)
    {
        EnsureStatus(JobStatus.Queued);
        return this with
        {
            Status = JobStatus.Running,
            Attempt = Attempt + 1,
            StartedAt = now,
            Error = null
        };
    }

    public Job Succeed(DateTimeOffset now, string? result)
    {
        EnsureStatus(JobStatus.Running);
        return this with { Status = JobStatus.Succeeded, CompletedAt = now, Result = result };
    }

    public Job Fail(DateTimeOffset now, string error, TimeSpan retryDelay)
    {
        EnsureStatus(JobStatus.Running);
        return Attempt < MaxAttempts
            ? this with { Status = JobStatus.Queued, RunAt = now + retryDelay, Error = error }
            : this with { Status = JobStatus.Failed, CompletedAt = now, Error = error };
    }

    public Job Cancel(DateTimeOffset now)
    {
        if (Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled)
        {
            return this;
        }

        return this with { Status = JobStatus.Cancelled, CompletedAt = now };
    }

    private void EnsureStatus(JobStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException(
                $"Job {Id} must be {expected} but is {Status}.");
        }
    }
}
