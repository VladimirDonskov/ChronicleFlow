using System.Text.Json;
using ChronicleFlow.Application;
using ChronicleFlow.Domain;
using ChronicleFlow.Infrastructure;

namespace ChronicleFlow.Tests;

public sealed class JobTests
{
    [Fact]
    public void Fail_requeues_until_attempt_budget_is_exhausted()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var job = NewJob(now, maxAttempts: 2).Start(now);

        var retried = job.Fail(now, "transient", TimeSpan.FromSeconds(1));
        var failed = retried.Start(now.AddSeconds(1))
            .Fail(now.AddSeconds(1), "permanent", TimeSpan.FromSeconds(2));

        Assert.Equal(JobStatus.Queued, retried.Status);
        Assert.Equal(now.AddSeconds(1), retried.RunAt);
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Equal(2, failed.Attempt);
    }

    [Fact]
    public async Task Store_restores_latest_state_from_append_only_log()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chronicleflow-{Guid.NewGuid():N}.jsonl");
        var now = DateTimeOffset.UtcNow;
        var job = NewJob(now);

        await using (var store = new JsonLineJobStore(path))
        {
            await store.AddAsync(job, CancellationToken.None);
            await store.UpdateAsync(
                job.Id,
                current => current.Start(now),
                CancellationToken.None);
            await store.UpdateAsync(
                job.Id,
                current => current.Succeed(now, "done"),
                CancellationToken.None);
        }

        await using (var restoredStore = new JsonLineJobStore(path))
        {
            var restored = await restoredStore.GetAsync(
                job.Id,
                CancellationToken.None);

            Assert.NotNull(restored);
            Assert.Equal(JobStatus.Succeeded, restored.Status);
            Assert.Equal("done", restored.Result);
        }

        File.Delete(path);
    }

    [Fact]
    public async Task Duplicate_idempotency_key_returns_original_job()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chronicleflow-{Guid.NewGuid():N}.jsonl");
        await using var store = new JsonLineJobStore(path);
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var service = new JobService(store, clock);
        var payload = JsonDocument.Parse("""{"message":"hello"}""").RootElement;

        var first = await service.SubmitAsync(
            new SubmitJob("echo", payload, IdempotencyKey: "same-request"),
            CancellationToken.None);
        var duplicate = await service.SubmitAsync(
            new SubmitJob("echo", payload, IdempotencyKey: "same-request"),
            CancellationToken.None);

        Assert.Equal(first.Id, duplicate.Id);
    }

    private static Job NewJob(DateTimeOffset now, int maxAttempts = 3) =>
        Job.Create(
            "echo",
            JsonDocument.Parse("""{"message":"hello"}""").RootElement,
            now,
            now,
            priority: 0,
            maxAttempts,
            idempotencyKey: null);

    private sealed record FakeClock(DateTimeOffset UtcNow) : IClock;
}
