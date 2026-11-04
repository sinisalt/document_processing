using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DocProc.Api.Endpoints;
using DocProc.Application.Outputs;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Api.Tests;

/// <summary>
/// The API host with a renderer whose renders wait until the test lets them finish, and no preview queue: the test
/// host shares one in-memory SQLite connection, so requests must not reach the database at the same time, and queued
/// previews would all start at once when the held ones end.
/// </summary>
public sealed class HeldRendererApiFactory : ApiFactory
{
    public HeldRenderer Renderer { get; } = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IOutputRenderer>(Renderer);
        services.Configure<OutputPreviewOptions>(o => o.QueuedPreviewsPerTenant = 0);
    }
}

/// <summary>Renders that answer <c>"ok"</c> once <see cref="Release"/> is called, counting those waiting.</summary>
public sealed class HeldRenderer : IOutputRenderer
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _waiting;

    public int Waiting => Volatile.Read(ref _waiting);

    public void Release() => _release.TrySetResult();

    public async Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _waiting);
        try
        {
            await _release.Task.WaitAsync(cancellationToken);
            return new RenderResult(JsonValue.Create("ok"), false, 4, null, [], [], [], TimeSpan.Zero) { OutputText = "\"ok\"" };
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    public Task<ExpressionCheck> CheckAsync(string expression, JsonObject? targetSchema, CancellationToken cancellationToken) =>
        Task.FromResult(new ExpressionCheck(null, [], []));
}

/// <summary>
/// The output mapping preview renders in the renderer pool the API's tenants share: a tenant runs a few previews at a
/// time (and a few more wait), and gets 429 beyond that, while another tenant's previews still run.
/// </summary>
public sealed class OutputPreviewLimitsTests(HeldRendererApiFactory factory) : IClassFixture<HeldRendererApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_preview_carries_the_per_tenant_limit_with_its_defaults()
    {
        var defaults = new OutputPreviewOptions();
        defaults.ConcurrentPreviewsPerTenant.ShouldBe(2);
        defaults.QueuedPreviewsPerTenant.ShouldBe(2);
        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Single(e => e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "PreviewOutputMapping");
        endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>().ShouldNotBeNull().PolicyName.ShouldBe(OutputPreviewOptions.RateLimitPolicy);
    }

    [Fact]
    public async Task A_tenant_beyond_its_previews_gets_429_and_another_tenant_still_renders()
    {
        var limit = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<OutputPreviewOptions>>().Value.ConcurrentPreviewsPerTenant;
        var tenant = factory.CreateTenant("Preview limits");
        factory.Run(db => db.Tenants.Single(t => t.Id == tenant).Settings.Features!.Outputs = true);
        var other = factory.CreateTenant("Preview limits other");
        factory.Run(db => db.Tenants.Single(t => t.Id == other).Settings.Features!.Outputs = true);
        using var admin = factory.TenantAdminOf(tenant);
        using var otherAdmin = factory.TenantAdminOf(other);
        var body = new { expression = "\"ok\"", sampleValues = new { } };

        // One request at a time reaches the renderer (and the database before it), where it is held.
        var held = new List<Task<HttpResponseMessage>>();
        for (var i = 1; i <= limit; i++)
        {
            held.Add(admin.PostAsJsonAsync("/api/v1/output-mappings:preview", body, Ct));
            await WaitForAsync(() => factory.Renderer.Waiting >= i, $"preview {i} reaches the renderer");
        }

        var refused = await admin.PostAsJsonAsync("/api/v1/output-mappings:preview", body, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "the tenant's previews are all running and its queue is full");
        factory.Renderer.Waiting.ShouldBe(limit);

        held.Add(otherAdmin.PostAsJsonAsync("/api/v1/output-mappings:preview", body, Ct));
        await WaitForAsync(() => factory.Renderer.Waiting >= limit + 1, "another tenant's preview has slots of its own");

        factory.Renderer.Release();
        foreach (var response in await Task.WhenAll(held))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            response.Dispose();
        }

        // With its previews done, the tenant renders again.
        (await admin.PostAsJsonAsync("/api/v1/output-mappings:preview", body, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30), what);
            await Task.Delay(TimeSpan.FromMilliseconds(10), Ct);
        }
    }
}
