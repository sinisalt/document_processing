using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.AppHost.Tests;

/// <summary>
/// The AppHost's application model for checks that must never start anything.
/// <para>
/// <see cref="DistributedApplicationTestingBuilder"/> runs the AppHost's own entry point on a background thread and
/// holds it inside <c>builder.Build()</c> until the test calls <c>BuildAsync</c>. The entry point then goes on with
/// <c>.Run()</c>, which starts the orchestrator (DCP) and, with it, every container of the model. Only disposing the
/// app cancels that start. A test that did not dispose its app (the compose parity checks), or a start that got far
/// enough before the dispose, left the persistent containers of the AppHost (Azurite, the Service Bus emulator and
/// its SQL Server, Keycloak, Lowkey Vault, GreenMail) running after every <c>dotnet test</c>, because a persistent
/// container outlives the process that created it.
/// </para>
/// <para>
/// Every model-only test builds through here: a <see cref="BeforeStartEvent"/> subscription made on the builder, before
/// any integration subscribes, stops the entry point's run before the host and the orchestrator start, and the app is
/// always disposed. The AppHost itself is unchanged, so <c>dotnet run</c> on a developer machine behaves as before.
/// </para>
/// </summary>
internal sealed class AppHostModel : IAsyncDisposable
{
    private readonly TaskCompletionSource _runStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private AppHostModel()
    {
    }

    public DistributedApplication App { get; private set; } = null!;

    public DistributedApplicationModel Model => App.Services.GetRequiredService<DistributedApplicationModel>();

    public DistributedApplicationExecutionContext ExecutionContext => App.Services.GetRequiredService<DistributedApplicationExecutionContext>();

    /// <summary>Completes when the entry point's <c>Run()</c> reached the start and was stopped before anything started.</summary>
    public Task RunStopped => _runStopped.Task;

    public static async Task<AppHostModel> BuildAsync(CancellationToken cancellationToken)
    {
        var model = new AppHostModel();
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.DocProc_AppHost>(cancellationToken);
        builder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
        {
            model._runStopped.TrySetResult();
            throw new ModelOnlyException();
        });
        model.App = await builder.BuildAsync(cancellationToken);
        return model;
    }

    /// <summary>The model alone: the app is built, stopped before it starts, and disposed.</summary>
    public static async Task<DistributedApplicationModel> ModelAsync(CancellationToken cancellationToken)
    {
        await using var app = await BuildAsync(cancellationToken);
        return app.Model;
    }

    public async ValueTask DisposeAsync()
    {
        if (App is not null)
        {
            await App.DisposeAsync();
        }
    }

    /// <summary>Thrown into the entry point's run; the testing factory records it as the entry point's exit.</summary>
    private sealed class ModelOnlyException() : InvalidOperationException("A model-only AppHost test does not start the application.");
}
