using System.Security.Cryptography;
using System.Text;
using ChronicleFlow.Application;
using ChronicleFlow.Domain;

namespace ChronicleFlow.Infrastructure;

public sealed class EchoJobHandler : IJobHandler
{
    public string Type => "echo";

    public Task<string?> ExecuteAsync(Job job, CancellationToken cancellationToken)
    {
        var message = job.Payload.TryGetProperty("message", out var value)
            ? value.GetString()
            : job.Payload.GetRawText();

        return Task.FromResult<string?>(message);
    }
}

public sealed class DelayJobHandler : IJobHandler
{
    public string Type => "delay";

    public async Task<string?> ExecuteAsync(Job job, CancellationToken cancellationToken)
    {
        var milliseconds = job.Payload.GetProperty("milliseconds").GetInt32();
        if (milliseconds is < 0 or > 30_000)
        {
            throw new ArgumentException(
                "Delay must be between 0 and 30000 milliseconds.",
                nameof(job));
        }

        await Task.Delay(milliseconds, cancellationToken);
        return $"Waited {milliseconds} ms.";
    }
}

public sealed class Sha256JobHandler : IJobHandler
{
    public string Type => "sha256";

    public Task<string?> ExecuteAsync(Job job, CancellationToken cancellationToken)
    {
        var text = job.Payload.GetProperty("text").GetString()
            ?? throw new ArgumentException("The text property is required.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return Task.FromResult<string?>(hash);
    }
}
