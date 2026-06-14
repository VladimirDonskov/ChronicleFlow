using ChronicleFlow.Domain;

namespace ChronicleFlow.Application;

public interface IJobStore
{
    Task<Job> AddAsync(Job job, CancellationToken cancellationToken);
    Task<Job?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<Job?> GetByIdempotencyKeyAsync(string key, CancellationToken cancellationToken);
    Task<IReadOnlyList<Job>> ListAsync(JobStatus? status, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<Job>> GetDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);
    Task<Job?> UpdateAsync(
        Guid id,
        Func<Job, Job?> update,
        CancellationToken cancellationToken);
}

public interface IJobHandler
{
    string Type { get; }
    Task<string?> ExecuteAsync(Job job, CancellationToken cancellationToken);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
