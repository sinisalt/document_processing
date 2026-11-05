using System.Text;
using System.Text.Json.Nodes;
using DocProc.Infrastructure.Outputs;
using DocProc.Infrastructure.Outputs.Delivery;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DocProc.Pipeline.Tests.Delivery;

/// <summary>One renderer pool for the transport tests (the responseMapping runs in it).</summary>
public sealed class DeliveryRendererFixture : IDisposable
{
    public JsonataRenderer Renderer { get; } = new(Options.Create(new JsonataRendererOptions { PoolSize = 1 }));

    public void Dispose() => Renderer.Dispose();
}

/// <summary>
/// The transports of docs/11 §13.3 against a fake receiver on loopback: what an http delivery sends (method, URL,
/// idempotency and revision headers, every auth variant, multipart with the original file), how its answers are
/// classified (docs/11 §13.6), the responseMapping, the endpoint guard and its Development allowance, and the file
/// drop's write-then-rename.
/// </summary>
public sealed class DeliveryTransportTests(DeliveryRendererFixture fixture) : IClassFixture<DeliveryRendererFixture>
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("""{"header":{"docNo":"RE-1","priceListId":"PL 2027/01"},"rows":[{"net":100}]}""");

    private static readonly System.Text.Json.JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_http_delivery_posts_the_payload_with_the_idempotency_key_and_the_revision()
    {
        using var receiver = new FakeReceiver();
        var (transport, _) = Http(receiver);
        var context = Context(receiver, new JsonObject { ["urlTemplate"] = receiver.BaseUrl + "/api/ap/invoices?doc={{meta.documentId}}", ["headers"] = new JsonObject { ["X-Tenant-Code"] = "1000" } });

        var outcome = await transport.SendAsync(context, Ct);

        outcome.Kind.ShouldBe(DeliveryOutcomeKind.Delivered);
        outcome.StatusCode.ShouldBe(201);
        var request = receiver.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe("POST");
        request.PathAndQuery.ShouldBe($"/api/ap/invoices?doc={context.Delivery.DocumentId:D}");
        request.Header("Idempotency-Key").ShouldBe(context.Delivery.IdempotencyKey);
        request.Header("X-DocProc-Document-Id").ShouldBe(context.Delivery.DocumentId.ToString("D"));
        request.Header("X-DocProc-Revision").ShouldBe("7");
        request.Header("X-DocProc-Event").ShouldBe(OutputEvents.Approved);
        request.Header("X-Tenant-Code").ShouldBe("1000");
        request.Header("Content-Type")!.ShouldStartWith("application/json");
        request.Body.ShouldBe(Payload);
        request.Header("Authorization").ShouldBeNull();
    }

    [Theory]
    [InlineData(503, DeliveryOutcomeKind.Retryable)]
    [InlineData(502, DeliveryOutcomeKind.Retryable)]
    [InlineData(429, DeliveryOutcomeKind.Retryable)]
    [InlineData(425, DeliveryOutcomeKind.Retryable)]
    [InlineData(408, DeliveryOutcomeKind.Retryable)]
    [InlineData(400, DeliveryOutcomeKind.Permanent)]
    [InlineData(404, DeliveryOutcomeKind.Permanent)]
    [InlineData(409, DeliveryOutcomeKind.Permanent)]
    [InlineData(204, DeliveryOutcomeKind.Delivered)]
    public async Task Answers_are_classified_as_docs_11_13_6_says(int status, DeliveryOutcomeKind expected)
    {
        using var receiver = new FakeReceiver { Default = new ReceiverAnswer(status, """{"error":"x"}""") };
        var (transport, _) = Http(receiver);

        var outcome = await transport.SendAsync(Context(receiver), Ct);

        outcome.Kind.ShouldBe(expected);
        outcome.StatusCode.ShouldBe(status);
        if (expected != DeliveryOutcomeKind.Delivered)
        {
            outcome.ResponseExcerpt.ShouldBe("""{"error":"x"}""");
        }
    }

    [Fact]
    public async Task Retry_after_is_read_and_success_codes_narrow_the_2xx()
    {
        using var receiver = new FakeReceiver();
        receiver.Script(new ReceiverAnswer(503, "{}", new Dictionary<string, string> { ["Retry-After"] = "7" }), new ReceiverAnswer(202));
        var (transport, _) = Http(receiver);

        var outcome = await transport.SendAsync(Context(receiver), Ct);
        outcome.Kind.ShouldBe(DeliveryOutcomeKind.Retryable);
        outcome.RetryAfter.ShouldBe(TimeSpan.FromSeconds(7));

        var strict = await transport.SendAsync(Context(receiver, new JsonObject { ["urlTemplate"] = receiver.BaseUrl + "/x", ["successCodes"] = new JsonArray(201) }), Ct);
        strict.Kind.ShouldBe(DeliveryOutcomeKind.Permanent, "202 is not one of the destination's success codes");
    }

    [Fact]
    public async Task The_endpoint_guard_refuses_a_private_http_receiver_unless_it_is_a_development_host()
    {
        using var receiver = new FakeReceiver();
        var strict = Transport(DeliveryEndpointPolicy.Strict, new InMemorySecretStore());

        var refused = await strict.SendAsync(Context(receiver), Ct);

        refused.Kind.ShouldBe(DeliveryOutcomeKind.Permanent);
        refused.ErrorClass.ShouldBe("permanent:blocked");
        receiver.Requests.ShouldBeEmpty("nothing is sent to a refused address");

        var https = await strict.SendAsync(Context(receiver, new JsonObject { ["urlTemplate"] = $"https://127.0.0.1:{receiver.Port}/x" }), Ct);
        https.ErrorClass.ShouldBe("permanent:blocked", "loopback is refused over https too");

        var (allowed, _) = Http(receiver);
        (await allowed.SendAsync(Context(receiver), Ct)).Kind.ShouldBe(DeliveryOutcomeKind.Delivered);
        new DeliveryEndpointPolicy(["mock-erp:8080"]).IsExempt("mock-erp", 8081).ShouldBeFalse("the port is part of the entry");
        new DeliveryEndpointPolicy(["MOCK-ERP"]).IsExempt("mock-erp", 1).ShouldBeTrue("a host without a port is exempt on every port");
    }

    [Theory]
    [InlineData(HttpAuthTypes.ApiKeyHeader)]
    [InlineData(HttpAuthTypes.Basic)]
    [InlineData(HttpAuthTypes.Bearer)]
    [InlineData(HttpAuthTypes.HmacSha256)]
    public async Task Every_auth_variant_sends_the_destinations_secret_its_way(string type)
    {
        using var receiver = new FakeReceiver();
        var (transport, secrets) = Http(receiver);
        var auth = new JsonObject { ["type"] = type };
        if (type == HttpAuthTypes.ApiKeyHeader)
        {
            auth["headerName"] = "X-Erp-Key";
        }

        if (type == HttpAuthTypes.Basic)
        {
            auth["username"] = "docproc";
        }

        var context = Context(receiver, new JsonObject { ["urlTemplate"] = receiver.BaseUrl + "/x", ["auth"] = auth });
        await secrets.SetAsync(context.Destination.SecretRef!, "s3cret-value", Ct);

        (await transport.SendAsync(context, Ct)).Kind.ShouldBe(DeliveryOutcomeKind.Delivered);
        var request = receiver.Requests.ShouldHaveSingleItem();
        switch (type)
        {
            case HttpAuthTypes.ApiKeyHeader:
                request.Header("X-Erp-Key").ShouldBe("s3cret-value");
                break;
            case HttpAuthTypes.Basic:
                request.Header("Authorization").ShouldBe("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("docproc:s3cret-value")));
                break;
            case HttpAuthTypes.Bearer:
                request.Header("Authorization").ShouldBe("Bearer s3cret-value");
                break;
            case HttpAuthTypes.HmacSha256:
                var timestamp = request.Header("X-DocProc-Timestamp").ShouldNotBeNull();
                request.Header("X-DocProc-Signature").ShouldBe("sha256=" + HttpDeliveryTransport.Signature("s3cret-value", timestamp, request.Body));
                request.Header("Authorization").ShouldBeNull();
                break;
        }
    }

    [Fact]
    public async Task A_missing_secret_is_permanent_and_nothing_is_sent()
    {
        using var receiver = new FakeReceiver();
        var (transport, _) = Http(receiver);

        var outcome = await transport.SendAsync(Context(receiver, new JsonObject { ["urlTemplate"] = receiver.BaseUrl + "/x", ["auth"] = new JsonObject { ["type"] = "bearer" } }), Ct);

        outcome.ErrorClass.ShouldBe("permanent:secret");
        receiver.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Oauth2_client_credentials_asks_for_a_token_once_and_again_after_a_401()
    {
        using var receiver = new FakeReceiver
        {
            Respond = request => request.PathAndQuery == "/oauth/token"
                ? new ReceiverAnswer(200, """{"access_token":"tok-1","expires_in":3600}""")
                : request.Header("Authorization") == "Bearer tok-1" ? new ReceiverAnswer(201) : new ReceiverAnswer(401),
        };
        var (transport, secrets) = Http(receiver);
        var config = new JsonObject
        {
            ["urlTemplate"] = receiver.BaseUrl + "/api/ap/invoices",
            ["auth"] = new JsonObject { ["type"] = "oauth2ClientCredentials", ["tokenUrl"] = receiver.BaseUrl + "/oauth/token", ["clientId"] = "docproc", ["scope"] = "ap.write" },
        };
        var context = Context(receiver, config);
        await secrets.SetAsync(context.Destination.SecretRef!, "client-secret", Ct);

        (await transport.SendAsync(context, Ct)).Kind.ShouldBe(DeliveryOutcomeKind.Delivered);
        (await transport.SendAsync(context, Ct)).Kind.ShouldBe(DeliveryOutcomeKind.Delivered);

        var token = receiver.Requests.Where(r => r.PathAndQuery == "/oauth/token").ShouldHaveSingleItem("the token is cached until it expires");
        token.Text.ShouldContain("grant_type=client_credentials");
        token.Text.ShouldContain("client_id=docproc");
        token.Text.ShouldContain("client_secret=client-secret");
        token.Text.ShouldContain("scope=ap.write");

        // The receiver stops taking the token: the 401 drops it, so the next attempt asks for a new one.
        receiver.Respond = request => request.PathAndQuery == "/oauth/token" ? new ReceiverAnswer(200, """{"access_token":"tok-2","expires_in":3600}""") : request.Header("Authorization") == "Bearer tok-2" ? new ReceiverAnswer(201) : new ReceiverAnswer(401);
        var refused = await transport.SendAsync(context, Ct);
        refused.Kind.ShouldBe(DeliveryOutcomeKind.Retryable);
        refused.ErrorClass.ShouldBe("retryable:token");
        (await transport.SendAsync(context, Ct)).Kind.ShouldBe(DeliveryOutcomeKind.Delivered);
        receiver.Requests.Count(r => r.PathAndQuery == "/oauth/token").ShouldBe(2);
    }

    [Fact]
    public async Task A_response_mapping_captures_the_receivers_id_and_the_excerpt_masks_the_secret()
    {
        using var receiver = new FakeReceiver { Default = new ReceiverAnswer(201, """{"erpId":"AP-0042","echo":"Bearer s3cret-value"}""") };
        var (transport, secrets) = Http(receiver);
        var context = Context(receiver, new JsonObject { ["urlTemplate"] = receiver.BaseUrl + "/x", ["auth"] = new JsonObject { ["type"] = "bearer" }, ["responseMapping"] = "erpId" });
        await secrets.SetAsync(context.Destination.SecretRef!, "s3cret-value", Ct);

        var outcome = await transport.SendAsync(context, Ct);

        outcome.ExternalId.ShouldBe("AP-0042");
        outcome.ResponseExcerpt.ShouldNotBeNull().ShouldNotContain("s3cret-value");
        outcome.ResponseExcerpt.ShouldContain("***");

        receiver.Default = new ReceiverAnswer(201, "not json");
        var noJson = await transport.SendAsync(context, Ct);
        noJson.Kind.ShouldBe(DeliveryOutcomeKind.Delivered, "a response mapping that finds nothing does not fail the delivery");
        noJson.ExternalId.ShouldBeNull();
        noJson.Note.ShouldNotBeNull().ShouldContain("not JSON");
    }

    [Theory]
    [InlineData(HttpAuthTypes.Basic, "S3cretPassw0rd")]
    [InlineData(HttpAuthTypes.Bearer, "abc")]
    [InlineData(HttpAuthTypes.ApiKeyHeader, "k&y=1/2")]
    public async Task The_excerpt_masks_the_credential_as_it_travels_whatever_its_length(string type, string secret)
    {
        // A receiver whose error page echoes the request's headers (a debugging proxy, say).
        using var receiver = new FakeReceiver
        {
            Respond = r => new ReceiverAnswer(400, $$"""{"error":"bad","authorization":"{{r.Header("Authorization")}}","key":"{{r.Header("X-Erp-Key")}}","form":"{{Uri.EscapeDataString(secret)}}"}"""),
        };
        var (transport, secrets) = Http(receiver);
        var auth = new JsonObject { ["type"] = type };
        if (type == HttpAuthTypes.Basic)
        {
            auth["username"] = "erpuser";
        }

        if (type == HttpAuthTypes.ApiKeyHeader)
        {
            auth["headerName"] = "X-Erp-Key";
        }

        var context = Context(receiver, new JsonObject { ["urlTemplate"] = receiver.BaseUrl + "/x", ["auth"] = auth });
        await secrets.SetAsync(context.Destination.SecretRef!, secret, Ct);

        var outcome = await transport.SendAsync(context, Ct);

        outcome.Kind.ShouldBe(DeliveryOutcomeKind.Permanent);
        var excerpt = outcome.ResponseExcerpt.ShouldNotBeNull();
        excerpt.ShouldNotContain(secret);
        excerpt.ShouldNotContain(Uri.EscapeDataString(secret));
        excerpt.ShouldNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("erpuser:" + secret)));
        excerpt.ShouldContain("\"error\":\"bad\"");
    }

    [Theory]
    [InlineData("a b(c!")]
    [InlineData("x y*z'()")]
    [InlineData("plain-secret_1")]
    public async Task A_token_endpoint_that_echoes_the_posted_form_gets_the_client_secret_masked_as_it_was_sent(string secret)
    {
        // FormUrlEncodedContent writes a space as '+' and escapes !()*' (a%20 is not what travels).
        using var receiver = new FakeReceiver { Respond = r => new ReceiverAnswer(400, "invalid_client for request: " + r.Text) };
        var (transport, secrets) = Http(receiver);
        var context = Context(receiver, new JsonObject
        {
            ["urlTemplate"] = receiver.BaseUrl + "/x",
            ["auth"] = new JsonObject { ["type"] = "oauth2ClientCredentials", ["tokenUrl"] = receiver.BaseUrl + "/oauth/token", ["clientId"] = "docproc" },
        });
        await secrets.SetAsync(context.Destination.SecretRef!, secret, Ct);

        var outcome = await transport.SendAsync(context, Ct);

        outcome.ErrorClass.ShouldBe("permanent:token");
        var sent = receiver.Requests.ShouldHaveSingleItem().Text;
        var encoded = HttpDeliveryTransport.FormEncoded(secret);
        sent.ShouldContain("client_secret=" + encoded, Case.Sensitive, "the mask is the encoder's own output");
        var excerpt = outcome.ResponseExcerpt.ShouldNotBeNull();
        excerpt.ShouldContain("client_secret=***");
        excerpt.ShouldNotContain(encoded);
        excerpt.ShouldContain("client_id=docproc");
    }

    [Fact]
    public void A_secret_echoed_across_the_excerpts_cut_is_masked_whole_whatever_the_bodys_characters()
    {
        const string secret = "SECRET-0123456789";
        var masked = HttpDeliveryTransport.Masked(HttpAuthTypes.Bearer, null, secret, null);
        foreach (var filler in new[] { "a", "é", "€", "😀" })
        {
            // The secret starts just before byte 8192 and before character 4096, where the old excerpt cut the body.
            foreach (var at in new[] { 8192 - 5, DeliveryAttempt.MaxExcerptLength - 5 })
            {
                var prefix = new StringBuilder();
                while (Encoding.UTF8.GetByteCount(prefix.ToString()) + Encoding.UTF8.GetByteCount(filler) <= at)
                {
                    prefix.Append(filler);
                }

                var body = Encoding.UTF8.GetBytes(prefix + secret + string.Concat(Enumerable.Repeat(filler, 5000)));
                var excerpt = HttpDeliveryTransport.Excerpt(body, masked).ShouldNotBeNull();
                excerpt.Length.ShouldBeLessThanOrEqualTo(DeliveryAttempt.MaxExcerptLength);
                for (var length = 4; length <= secret.Length; length++)
                {
                    excerpt.ShouldNotContain(secret[..length], Case.Sensitive, $"filler '{filler}' at byte {at}: no part of the secret survives");
                }
            }
        }
    }

    [Theory]
    [InlineData("tok/en\"<x>")]
    [InlineData("key&value'1")]
    public void The_masks_cover_the_json_and_html_escaped_forms_of_a_secret(string secret)
    {
        var masked = HttpDeliveryTransport.Masked(HttpAuthTypes.ApiKeyHeader, null, secret, null);
        var strict = System.Text.Json.JsonSerializer.Serialize(secret)[1..^1];
        var relaxed = System.Text.Json.JsonSerializer.Serialize(secret, Relaxed)[1..^1];
        var body = Encoding.UTF8.GetBytes($$"""{"strict":"{{strict}}","relaxed":"{{relaxed}}","slashes":"{{relaxed.Replace("/", "\\/", StringComparison.Ordinal)}}","html":"{{System.Net.WebUtility.HtmlEncode(secret)}}"}""");

        var excerpt = HttpDeliveryTransport.Excerpt(body, masked).ShouldNotBeNull();

        excerpt.ShouldBe("""{"strict":"***","relaxed":"***","slashes":"***","html":"***"}""");
    }

    [Theory]
    [InlineData(HttpAuthTypes.Bearer, "abc\r\nInjected: yes")]
    [InlineData(HttpAuthTypes.ApiKeyHeader, "abc\nInjected: yes")]
    [InlineData(HttpAuthTypes.ApiKeyHeader, "kéy")]
    [InlineData(HttpAuthTypes.Basic, "pass\r\nword")]
    [InlineData(HttpAuthTypes.HmacSha256, "key\0")]
    public async Task A_stored_secret_a_header_cannot_carry_is_permanent_and_nothing_is_sent(string type, string secret)
    {
        // Saved before the API refused such secrets: the transport refuses them too, once, instead of retrying 15 times.
        using var receiver = new FakeReceiver();
        var (transport, secrets) = Http(receiver);
        var auth = new JsonObject { ["type"] = type };
        if (type == HttpAuthTypes.ApiKeyHeader)
        {
            auth["headerName"] = "X-Erp-Key";
        }

        if (type == HttpAuthTypes.Basic)
        {
            auth["username"] = "docproc";
        }

        var context = Context(receiver, new JsonObject { ["urlTemplate"] = receiver.BaseUrl + "/x", ["auth"] = auth });
        await secrets.SetAsync(context.Destination.SecretRef!, secret, Ct);

        var outcome = await transport.SendAsync(context, Ct);

        outcome.Kind.ShouldBe(DeliveryOutcomeKind.Permanent);
        outcome.ErrorClass.ShouldBe("permanent:secret");
        receiver.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("idempotency-key")]
    [InlineData("X-DocProc-Signature")]
    [InlineData("X-DocProc-Document-Id")]
    [InlineData("Content-Length")]
    [InlineData("Transfer-Encoding")]
    public async Task An_api_key_header_named_like_a_header_the_transport_sets_is_refused_when_saved_and_when_sent(string headerName)
    {
        var auth = new JsonObject { ["type"] = HttpAuthTypes.ApiKeyHeader, ["headerName"] = headerName };
        DestinationConfig.Validate(OutputTransports.Http, new JsonObject { ["urlTemplate"] = "https://erp.example/x", ["auth"] = auth.DeepClone() })
            .ShouldContain(p => p.Field == "config.auth.headerName");
        DestinationConfig.Validate(OutputTransports.Http, new JsonObject { ["urlTemplate"] = "https://erp.example/x", ["headers"] = new JsonObject { [headerName] = "x" } })
            .ShouldContain(p => p.Field == "config.headers." + headerName);

        using var receiver = new FakeReceiver();
        var (transport, secrets) = Http(receiver);
        var context = Context(receiver, new JsonObject { ["urlTemplate"] = receiver.BaseUrl + "/x", ["auth"] = auth });
        await secrets.SetAsync(context.Destination.SecretRef!, "key-1", Ct);

        var outcome = await transport.SendAsync(context, Ct);

        outcome.ErrorClass.ShouldBe("permanent:auth");
        receiver.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_request_the_headers_cannot_carry_is_permanent_not_an_unexpected_transport_failure()
    {
        // MediaTypeHeaderValue.Parse throws FormatException: the same on every attempt, so no 15 retries.
        using var receiver = new FakeReceiver();
        var (transport, _) = Http(receiver);
        var context = Context(receiver) with { ContentType = "not a media type" };

        var outcome = await transport.SendAsync(context, Ct);

        outcome.ErrorClass.ShouldBe("permanent:request");
        receiver.Requests.ShouldBeEmpty();
    }

    [Fact]
    public void An_api_key_may_still_go_in_a_header_of_its_own_and_the_secrets_are_checked_per_auth_type()
    {
        foreach (var header in new[] { "X-Api-Key", "Authorization", "X-Erp-Key" })
        {
            DestinationConfig.Validate(OutputTransports.Http, new JsonObject { ["urlTemplate"] = "https://erp.example/x", ["auth"] = new JsonObject { ["type"] = HttpAuthTypes.ApiKeyHeader, ["headerName"] = header } })
                .ShouldBeEmpty(header);
        }

        DestinationConfig.SecretProblem(HttpAuthTypes.Bearer, "abc DEF\t~!").ShouldBeNull();
        DestinationConfig.SecretProblem(HttpAuthTypes.Bearer, "abc\rdef").ShouldNotBeNull();
        DestinationConfig.SecretProblem(HttpAuthTypes.Bearer, "kéy").ShouldNotBeNull();
        DestinationConfig.SecretProblem(HttpAuthTypes.Basic, "pässwört").ShouldBeNull("basic sends it base64");
        DestinationConfig.SecretProblem(HttpAuthTypes.OAuth2ClientCredentials, "sécret").ShouldBeNull("the token request form-encodes it");
        DestinationConfig.SecretProblem(HttpAuthTypes.HmacSha256, "clé").ShouldBeNull("the key never leaves");
        DestinationConfig.SecretProblem(HttpAuthTypes.HmacSha256, "a\nb").ShouldNotBeNull();
    }

    [Fact]
    public void A_config_change_that_sends_the_secret_elsewhere_is_named()
    {
        JsonObject Config(string url, string type = HttpAuthTypes.Bearer, string? update = null, string? tokenUrl = null, string? header = null)
        {
            var auth = new JsonObject { ["type"] = type };
            if (tokenUrl is not null)
            {
                auth["tokenUrl"] = tokenUrl;
                auth["clientId"] = "docproc";
            }

            if (header is not null)
            {
                auth["headerName"] = header;
            }

            var config = new JsonObject { ["urlTemplate"] = url, ["auth"] = auth };
            if (update is not null)
            {
                config["updateUrlTemplate"] = update;
            }

            return config;
        }

        var erp = Config("https://erp.example/api/ap");
        DestinationConfig.SecretRedirects(erp, Config("https://erp.example/api/ap/v2?x=1")).ShouldBeEmpty("the path is the receiver's own");
        DestinationConfig.SecretRedirects(erp, Config("HTTPS://ERP.example:443/other")).ShouldBeEmpty("the same origin written another way");
        DestinationConfig.SecretRedirects(erp, Config("https://attacker.example/api/ap")).ShouldContain("the host of urlTemplate");
        DestinationConfig.SecretRedirects(erp, Config("https://erp.example:8443/api/ap")).ShouldContain("the host of urlTemplate");
        DestinationConfig.SecretRedirects(erp, Config("http://erp.example/api/ap")).ShouldContain("the host of urlTemplate");
        DestinationConfig.SecretRedirects(erp, Config("https://erp.example/api/ap", update: "https://attacker.example/{{meta.documentId}}")).ShouldBe(["the host of updateUrlTemplate"]);
        DestinationConfig.SecretRedirects(erp, Config("https://erp.example/api/ap", type: HttpAuthTypes.Basic)).ShouldBe(["auth.type"]);
        DestinationConfig.SecretRedirects(erp, Config("https://attacker.example/", type: HttpAuthTypes.None)).ShouldBeEmpty("none sends nothing, and a later move to an auth that sends a secret changes auth.type again");

        // A bearer token, a basic password or any other credential does not become an HMAC key for another host
        // without the secret again: the receiver would get HMAC(secret, timestamp.body) over text it knows.
        DestinationConfig.SecretRedirects(erp, Config("https://attacker.example/", type: HttpAuthTypes.HmacSha256)).ShouldBe(["auth.type"]);
        DestinationConfig.SecretRedirects(erp, Config("https://erp.example/api/ap", type: HttpAuthTypes.HmacSha256)).ShouldBe(["auth.type"]);
        var basic = Config("https://erp.example/api", HttpAuthTypes.Basic);
        DestinationConfig.SecretRedirects(basic, Config("https://collector.attacker.example/hook", HttpAuthTypes.HmacSha256)).ShouldBe(["auth.type"]);
        var none = Config("https://erp.example/api", HttpAuthTypes.None);
        DestinationConfig.SecretRedirects(none, Config("https://erp.example/api", HttpAuthTypes.HmacSha256)).ShouldBe(["auth.type"]);
        DestinationConfig.SecretRedirects(none, Config("https://erp.example/api", HttpAuthTypes.Bearer)).ShouldBe(["auth.type"]);

        // A key set up for HMAC signs and never travels: it may follow the destination to another origin.
        var hmac = Config("https://erp.example/api", HttpAuthTypes.HmacSha256);
        DestinationConfig.SecretRedirects(hmac, Config("https://other.example/hook", HttpAuthTypes.HmacSha256)).ShouldBeEmpty("an HMAC key never leaves");
        DestinationConfig.SecretRedirects(hmac, Config("https://erp.example/api", HttpAuthTypes.Bearer)).ShouldBe(["auth.type"]);

        var oauth = Config("https://erp.example/api/ap", HttpAuthTypes.OAuth2ClientCredentials, tokenUrl: "https://login.example/token");
        DestinationConfig.SecretRedirects(oauth, Config("https://erp.example/api/ap", HttpAuthTypes.OAuth2ClientCredentials, tokenUrl: "https://login.example/v2/token")).ShouldBeEmpty();
        DestinationConfig.SecretRedirects(oauth, Config("https://erp.example/api/ap", HttpAuthTypes.OAuth2ClientCredentials, tokenUrl: "https://attacker.example/token")).ShouldBe(["the host of auth.tokenUrl"]);

        var key = Config("https://erp.example/api/ap", HttpAuthTypes.ApiKeyHeader, header: "X-Erp-Key");
        DestinationConfig.SecretRedirects(key, Config("https://erp.example/api/ap", HttpAuthTypes.ApiKeyHeader, header: "x-erp-key")).ShouldBeEmpty();
        DestinationConfig.SecretRedirects(key, Config("https://erp.example/api/ap", HttpAuthTypes.ApiKeyHeader, header: "X-Debug-Echo")).ShouldBe(["auth.headerName"]);
    }

    [Fact]
    public async Task A_cached_oauth_token_is_not_used_for_another_token_url_or_secret()
    {
        var issued = 0;
        using var receiver = new FakeReceiver
        {
            Respond = request => request.PathAndQuery.EndsWith("/token", StringComparison.Ordinal)
                ? new ReceiverAnswer(200, $$"""{"access_token":"tok-{{Interlocked.Increment(ref issued)}}","expires_in":3600}""")
                : new ReceiverAnswer(201),
        };
        var (transport, secrets) = Http(receiver);
        var auth = new JsonObject { ["type"] = "oauth2ClientCredentials", ["tokenUrl"] = receiver.BaseUrl + "/a/token", ["clientId"] = "docproc" };
        var context = Context(receiver, new JsonObject { ["urlTemplate"] = receiver.BaseUrl + "/x", ["auth"] = auth });
        await secrets.SetAsync(context.Destination.SecretRef!, "client-secret", Ct);

        (await transport.SendAsync(context, Ct)).Kind.ShouldBe(DeliveryOutcomeKind.Delivered);
        (await transport.SendAsync(context, Ct)).Kind.ShouldBe(DeliveryOutcomeKind.Delivered);
        issued.ShouldBe(1);

        // Another token URL (a PATCH in the API process, which cannot reach this cache): a new token.
        var moved = context with { Config = new JsonObject { ["urlTemplate"] = receiver.BaseUrl + "/x", ["auth"] = new JsonObject { ["type"] = "oauth2ClientCredentials", ["tokenUrl"] = receiver.BaseUrl + "/b/token", ["clientId"] = "docproc" } } };
        (await transport.SendAsync(moved, Ct)).Kind.ShouldBe(DeliveryOutcomeKind.Delivered);
        issued.ShouldBe(2);
        receiver.Requests[^1].Header("Authorization").ShouldBe("Bearer tok-2");

        // The same slot set again later (a rotation back to slot 1): a new token.
        context.Destination.SecretSetAt = DateTimeOffset.UnixEpoch.AddDays(1);
        (await transport.SendAsync(moved, Ct)).Kind.ShouldBe(DeliveryOutcomeKind.Delivered);
        issued.ShouldBe(3);
    }

    [Fact]
    public async Task The_token_request_counts_against_the_attempts_timeout()
    {
        using var receiver = new FakeReceiver
        {
            Respond = request =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(3));
                return new ReceiverAnswer(200, """{"access_token":"tok","expires_in":3600}""");
            },
        };
        var (transport, secrets) = Http(receiver);
        var context = Context(receiver, new JsonObject
        {
            ["urlTemplate"] = receiver.BaseUrl + "/x",
            ["timeoutSeconds"] = 1,
            ["auth"] = new JsonObject { ["type"] = "oauth2ClientCredentials", ["tokenUrl"] = receiver.BaseUrl + "/oauth/token", ["clientId"] = "docproc" },
        });
        await secrets.SetAsync(context.Destination.SecretRef!, "client-secret", Ct);

        var outcome = await transport.SendAsync(context, Ct);

        outcome.ErrorClass.ShouldBe("retryable:token");
        outcome.DurationMs.ShouldBeLessThan(2900, "the attempt's 1 s budget ends the token request, not its own 30 s");
    }

    [Fact]
    public async Task A_token_endpoint_answer_is_read_up_to_its_bound_never_whole()
    {
        // A token endpoint that announces 300 MB and streams it: the delivery reads 64 KB of it and stops.
        using var endless = new StreamingServer(contentLength: 300L * 1024 * 1024, status: 200);
        using var failing = new StreamingServer(contentLength: 300L * 1024 * 1024, status: 500);
        var secrets = new InMemorySecretStore();
        var transport = Transport(new DeliveryEndpointPolicy([endless.Host, failing.Host]), secrets);
        var context = Context(null, new JsonObject
        {
            ["urlTemplate"] = endless.BaseUrl + "/api/x",
            ["auth"] = new JsonObject { ["type"] = "oauth2ClientCredentials", ["tokenUrl"] = endless.BaseUrl + "/oauth/token", ["clientId"] = "docproc" },
        });
        await secrets.SetAsync(context.Destination.SecretRef!, "client-secret", Ct);

        var outcome = await transport.SendAsync(context, Ct);

        outcome.Kind.ShouldBe(DeliveryOutcomeKind.Permanent);
        outcome.ErrorClass.ShouldBe("permanent:token");
        outcome.Error.ShouldNotBeNull().ShouldContain("larger than 64 KB");
        endless.BytesSent.ShouldBeLessThan(32L * 1024 * 1024, "the client stops reading and hangs up; the old read took all 300 MB");

        var failed = Context(null, new JsonObject
        {
            ["urlTemplate"] = failing.BaseUrl + "/api/x",
            ["auth"] = new JsonObject { ["type"] = "oauth2ClientCredentials", ["tokenUrl"] = failing.BaseUrl + "/oauth/token", ["clientId"] = "docproc" },
        });
        await secrets.SetAsync(failed.Destination.SecretRef!, "client-secret", Ct);
        var refused = await transport.SendAsync(failed, Ct);
        refused.ErrorClass.ShouldBe("retryable:token");
        refused.ResponseExcerpt!.Length.ShouldBeLessThanOrEqualTo(DeliveryAttempt.MaxExcerptLength);
        failing.BytesSent.ShouldBeLessThan(32L * 1024 * 1024);
    }

    [Fact]
    public async Task A_multipart_body_carries_the_payload_and_the_original_file()
    {
        using var receiver = new FakeReceiver();
        var (transport, _) = Http(receiver);
        var config = new JsonObject
        {
            ["urlTemplate"] = receiver.BaseUrl + "/api/ap/invoices",
            ["multipart"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["name"] = "data", ["mapping"] = "self" }, new JsonObject { ["name"] = "file", ["content"] = "original" }) },
        };
        var original = new OriginalFile(Encoding.ASCII.GetBytes("%PDF-1.7 original"), "rechnung.pdf", "application/pdf");

        var outcome = await transport.SendAsync(Context(receiver, config, original), Ct);

        outcome.Kind.ShouldBe(DeliveryOutcomeKind.Delivered);
        var request = receiver.Requests.ShouldHaveSingleItem();
        request.Header("Content-Type")!.ShouldStartWith("multipart/form-data; boundary=");
        request.Text.ShouldContain("name=data");
        request.Text.ShouldContain(Encoding.UTF8.GetString(Payload));
        request.Text.ShouldContain("name=file");
        request.Text.ShouldContain("filename=rechnung.pdf");
        request.Text.ShouldContain("%PDF-1.7 original");

        var missing = await transport.SendAsync(Context(receiver, config, original: null), Ct);
        missing.ErrorClass.ShouldBe("permanent:original");

        // A content type that is not a media type (saved before mappings checked it, or an original's) fails for good
        // on the first attempt instead of being retried for 72 hours as a transport error.
        var badPayloadType = Context(receiver, config, original) with { ContentType = "not a media type" };
        (await transport.SendAsync(badPayloadType, Ct)).ErrorClass.ShouldBe("permanent:request");
        var badOriginal = new OriginalFile(original.Bytes, "rechnung.pdf", "pdf please");
        (await transport.SendAsync(Context(receiver, config, badOriginal), Ct)).ErrorClass.ShouldBe("permanent:request");
        receiver.Requests.Count.ShouldBe(1, "neither was sent");
    }

    [Fact]
    public async Task An_update_goes_with_the_update_method_and_url_naming_a_value_of_the_payload()
    {
        using var receiver = new FakeReceiver { Default = new ReceiverAnswer(200) };
        var (transport, _) = Http(receiver);
        var config = new JsonObject
        {
            ["urlTemplate"] = receiver.BaseUrl + "/api/article-prices",
            ["updateUrlTemplate"] = receiver.BaseUrl + "/api/article-prices/{{output.header.priceListId}}",
        };

        (await transport.SendAsync(Context(receiver, config, eventType: OutputEvents.Updated), Ct)).Kind.ShouldBe(DeliveryOutcomeKind.Delivered);
        (await transport.SendAsync(Context(receiver, config, eventType: OutputEvents.Approved), Ct)).Kind.ShouldBe(DeliveryOutcomeKind.Delivered);

        receiver.Requests[0].Method.ShouldBe("PUT");
        receiver.Requests[0].PathAndQuery.ShouldBe("/api/article-prices/PL%202027%2F01", "a value is one escaped segment");
        receiver.Requests[0].Header("X-DocProc-Event").ShouldBe(OutputEvents.Updated);
        receiver.Requests[1].Method.ShouldBe("POST");
        receiver.Requests[1].PathAndQuery.ShouldBe("/api/article-prices");

        var missing = await transport.SendAsync(Context(receiver, new JsonObject { ["urlTemplate"] = receiver.BaseUrl + "/x/{{output.header.nothing}}" }), Ct);
        missing.ErrorClass.ShouldBe("permanent:template");
    }

    [Fact]
    public async Task The_payload_cannot_choose_the_host_a_credential_goes_to()
    {
        using var receiver = new FakeReceiver { Default = new ReceiverAnswer(201) };
        var (transport, secrets) = Http(receiver);

        // Saved before the check of the config: a placeholder in the host, filled from the rendered payload.
        var config = new JsonObject { ["urlTemplate"] = $"http://{{{{output.h}}}}:{receiver.Port}/x", ["auth"] = new JsonObject { ["type"] = "bearer" } };
        var context = Context(receiver, config);
        context = context with { Values = context.Values with { Output = JsonNode.Parse("""{"h":"127.0.0.1"}""") } };
        await secrets.SetAsync(context.Destination.SecretRef!, "TOPSECRET", Ct);

        var outcome = await transport.SendAsync(context, Ct);
        outcome.ErrorClass.ShouldBe("permanent:template");
        receiver.Requests.ShouldBeEmpty("nothing is sent, so neither is the token");

        var update = Context(receiver, new JsonObject { ["urlTemplate"] = receiver.BaseUrl + "/x", ["updateUrlTemplate"] = $"http://127.0.0.1{{{{output.sfx}}}}:{receiver.Port}/x", ["auth"] = new JsonObject { ["type"] = "bearer" } }, eventType: OutputEvents.Updated);
        update = update with { Values = update.Values with { Output = JsonNode.Parse("""{"sfx":".nip.example"}""") } };
        await secrets.SetAsync(update.Destination.SecretRef!, "TOPSECRET", Ct);
        (await transport.SendAsync(update, Ct)).ErrorClass.ShouldBe("permanent:template");
        receiver.Requests.ShouldBeEmpty();

        // The check at send time compares the expanded URL with the template's literal scheme, host and port.
        DestinationConfig.KeepsTemplateOrigin("https://erp.example/api/{{output.x}}", new Uri("https://erp.example/api/PL%202027")).ShouldBeTrue();
        DestinationConfig.KeepsTemplateOrigin("https://ERP.example:443/api/{{output.x}}", new Uri("https://erp.example/api/1")).ShouldBeTrue();
        DestinationConfig.KeepsTemplateOrigin("https://erp.example/api/{{output.x}}", new Uri("https://evil.example/api/1")).ShouldBeFalse();
        DestinationConfig.KeepsTemplateOrigin("https://erp.example/api/{{output.x}}", new Uri("https://erp.example:8443/api/1")).ShouldBeFalse();
        DestinationConfig.KeepsTemplateOrigin("https://erp.example/api/{{output.x}}", new Uri("http://erp.example/api/1")).ShouldBeFalse();
        DestinationConfig.KeepsTemplateOrigin("https://{{output.h}}/api", new Uri("https://x/api")).ShouldBeFalse("a placeholder in the origin is never the template's own");

        // A value in the path stays one escaped segment: the receiver's host is the template's.
        var path = context with { Config = new JsonObject { ["urlTemplate"] = receiver.BaseUrl + "/x/{{output.h}}", ["auth"] = new JsonObject { ["type"] = "bearer" } }, Values = context.Values with { Output = JsonNode.Parse("""{"h":"@evil.example:1/../y"}""") } };
        (await transport.SendAsync(path, Ct)).Kind.ShouldBe(DeliveryOutcomeKind.Delivered);
        receiver.Requests.ShouldHaveSingleItem().PathAndQuery.ShouldBe("/x/%40evil.example%3A1%2F..%2Fy");
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    public async Task A_value_that_is_a_dot_segment_cannot_move_the_request_to_another_path(string value)
    {
        using var receiver = new FakeReceiver { Default = new ReceiverAnswer(200) };
        var (transport, _) = Http(receiver);
        var config = new JsonObject
        {
            ["urlTemplate"] = receiver.BaseUrl + "/api/article-prices",
            ["updateUrlTemplate"] = receiver.BaseUrl + "/api/v1/article-prices/{{output.a}}/{{output.b}}/delete",
        };
        var context = Context(receiver, config, eventType: OutputEvents.Updated);
        context = context with { Values = context.Values with { Output = new JsonObject { ["a"] = value, ["b"] = value } } };

        // Escaping leaves dots alone, and the URL parser removes dot segments: /api/v1/article-prices/../../delete
        // would be sent to /api/delete. The value is refused as a permanent template error, and nothing is sent.
        var outcome = await transport.SendAsync(context, Ct);

        outcome.Kind.ShouldBe(DeliveryOutcomeKind.Permanent);
        outcome.ErrorClass.ShouldBe("permanent:template");
        outcome.Error!.ShouldContain("dot segment");
        receiver.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/api/items?note={{output.x}}", ".", "z", "/api/items?note=.")]
    [InlineData("/api/items?note={{output.x}}", "..", "z", "/api/items?note=..")]
    [InlineData("/api/items?note={{output.x}}&b={{output.y}}", "..", "..", "/api/items?note=..&b=..")]
    [InlineData("/api/items/x#{{output.x}}", "..", "z", "/api/items/x")]
    [InlineData("/api/v1/{{output.x}}{{output.y}}", ".", "z", "/api/v1/.z")]
    [InlineData("/api/v1/{{output.x}}/{{output.y}}", "...", "a.b", "/api/v1/.../a.b")]
    [InlineData("/api/v1/{{output.x}}.{{output.y}}", ".", ".", "/api/v1/...")]
    public async Task A_dot_value_that_is_no_whole_path_segment_is_sent(string path, string x, string y, string sent)
    {
        using var receiver = new FakeReceiver { Default = new ReceiverAnswer(200) };
        var (transport, _) = Http(receiver);
        var context = Context(receiver, new JsonObject { ["urlTemplate"] = receiver.BaseUrl + path });
        context = context with { Values = context.Values with { Output = new JsonObject { ["x"] = x, ["y"] = y } } };

        // A dot segment is a whole path segment: in the query, in the fragment or beside other text of its segment a
        // value of "." or ".." moves nothing, and the delivery goes out.
        (await transport.SendAsync(context, Ct)).Kind.ShouldBe(DeliveryOutcomeKind.Delivered);
        receiver.Requests.ShouldHaveSingleItem().PathAndQuery.ShouldBe(sent);
    }

    [Theory]
    [InlineData("/api/v1/{{output.x}}{{output.y}}/delete", ".", ".")]
    [InlineData("/api/v1/.{{output.x}}/delete", ".", "z")]
    [InlineData("/api/v1/{{output.x}}", "..", "z")]
    [InlineData("/api/v1/{{output.x}}?id={{output.y}}", ".", "z")]
    [InlineData("/api/v1/{{output.y}}/{{output.x}}/delete", ".", "z")]
    public async Task A_value_that_makes_a_whole_path_segment_a_dot_segment_is_refused(string path, string x, string y)
    {
        using var receiver = new FakeReceiver { Default = new ReceiverAnswer(200) };
        var (transport, _) = Http(receiver);
        var context = Context(receiver, new JsonObject { ["urlTemplate"] = receiver.BaseUrl + path });
        context = context with { Values = context.Values with { Output = new JsonObject { ["x"] = x, ["y"] = y } } };

        // Alone or with the template's text or another value, the segment is "." or "..", which the URL parser removes
        // (/api/v1/z/./delete is /api/v1/z/delete): a permanent template error, and nothing is sent.
        var outcome = await transport.SendAsync(context, Ct);

        outcome.ErrorClass.ShouldBe("permanent:template");
        outcome.Error!.ShouldContain("dot segment");
        receiver.Requests.ShouldBeEmpty();
    }

    [Fact]
    public void A_dot_segment_of_the_templates_own_text_is_left_to_the_path_check()
    {
        // Only a segment that holds a value is refused when the URL is expanded; the template's own text is the
        // destination's, and the path check judges what the parser makes of it.
        var values = new DeliveryTemplateValues(Guid.NewGuid(), 1, OutputEvents.Approved, "Invoice", Guid.NewGuid(), new JsonObject { ["x"] = "a" });
        DeliveryTemplates.ExpandUrl("https://erp.example/api/./v1/{{output.x}}", values).ShouldBe("https://erp.example/api/./v1/a");
        DeliveryTemplates.ExpandUrl("https://erp.example/api/v1/{{output.x}}/../b", values).ShouldBe("https://erp.example/api/v1/a/../b");
        DestinationConfig.KeepsTemplatePath("https://erp.example/api/v1/{{output.x}}/../../b", new Uri("https://erp.example/api/v1/a/../../b")).ShouldBeFalse();
    }

    [Fact]
    public void The_expanded_path_must_stay_below_the_templates_literal_path()
    {
        // The send-time check behind the value check: whatever the values were, the URL parser's path of the expanded
        // URL starts with the path of the template's text before its first placeholder.
        DestinationConfig.KeepsTemplatePath("https://erp.example/api/v1/.{{output.x}}/x", new Uri("https://erp.example/api/v1/../x")).ShouldBeFalse("/api/v1/../x is /api/x");
        DestinationConfig.KeepsTemplatePath("https://erp.example/api/v1/{{output.x}}", new Uri("https://erp.example/api/v1/PL%202027%2F01")).ShouldBeTrue();
        DestinationConfig.KeepsTemplatePath("https://erp.example/api/v1/{{output.x}}", new Uri("https://erp.example/api/v1/../admin")).ShouldBeFalse();
        DestinationConfig.KeepsTemplatePath("https://erp.example/api/items?id={{output.x}}", new Uri("https://erp.example/api/items?id=..")).ShouldBeTrue("a value in the query does not touch the path");
        DestinationConfig.KeepsTemplatePath("https://erp.example/api/items", new Uri("https://erp.example/api/items")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_receiver_that_does_not_answer_in_time_is_retried()
    {
        using var receiver = new FakeReceiver
        {
            Respond = _ =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(2));
                return new ReceiverAnswer(201);
            },
        };
        var (transport, _) = Http(receiver);

        var outcome = await transport.SendAsync(Context(receiver, new JsonObject { ["urlTemplate"] = receiver.BaseUrl + "/slow", ["timeoutSeconds"] = 1 }), Ct);

        outcome.Kind.ShouldBe(DeliveryOutcomeKind.Retryable);
        outcome.ErrorClass.ShouldBe("retryable:timeout");
    }

    [Fact]
    public async Task The_file_drop_writes_below_the_tenants_folder_and_never_overwrites_other_bytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "docproc-drop-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var drop = new BlobDropTransport(root);
            var config = new JsonObject { ["path"] = "exports/ap", ["fileNameTemplate"] = "{{meta.typeKey}}-{{output.header.docNo}}-{{meta.version}}.json" };
            var context = Context(null, config);

            var outcome = await drop.SendAsync(context, Ct);

            outcome.Kind.ShouldBe(DeliveryOutcomeKind.Delivered);
            var file = Path.Combine(root, Tenant.ToString("D"), "exports", "ap", "Invoice-RE-1-7.json");
            File.ReadAllBytes(file).ShouldBe(Payload);
            Directory.GetFiles(Path.GetDirectoryName(file)!).ShouldHaveSingleItem("the temporary file was renamed");

            (await drop.SendAsync(context, Ct)).Kind.ShouldBe(DeliveryOutcomeKind.Delivered, "the same bytes again: a retry after the rename");
            var other = context with { Payload = Encoding.UTF8.GetBytes("""{"header":{"docNo":"RE-1"},"other":true}""") };
            var refused = await drop.SendAsync(other, Ct);
            refused.ErrorClass.ShouldBe("permanent:exists");
            File.ReadAllBytes(file).ShouldBe(Payload);

            var mapping = context with { MappingFileNameTemplate = "../{{output.header.docNo}}.json" };
            var named = await drop.SendAsync(mapping, Ct);
            named.Kind.ShouldBe(DeliveryOutcomeKind.Delivered, "the mapping version's template wins, its '..' made harmless");
            Directory.GetFiles(Path.Combine(root, Tenant.ToString("D"), "exports", "ap")).Length.ShouldBe(2);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void The_backoff_follows_the_schedule_with_jitter_and_honours_retry_after()
    {
        var options = new DeliveryOptions();
        var random = new Random(42);
        var expected = new[] { 1, 5, 15, 60, 180, 360, 720, 720, 720 };
        for (var attempt = 1; attempt <= expected.Length; attempt++)
        {
            var delay = DeliveryBackoff.Delay(attempt, null, options, random);
            delay.TotalMinutes.ShouldBeInRange(expected[attempt - 1] * 0.9, expected[attempt - 1] * 1.1, $"attempt {attempt}");
        }

        DeliveryBackoff.Delay(1, TimeSpan.FromSeconds(30), options, random).ShouldBe(TimeSpan.FromSeconds(30));
        DeliveryBackoff.Delay(1, TimeSpan.Zero, options, random).ShouldBe(TimeSpan.FromSeconds(1));
        DeliveryBackoff.Delay(1, TimeSpan.FromDays(3), options, random).ShouldBe(options.MaxRetryAfter);
        DeliveryBackoff.Delay(3, null, new DeliveryOptions { RetrySchedule = [TimeSpan.FromSeconds(10)] }, new Random(1)).TotalSeconds.ShouldBeInRange(9, 11);
    }

    [Fact]
    public void Destination_configs_take_the_new_settings_and_refuse_unknown_placeholders()
    {
        DestinationConfig.Validate(OutputTransports.Http, new JsonObject
        {
            ["urlTemplate"] = "https://erp.example/api/article-prices",
            ["updateMethod"] = "PUT",
            ["updateUrlTemplate"] = "https://erp.example/api/article-prices/{{output.header.priceListId}}",
            ["multipart"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["name"] = "data", ["mapping"] = "self" }, new JsonObject { ["name"] = "file", ["content"] = "original" }) },
        }).ShouldBeEmpty();

        DestinationConfig.Validate(OutputTransports.Http, new JsonObject { ["urlTemplate"] = "https://erp.example/{{supplier.vatId}}" })
            .ShouldContain(p => p.Field == "config.urlTemplate");
        DestinationConfig.Validate(OutputTransports.Http, new JsonObject { ["urlTemplate"] = "https://erp.example/", ["updateMethod"] = "DELETE" })
            .ShouldContain(p => p.Field == "config.updateMethod");
        DestinationConfig.Validate(OutputTransports.Http, new JsonObject { ["urlTemplate"] = "https://erp.example/", ["multipart"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["name"] = "file", ["content"] = "original" }) } })
            .ShouldContain(p => p.Field == "config.multipart.parts");
        DestinationConfig.Validate(OutputTransports.Http, new JsonObject { ["urlTemplate"] = "https://erp.example/", ["headers"] = new JsonObject { ["X-DocProc-Signature"] = "x" } })
            .ShouldContain(p => p.Field == "config.headers.X-DocProc-Signature");
        foreach (var url in new[] { "http://{{output.h}}:8080/x", "https://erp{{output.sfx}}/api", "https://erp.example{{output.docNo}}/api", "https://erp.example:{{meta.version}}/", "{{output.url}}", "http{{output.s}}://erp.example/", "https://erp.example\\{{output.x}}/api" })
        {
            foreach (var field in new[] { "urlTemplate", "updateUrlTemplate" })
            {
                var config = new JsonObject { ["urlTemplate"] = "https://erp.example/api", ["auth"] = new JsonObject { ["type"] = "bearer" } };
                config[field] = url;
                DestinationConfig.Validate(OutputTransports.Http, config).ShouldContain(p => p.Field == "config." + field && p.Message.Contains("path or the query only"), url);
            }
        }

        DestinationConfig.Validate(OutputTransports.Http, new JsonObject { ["urlTemplate"] = "https://erp.example/{{output.a}}?b={{meta.version}}#{{meta.event}}" }).ShouldBeEmpty();
        DestinationConfig.Validate(OutputTransports.Blob, new JsonObject { ["path"] = "a", ["fileNameTemplate"] = "{{meta.documentId}}-{{meta.version}}.json" }).ShouldBeEmpty();
        DestinationConfig.Validate(OutputTransports.Blob, new JsonObject { ["path"] = "a", ["fileNameTemplate"] = "{{doc.number}}.json" }).ShouldContain(p => p.Field == "config.fileNameTemplate");
    }

    private (HttpDeliveryTransport Transport, InMemorySecretStore Secrets) Http(FakeReceiver receiver)
    {
        var secrets = new InMemorySecretStore();
        return (Transport(new DeliveryEndpointPolicy([receiver.Host]), secrets), secrets);
    }

    private HttpDeliveryTransport Transport(DeliveryEndpointPolicy policy, InMemorySecretStore secrets)
    {
        var http = new DeliveryHttpClient(policy);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        return new HttpDeliveryTransport(http, secrets, new OAuthTokenCache(http, clock), fixture.Renderer, clock);
    }

    private static DeliveryContext Context(FakeReceiver? receiver, JsonObject? config = null, OriginalFile? original = null, string eventType = OutputEvents.Approved)
    {
        var documentId = Guid.CreateVersion7();
        var destinationId = Guid.CreateVersion7();
        var bindingId = Guid.CreateVersion7();
        var delivery = new OutboxDelivery
        {
            Id = Guid.CreateVersion7(),
            TenantId = Tenant,
            DocumentId = documentId,
            DocumentVersion = 7,
            BindingId = bindingId,
            DestinationId = destinationId,
            IdempotencyKey = DeliveryFanOut.IdempotencyKey(documentId, 7, bindingId),
            Event = eventType,
        };
        var destination = new OutputDestination
        {
            Id = destinationId,
            TenantId = Tenant,
            Name = "erp",
            Transport = OutputTransports.Http,
            CreatedBy = "test",
            SecretRef = $"tenant/{Tenant:D}/destination/{destinationId:D}/1",
        };
        config ??= new JsonObject { ["urlTemplate"] = (receiver?.BaseUrl ?? "http://unused") + "/api/ap/invoices" };
        var values = new DeliveryTemplateValues(documentId, 7, eventType, "Invoice", delivery.Id, JsonNode.Parse(Payload));
        return new DeliveryContext(delivery, destination, config, Payload, "application/json", values, null, _ => Task.FromResult(original));
    }
}

/// <summary>
/// A raw HTTP/1.1 server on loopback that answers every request with <c>Content-Length</c> bytes of <c>x</c>, written
/// from one small buffer until the length is reached or the client hangs up, and counts what it sent.
/// </summary>
internal sealed class StreamingServer : IDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private long _sent;

    public StreamingServer(long contentLength, int status)
    {
        _listener.Start();
        Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client, contentLength, status));
            }
        });
    }

    public int Port { get; }

    public string Host => $"127.0.0.1:{Port}";

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public long BytesSent => Interlocked.Read(ref _sent);

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // stopped
        }

        _stop.Dispose();
    }

    private async Task ServeAsync(System.Net.Sockets.TcpClient client, long contentLength, int status)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = new byte[64 * 1024];
                var seen = new StringBuilder();
                while (!seen.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(request, _stop.Token);
                    if (read == 0)
                    {
                        return;
                    }

                    seen.Append(Encoding.ASCII.GetString(request, 0, read));
                }

                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {contentLength}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, _stop.Token);
                var chunk = Enumerable.Repeat((byte)'x', 64 * 1024).ToArray();
                for (long left = contentLength; left > 0; left -= chunk.Length)
                {
                    var size = (int)Math.Min(chunk.Length, left);
                    await stream.WriteAsync(chunk.AsMemory(0, size), _stop.Token);
                    Interlocked.Add(ref _sent, size);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException)
            {
                // the client hung up
            }
        }
    }
}
