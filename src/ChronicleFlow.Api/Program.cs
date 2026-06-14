using ChronicleFlow.Api;
using ChronicleFlow.Application;
using ChronicleFlow.Domain;
using ChronicleFlow.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IJobStore>(_ =>
    new JsonLineJobStore(
        builder.Configuration["Jobs:LogPath"]
        ?? Path.Combine(builder.Environment.ContentRootPath, "data", "jobs.jsonl")));
builder.Services.AddSingleton<JobService>();
builder.Services.AddSingleton<IJobHandler, EchoJobHandler>();
builder.Services.AddSingleton<IJobHandler, DelayJobHandler>();
builder.Services.AddSingleton<IJobHandler, Sha256JobHandler>();
builder.Services.AddSingleton<JobMetrics>();
builder.Services.AddHostedService<JobDispatcher>();
builder.Services.AddHealthChecks();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();

app.UseExceptionHandler(exceptionHandlerApp =>
    exceptionHandlerApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await Results.Problem(
            title: "Request could not be processed.",
            detail: context.Features
                .Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?
                .Error.Message,
            statusCode: StatusCodes.Status400BadRequest)
            .ExecuteAsync(context);
    }));

app.MapGet("/", () => Results.Ok(new
{
    name = "ChronicleFlow",
    description = "A durable background job orchestrator built with modern C#.",
    endpoints = "/jobs, /health"
}));

app.MapPost("/jobs", async (
    SubmitJob request,
    JobService jobs,
    CancellationToken cancellationToken) =>
{
    var job = await jobs.SubmitAsync(request, cancellationToken);
    return Results.Created($"/jobs/{job.Id}", job);
});

app.MapGet("/jobs", async (
    JobStatus? status,
    int? limit,
    JobService jobs,
    CancellationToken cancellationToken) =>
    Results.Ok(await jobs.ListAsync(status, limit ?? 100, cancellationToken)));

app.MapGet("/jobs/{id:guid}", async (
    Guid id,
    JobService jobs,
    CancellationToken cancellationToken) =>
{
    var job = await jobs.GetAsync(id, cancellationToken);
    return job is null ? Results.NotFound() : Results.Ok(job);
});

app.MapPost("/jobs/{id:guid}/cancel", async (
    Guid id,
    JobService jobs,
    CancellationToken cancellationToken) =>
{
    var job = await jobs.CancelAsync(id, cancellationToken);
    return job is null ? Results.NotFound() : Results.Ok(job);
});

app.MapHealthChecks("/health");

app.Run();
