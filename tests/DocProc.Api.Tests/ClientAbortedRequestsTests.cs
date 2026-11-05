using System.Collections.Concurrent;
using DocProc.Api.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DocProc.Api.Tests;

/// <summary>
/// A request the client gave up on is a closed request, not an unhandled 500 (found by the P1 demo in the compose stack:
/// SqlClient reports a cancelled query as a SqlException, which the exception handler logged as an error).
/// </summary>
public sealed class ClientAbortedRequestsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_exception_of_an_aborted_request_becomes_a_cancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var context = new DefaultHttpContext { RequestAborted = aborted.Token };
        var cause = new InvalidOperationException("A severe error occurred on the current command. Operation cancelled by user.");

        // Thrown with the request's cancelled token, the task ends Canceled: awaited, it is a cancellation, as the
        // exception handler sees it (the SqlException is not passed on).
        var task = ClientAbortedRequests.InvokeAsync(context, _ => throw cause);
        var thrown = await Should.ThrowAsync<OperationCanceledException>(() => task);
        task.IsCanceled.ShouldBeTrue();
        thrown.CancellationToken.ShouldBe(aborted.Token);
    }

    [Fact]
    public async Task Any_other_failure_stays_what_it_is()
    {
        var context = new DefaultHttpContext();
        var cause = new InvalidOperationException("a genuine fault");
        (await Should.ThrowAsync<InvalidOperationException>(() => ClientAbortedRequests.InvokeAsync(context, _ => throw cause))).ShouldBeSameAs(cause);
        await ClientAbortedRequests.InvokeAsync(context, _ => Task.CompletedTask);
    }

    [Fact]
    public async Task Behind_the_exception_handler_an_aborted_request_logs_no_error_and_a_fault_still_does()
    {
        var logs = new ConcurrentQueue<(LogLevel Level, string Category)>();
        var statuses = new ConcurrentQueue<int>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(new QueueLoggerProvider(logs));
        builder.Services.AddProblemDetails();
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            await next(context);
            statuses.Enqueue(context.Response.StatusCode);
        });
        app.UseExceptionHandler(new ExceptionHandlerOptions { StatusCodeSelector = _ => StatusCodes.Status500InternalServerError });
        app.UseClientAbortedRequests();
        app.Map("/aborted", (HttpContext context) =>
        {
            context.Abort();
            throw new InvalidOperationException("A severe error occurred on the current command. Operation cancelled by user.");
        });
        app.Map("/fault", () => { throw new InvalidOperationException("a genuine fault"); });
        await app.StartAsync(Ct);
        using var client = app.GetTestClient();

        await Should.ThrowAsync<Exception>(() => client.GetAsync("/aborted", Ct));
        logs.Where(l => l.Level >= LogLevel.Error).ShouldBeEmpty();
        statuses.ShouldBe([StatusCodes.Status499ClientClosedRequest]);

        (await client.GetAsync("/fault", Ct)).StatusCode.ShouldBe(System.Net.HttpStatusCode.InternalServerError);
        logs.ShouldContain(l => l.Level == LogLevel.Error);
        await app.StopAsync(Ct);
    }

    private sealed class QueueLoggerProvider(ConcurrentQueue<(LogLevel Level, string Category)> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new QueueLogger(lines, categoryName);

        public void Dispose()
        {
        }

        private sealed class QueueLogger(ConcurrentQueue<(LogLevel Level, string Category)> lines, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue((logLevel, category));
        }
    }
}
