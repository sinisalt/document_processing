using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using DocProc.Infrastructure.Outputs;
using DocProc.Infrastructure.Outputs.Delivery;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Security;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace DocProc.Integration.Tests;

/// <summary>
/// The <c>mock-erp</c> service of docker-compose.yml and the AppHost (docs/11 §23, docs/09 "Mock ERP"): the WireMock
/// image the compose file names, run over the stub mappings in <c>docker/mock-erp</c> mounted read-only, answers the
/// erp-ap and article-price endpoints and fails with 503 when told to. The later delivery demos (retried 503, then
/// success) rely on exactly this behaviour.
/// </summary>
public sealed partial class MockErpStubTests : IAsyncLifetime
{
    private IContainer? _container;
    private HttpClient? _http;
    private string? _skip;

    private HttpClient Http => _http!;

    public async ValueTask InitializeAsync()
    {
        if (!DockerFacts.IsAvailable)
        {
            return;
        }

        _container = new ContainerBuilder(ComposeImage())
            .WithCommand("--disable-banner")
            .WithBindMount(RepositoryPath("docker", "mock-erp"), "/home/wiremock", AccessMode.ReadOnly)
            .WithPortBinding(8080, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/__admin/health")))
            .Build();
        try
        {
            await _container.StartAsync(TestContext.Current.CancellationToken);
        }
        catch (Exception e) when (e.ToString().Contains("429", StringComparison.Ordinal) || e.ToString().Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase))
        {
            // Docker Hub's pull rate limit is not a defect of the stubs.
            _skip = $"The WireMock image could not be pulled (Docker Hub rate limit): {e.Message}";
            return;
        }

        _http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(8080)}"),
        };
    }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_erp_ap_endpoint_accepts_an_invoice_and_echoes_the_idempotency_key()
    {
        SkipUnlessRunning();
        using var request = Json(HttpMethod.Post, "/api/ap/invoices", """{"header":{"docNo":"RE-1"}}""");
        request.Headers.Add("Idempotency-Key", "doc-1:v3");

        using var response = await Http.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("erpId").GetString().ShouldStartWith("AP-");
        body.GetProperty("idempotencyKey").GetString().ShouldBe("doc-1:v3");
    }

    [Fact]
    public async Task The_article_price_endpoint_accepts_an_import_and_refuses_a_body_that_is_not_json()
    {
        SkipUnlessRunning();

        using var accepted = await Http.SendAsync(Json(HttpMethod.Post, "/api/article-prices", """{"prices":[]}"""), TestContext.Current.CancellationToken);
        using var refused = await Http.PostAsync(new Uri("/api/article-prices", UriKind.Relative), new StringContent("sku;price", Encoding.UTF8, "text/csv"), TestContext.Current.CancellationToken);

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Theory]
    [InlineData("erp-ap", "/api/ap/invoices", HttpStatusCode.Created)]
    [InlineData("article-prices", "/api/article-prices", HttpStatusCode.Accepted)]
    [InlineData("pos-promotions", "/api/promotions", HttpStatusCode.Created)]
    public async Task A_forced_503_fails_the_next_call_only_and_then_the_retry_succeeds(string name, string path, HttpStatusCode success)
    {
        SkipUnlessRunning();
        (await Control(name, "fail-next")).ShouldBe(HttpStatusCode.OK);

        using var first = await Http.SendAsync(Json(HttpMethod.Post, path, "{}"), TestContext.Current.CancellationToken);
        using var retry = await Http.SendAsync(Json(HttpMethod.Post, path, "{}"), TestContext.Current.CancellationToken);

        first.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        first.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(1));
        retry.StatusCode.ShouldBe(success);
    }

    [Fact]
    public async Task The_pos_promotions_endpoint_echoes_the_promotion_and_what_it_replaces()
    {
        SkipUnlessRunning();
        (await Control("pos-promotions", "reset")).ShouldBe(HttpStatusCode.OK);
        using var response = await Http.SendAsync(Json(HttpMethod.Post, "/api/promotions", """{"promotionId":"V-10042-20261105","replaces":"8d2a","items":[]}"""), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("promotionId").GetString().ShouldBe("V-10042-20261105");
        body.GetProperty("replaced").GetString().ShouldBe("8d2a");
    }

    [Fact]
    public async Task The_erp_status_stub_posts_queues_and_rejects_on_command()
    {
        SkipUnlessRunning();
        var ct = TestContext.Current.CancellationToken;
        async Task<string?> StateAsync() => (await Http.GetFromJsonAsync<JsonElement>(new Uri("/api/ap/invoices/AP-123/status", UriKind.Relative), ct)).GetProperty("state").GetString();

        (await Control("erp-ap-status", "reset")).ShouldBe(HttpStatusCode.OK);
        (await StateAsync()).ShouldBe("POSTED");
        (await Control("erp-ap-status", "pending")).ShouldBe(HttpStatusCode.OK);
        (await StateAsync()).ShouldBe("QUEUED");
        (await Control("erp-ap-status", "reject-all")).ShouldBe(HttpStatusCode.OK);
        (await StateAsync()).ShouldBe("REJECTED");
        (await Control("erp-ap-status", "reset")).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Fail_always_holds_until_reset_and_the_header_forces_a_503_on_a_single_request()
    {
        SkipUnlessRunning();
        (await Control("erp-ap", "fail-always")).ShouldBe(HttpStatusCode.OK);
        for (var i = 0; i < 3; i++)
        {
            using var down = await Http.SendAsync(Json(HttpMethod.Post, "/api/ap/invoices", "{}"), TestContext.Current.CancellationToken);
            down.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        }

        (await Control("erp-ap", "reset")).ShouldBe(HttpStatusCode.OK);
        using var up = await Http.SendAsync(Json(HttpMethod.Post, "/api/ap/invoices", "{}"), TestContext.Current.CancellationToken);
        up.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var forced = Json(HttpMethod.Post, "/api/ap/invoices", "{}");
        forced.Headers.Add("X-Mock-Erp-Fail", "503");
        using var failed = await Http.SendAsync(forced, TestContext.Current.CancellationToken);
        failed.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        // The other endpoint was never affected.
        using var prices = await Http.SendAsync(Json(HttpMethod.Post, "/api/article-prices", "{}"), TestContext.Current.CancellationToken);
        prices.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task The_request_journal_shows_what_the_erp_received()
    {
        SkipUnlessRunning();
        using var sent = await Http.SendAsync(Json(HttpMethod.Post, "/api/ap/invoices", """{"header":{"docNo":"RE-JOURNAL"}}"""), TestContext.Current.CancellationToken);

        var journal = await Http.GetFromJsonAsync<JsonElement>(new Uri("/__admin/requests", UriKind.Relative), TestContext.Current.CancellationToken);

        journal.GetProperty("requests").EnumerateArray()
            .Select(r => r.GetProperty("request").GetProperty("body").GetString())
            .ShouldContain(b => b != null && b.Contains("RE-JOURNAL", StringComparison.Ordinal));
    }

    /// <summary>
    /// The http transport against the stubs the P3 demo uses (docs/11 §13.3, §23): the Development allowance lets it
    /// reach mock-erp over plain http, a forced 503 is retryable with the stub's Retry-After and the next call goes
    /// through, the responseMapping captures the ERP's id, an update is a PUT to the price list's URL, the invoice goes
    /// with its original file as multipart, and the oauth stub hands out the token the bearer header then carries.
    /// </summary>
    [Fact]
    public async Task The_http_transport_delivers_to_the_stubs_the_demo_uses()
    {
        SkipUnlessRunning();
        var ct = TestContext.Current.CancellationToken;
        (await Control("erp-ap", "reset")).ShouldBe(HttpStatusCode.OK);
        (await Control("article-prices", "reset")).ShouldBe(HttpStatusCode.OK);
        var baseUrl = Http.BaseAddress!.ToString().TrimEnd('/');
        var policy = new DeliveryEndpointPolicy([$"{Http.BaseAddress.Host}:{Http.BaseAddress.Port}"]);
        using var client = new DeliveryHttpClient(policy);
        using var renderer = new JsonataRenderer(Microsoft.Extensions.Options.Options.Create(new JsonataRendererOptions { PoolSize = 1 }));
        var secrets = new InMemorySecretStore();
        var transport = new HttpDeliveryTransport(client, secrets, new OAuthTokenCache(client, TimeProvider.System), renderer, TimeProvider.System);
        var tenant = Guid.NewGuid();

        DeliveryContext Context(JsonObject config, string payload, string eventType = OutputEvents.Approved, OriginalFile? original = null)
        {
            var delivery = new OutboxDelivery { Id = Guid.CreateVersion7(), TenantId = tenant, DocumentId = Guid.CreateVersion7(), DocumentVersion = 3, IdempotencyKey = $"doc:{Guid.NewGuid():N}", Event = eventType };
            var destination = new OutputDestination { Id = Guid.CreateVersion7(), TenantId = tenant, Name = "mock-erp", Transport = OutputTransports.Http, CreatedBy = "test", SecretRef = $"tenant/{tenant:D}/destination/x/1" };
            var bytes = Encoding.UTF8.GetBytes(payload);
            var values = new DeliveryTemplateValues(delivery.DocumentId, 3, eventType, "price-list", delivery.Id, JsonNode.Parse(payload));
            return new DeliveryContext(delivery, destination, config, bytes, "application/json", values, null, _ => Task.FromResult(original));
        }

        var invoices = new JsonObject { ["urlTemplate"] = baseUrl + "/api/ap/invoices", ["responseMapping"] = "erpId" };
        (await Control("erp-ap", "fail-next")).ShouldBe(HttpStatusCode.OK);
        var first = await transport.SendAsync(Context(invoices, """{"header":{"docNo":"RE-1"}}"""), ct);
        first.Kind.ShouldBe(DeliveryOutcomeKind.Retryable);
        first.StatusCode.ShouldBe(503);
        first.RetryAfter.ShouldBe(TimeSpan.FromSeconds(1));
        var retry = await transport.SendAsync(Context(invoices, """{"header":{"docNo":"RE-1"}}"""), ct);
        retry.Kind.ShouldBe(DeliveryOutcomeKind.Delivered);
        retry.StatusCode.ShouldBe(201);
        retry.ExternalId.ShouldNotBeNull().ShouldStartWith("AP-");

        var prices = new JsonObject { ["urlTemplate"] = baseUrl + "/api/article-prices", ["updateUrlTemplate"] = baseUrl + "/api/article-prices/{{output.header.priceListId}}", ["responseMapping"] = "priceListId" };
        var update = await transport.SendAsync(Context(prices, """{"header":{"priceListId":"PL-2027-01"},"prices":[]}""", OutputEvents.Updated), ct);
        update.StatusCode.ShouldBe(200);
        update.ExternalId.ShouldBe("PL-2027-01", "the stub echoes the price list of the PUT's path");
        (await transport.SendAsync(Context(prices, """{"header":{"priceListId":"PL-2027-01"},"prices":[]}"""), ct)).StatusCode.ShouldBe(202);

        var multipart = new JsonObject
        {
            ["urlTemplate"] = baseUrl + "/api/ap/invoices",
            ["responseMapping"] = "withAttachment ? erpId : undefined",
            ["multipart"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["name"] = "data", ["mapping"] = "self" }, new JsonObject { ["name"] = "file", ["content"] = "original" }) },
        };
        var withFile = await transport.SendAsync(Context(multipart, """{"header":{"docNo":"RE-2"}}""", original: new OriginalFile(Encoding.ASCII.GetBytes("%PDF-1.7"), "re-2.pdf", "application/pdf")), ct);
        withFile.StatusCode.ShouldBe(201);
        withFile.ExternalId.ShouldNotBeNull().ShouldStartWith("AP-");

        var oauth = new JsonObject
        {
            ["urlTemplate"] = baseUrl + "/api/ap/invoices",
            ["auth"] = new JsonObject { ["type"] = "oauth2ClientCredentials", ["tokenUrl"] = baseUrl + "/oauth/token", ["clientId"] = "docproc" },
        };
        var context = Context(oauth, """{"header":{"docNo":"RE-3"}}""");
        await secrets.SetAsync(context.Destination.SecretRef!, "mock-secret", ct);
        (await transport.SendAsync(context, ct)).StatusCode.ShouldBe(201);

        var journal = await Http.GetFromJsonAsync<JsonElement>(new Uri("/__admin/requests", UriKind.Relative), ct);
        var sent = journal.GetProperty("requests").EnumerateArray().Select(r => r.GetProperty("request")).ToList();
        sent.ShouldContain(r => r.GetProperty("method").GetString() == "PUT" && r.GetProperty("url").GetString() == "/api/article-prices/PL-2027-01");
        sent.Any(r => r.GetProperty("headers").TryGetProperty("Authorization", out var auth) && auth.GetString() == "Bearer mock-erp-token").ShouldBeTrue("the bearer token of the oauth stub");
        sent.Any(r => r.GetProperty("headers").TryGetProperty("X-DocProc-Revision", out var revision) && revision.GetString() == "3").ShouldBeTrue("the revision header");
    }

    private void SkipUnlessRunning()
    {
        DockerFacts.SkipUnlessAvailable();
        if (_skip is not null)
        {
            Assert.Skip(_skip);
        }
    }

    private async Task<HttpStatusCode> Control(string endpoint, string action)
    {
        using var response = await Http.PostAsync(new Uri($"/__control/{endpoint}/{action}", UriKind.Relative), null, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static HttpRequestMessage Json(HttpMethod method, string path, string body) =>
        new(method, new Uri(path, UriKind.Relative)) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>The image of the compose service, so the test runs what <c>docker compose up</c> runs.</summary>
    private static string ComposeImage()
    {
        var compose = File.ReadAllText(RepositoryPath("docker-compose.yml"));
        var match = MockErpImage().Match(compose);
        match.Success.ShouldBeTrue("docker-compose.yml declares the mock-erp service with an image");
        return match.Groups["image"].Value;
    }

    [GeneratedRegex(@"^  mock-erp:\s*\n(?:    .*\n)*?    image: (?<image>\S+)", RegexOptions.Multiline)]
    private static partial Regex MockErpImage();

    private static string RepositoryPath(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocProc.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine([dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root (DocProc.slnx)."), .. path]);
    }
}
