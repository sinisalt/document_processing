using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Api.Endpoints;
using DocProc.Application.Abstractions;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Outputs;
using DocProc.Infrastructure.Outputs.Delivery;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Api.Tests;

/// <summary>
/// The delivery routes of docs/11 §18 (P3 part B): the deliveries of a document with their attempts and payload, a
/// redelivery from the snapshot or rendered again, and the pull feed with its cursor and acknowledgements, the
/// <c>deliveries:read</c> API key scope (restricted to one destination or not), the roles, the <c>outputs</c> switch
/// and tenant isolation.
/// </summary>
public sealed class DeliveryEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deliveries_are_listed_per_document_with_their_attempts_payload_and_external_id()
    {
        var setup = await SetupAsync("Deliveries list", OutputTransports.Http);
        var delivered = await AddDeliveryAsync(setup, DeliveryStatus.Delivered, """{"docNo":"RE-1"}""", attempts: 2);
        var dead = await AddDeliveryAsync(setup, DeliveryStatus.DeadLettered, """{"docNo":"RE-1","v":2}""", attempts: 15, version: 2);
        factory.Run(db =>
        {
            db.DeliveryAttempts.Add(new DeliveryAttempt { TenantId = setup.Tenant, DeliveryId = delivered, At = factory.Clock.GetUtcNow(), StatusCode = 503, ErrorClass = "retryable:status", DurationMs = 12 });
            db.DeliveryAttempts.Add(new DeliveryAttempt { TenantId = setup.Tenant, DeliveryId = delivered, At = factory.Clock.GetUtcNow().AddMinutes(1), StatusCode = 201, ErrorClass = "delivered", DurationMs = 8 });
            db.DocumentExternalRefs.Add(new DocumentExternalRef { Id = Guid.CreateVersion7(), TenantId = setup.Tenant, DocumentId = setup.Document, DestinationId = setup.Destination, ExternalId = "AP-0042", Status = ExternalRefStatus.Accepted, UpdatedAt = factory.Clock.GetUtcNow() });
        });
        using var reviewer = factory.ClientFor(new TokenSpec("rev", [Roles.Reviewer], setup.Tenant.ToString()));

        var page = (await reviewer.GetFromJsonAsync<Page<DeliverySummary>>($"/api/v1/deliveries?documentId={setup.Document}", ApiFactory.Json, Ct))!;
        page.Items.Select(d => d.Id).ShouldBe([dead, delivered], "newest first");
        page.Items[0].BindingName.ShouldBe("ap");
        page.Items[0].DestinationName.ShouldBe("erp");
        page.Items[0].Transport.ShouldBe(OutputTransports.Http);
        page.Items[0].MappingVersion.ShouldBe(1);
        var onlyDead = (await reviewer.GetFromJsonAsync<Page<DeliverySummary>>("/api/v1/deliveries?status=deadLettered", ApiFactory.Json, Ct))!;
        onlyDead.Items.ShouldHaveSingleItem().Id.ShouldBe(dead);
        (await reviewer.GetAsync("/api/v1/deliveries?status=0", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var first = (await reviewer.GetFromJsonAsync<Page<DeliverySummary>>("/api/v1/deliveries?limit=1", ApiFactory.Json, Ct))!;
        first.NextCursor.ShouldNotBeNull();
        var second = (await reviewer.GetFromJsonAsync<Page<DeliverySummary>>($"/api/v1/deliveries?limit=1&cursor={Uri.EscapeDataString(first.NextCursor)}", ApiFactory.Json, Ct))!;
        second.Items.ShouldHaveSingleItem().Id.ShouldBe(delivered);

        var detail = (await reviewer.GetFromJsonAsync<DeliveryDetail>($"/api/v1/deliveries/{delivered}", ApiFactory.Json, Ct))!;
        detail.Attempts.Select(a => a.StatusCode).ShouldBe([503, 201]);
        detail.ExternalRef.ShouldNotBeNull().ExternalId.ShouldBe("AP-0042");
        var payload = await reviewer.GetAsync($"/api/v1/deliveries/{delivered}/payload", Ct);
        payload.StatusCode.ShouldBe(HttpStatusCode.OK);
        payload.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        (await payload.Content.ReadAsStringAsync(Ct)).ShouldBe("""{"docNo":"RE-1"}""");
    }

    [Fact]
    public async Task Deliveries_need_the_outputs_switch_and_stay_in_their_tenant()
    {
        var setup = await SetupAsync("Deliveries isolated", OutputTransports.Http);
        var delivery = await AddDeliveryAsync(setup, DeliveryStatus.DeadLettered, "{}");
        var other = factory.CreateTenant("Deliveries other");
        factory.Run(db => db.Tenants.Single(t => t.Id == other).Settings.Features!.Outputs = true);
        using var stranger = factory.TenantAdminOf(other);

        (await stranger.GetFromJsonAsync<Page<DeliverySummary>>("/api/v1/deliveries", ApiFactory.Json, Ct))!.Items.ShouldBeEmpty();
        (await stranger.GetAsync($"/api/v1/deliveries/{delivery}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await stranger.GetAsync($"/api/v1/deliveries/{delivery}/payload", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await stranger.PostAsJsonAsync($"/api/v1/deliveries/{delivery}:redeliver", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await stranger.GetAsync($"/api/v1/feeds/{setup.Destination}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var off = factory.CreateTenant("Deliveries off");
        using var offAdmin = factory.TenantAdminOf(off);
        (await offAdmin.GetAsync("/api/v1/deliveries", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_document_detail_says_whether_its_tenant_has_outputs_so_the_review_screen_shows_the_deliveries_tab()
    {
        var setup = await SetupAsync("Deliveries tab", OutputTransports.Http);
        using var reviewer = factory.ClientFor(new TokenSpec("rev", [Roles.Reviewer], setup.Tenant.ToString()));

        (await reviewer.GetFromJsonAsync<DocProc.Api.Documents.DocumentDetail>($"/api/v1/documents/{setup.Document}", ApiFactory.Json, Ct))!.Outputs.ShouldBeTrue();

        factory.Run(db => db.Tenants.Single(t => t.Id == setup.Tenant).Settings.Features!.Outputs = false);
        (await reviewer.GetFromJsonAsync<DocProc.Api.Documents.DocumentDetail>($"/api/v1/documents/{setup.Document}", ApiFactory.Json, Ct))!.Outputs.ShouldBeFalse("a tenant without the switch sees the review screen as before");
    }

    [Fact]
    public async Task A_dead_letter_is_redelivered_from_its_snapshot_as_a_new_delivery()
    {
        var setup = await SetupAsync("Redeliver", OutputTransports.Http);
        var dead = await AddDeliveryAsync(setup, DeliveryStatus.DeadLettered, """{"docNo":"RE-1"}""", attempts: 15);
        using var admin = factory.TenantAdminOf(setup.Tenant);
        using var reviewer = factory.ClientFor(new TokenSpec("rev", [Roles.Reviewer], setup.Tenant.ToString()));
        (await reviewer.PostAsJsonAsync($"/api/v1/deliveries/{dead}:redeliver", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "redelivering is an integration admin's");
        (await admin.PostAsJsonAsync($"/api/v1/deliveries/{dead}:redeliver", new { mode = "copy" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var response = await admin.PostAsJsonAsync($"/api/v1/deliveries/{dead}:redeliver", new { mode = "snapshot" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var redelivery = (await response.Content.ReadFromJsonAsync<DeliverySummary>(ApiFactory.Json, Ct))!;
        redelivery.Status.ShouldBe(nameof(DeliveryStatus.Pending));
        redelivery.RedeliveryOfId.ShouldBe(dead);
        redelivery.Attempts.ShouldBe(0);
        var original = factory.Query(db => db.OutboxDeliveries.Single(d => d.Id == dead));
        redelivery.IdempotencyKey.ShouldBe(original.IdempotencyKey + ":r1");
        redelivery.PayloadSha256.ShouldBe(original.PayloadSha256, "the same snapshot");
        original.Status.ShouldBe(DeliveryStatus.Cancelled, "the dead letter it replaces");
        factory.Queue.Deliveries.ShouldContain(m => m.DeliveryId == redelivery.Id && m.Attempt == 1);
        factory.Query(db => db.DocumentEvents.Where(e => e.DocumentId == setup.Document && e.EventType == DocumentEventTypes.RedeliveryRequested).Select(e => e.Actor).ToList())
            .ShouldHaveSingleItem().ShouldNotBeNull();

        var again = (await (await admin.PostAsJsonAsync($"/api/v1/deliveries/{dead}:redeliver", new { }, Ct)).Content.ReadFromJsonAsync<DeliverySummary>(ApiFactory.Json, Ct))!;
        again.IdempotencyKey.ShouldBe(original.IdempotencyKey + ":r2");
    }

    [Fact]
    public async Task A_redelivery_that_loses_the_race_for_its_key_is_a_409_not_a_500()
    {
        var setup = await SetupAsync("Redeliver race", OutputTransports.Http);
        var delivered = await AddDeliveryAsync(setup, DeliveryStatus.Delivered, """{"docNo":"RE-1"}""");
        var original = factory.Query(db => db.OutboxDeliveries.Single(d => d.Id == delivered));

        // A second request (a double click) counted the same redeliveries and saved ':r1' first; a delivered original
        // is not changed by a redelivery, so its row version does not catch the race, the unique key does. The row
        // stands in for the other request's insert, which this request's count did not see.
        factory.Run(db => db.OutboxDeliveries.Add(new OutboxDelivery
        {
            Id = Guid.CreateVersion7(),
            TenantId = setup.Tenant,
            OutboxMessageId = setup.Outbox,
            DocumentId = setup.Document,
            DocumentVersion = 1,
            BindingId = setup.Binding,
            DestinationId = setup.Destination,
            MappingVersionId = setup.MappingVersion,
            IdempotencyKey = original.IdempotencyKey + ":r1",
            Status = DeliveryStatus.Pending,
            CreatedAt = factory.Clock.GetUtcNow(),
        }));
        using var admin = factory.TenantAdminOf(setup.Tenant);

        var response = await admin.PostAsJsonAsync($"/api/v1/deliveries/{delivered}:redeliver", new { mode = "snapshot" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Delivery changed");
        factory.Query(db => db.OutboxDeliveries.Count(d => d.RedeliveryOfId == delivered)).ShouldBe(0);
        factory.Query(db => db.DocumentEvents.Count(e => e.DocumentId == setup.Document && e.EventType == DocumentEventTypes.RedeliveryRequested)).ShouldBe(0, "nothing of the refused request was saved");
    }

    [Fact]
    public async Task A_redelivery_renders_again_with_the_bindings_mapping_on_request()
    {
        var setup = await SetupAsync("Rerender", OutputTransports.Http);
        var dead = await AddDeliveryAsync(setup, DeliveryStatus.DeadLettered, """{"stale":true}""");
        using var admin = factory.TenantAdminOf(setup.Tenant);

        (await admin.PostAsJsonAsync($"/api/v1/deliveries/{dead}:redeliver", new { mode = "rerender", mappingVersion = 9 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync($"/api/v1/deliveries/{dead}:redeliver", new { mode = "snapshot", mappingVersion = 1 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var response = await admin.PostAsJsonAsync($"/api/v1/deliveries/{dead}:redeliver", new { mode = "rerender" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var redelivery = (await response.Content.ReadFromJsonAsync<DeliverySummary>(ApiFactory.Json, Ct))!;
        var payload = await admin.GetStringAsync($"/api/v1/deliveries/{redelivery.Id}/payload", Ct);
        var rendered = JsonDocument.Parse(payload).RootElement;
        rendered.GetProperty("documentId").GetString().ShouldBe(setup.Document.ToString("D"), "std.event rendered over the document");
        rendered.GetProperty("eventType").GetString().ShouldBe(OutputEvents.Approved);
    }

    [Fact]
    public async Task The_pull_feed_serves_entries_after_the_cursor_and_acknowledges_them()
    {
        var setup = await SetupAsync("Feed", OutputTransports.Pull);
        var entries = new List<Guid>();
        for (var i = 1; i <= 3; i++)
        {
            entries.Add(await AddDeliveryAsync(setup, DeliveryStatus.Sending, $$"""{"n":{{i}}}""", version: i, feedSequence: i));
        }

        factory.Run(db => db.OutputDestinations.Single(d => d.Id == setup.Destination).FeedSequence = 3);
        using var admin = factory.TenantAdminOf(setup.Tenant);

        var all = (await admin.GetFromJsonAsync<FeedPage>($"/api/v1/feeds/{setup.Destination}", ApiFactory.Json, Ct))!;
        all.Items.Select(e => e.Sequence).ShouldBe([1L, 2L, 3L]);
        all.Items[0].Payload!.Value.GetProperty("n").GetInt32().ShouldBe(1);
        all.Items.ShouldAllBe(e => !e.Acknowledged);
        all.Next.ShouldBe(3);
        all.HasMore.ShouldBeFalse();

        var paged = (await admin.GetFromJsonAsync<FeedPage>($"/api/v1/feeds/{setup.Destination}?after=0&limit=2", ApiFactory.Json, Ct))!;
        paged.Items.Select(e => e.Sequence).ShouldBe([1L, 2L]);
        paged.HasMore.ShouldBeTrue();
        paged.Next.ShouldBe(2);
        (await admin.GetFromJsonAsync<FeedPage>($"/api/v1/feeds/{setup.Destination}?after=2", ApiFactory.Json, Ct))!.Items.ShouldHaveSingleItem().Sequence.ShouldBe(3);

        var ack = await admin.PostAsJsonAsync($"/api/v1/feeds/{setup.Destination}:ack", new { upTo = 2 }, Ct);
        ack.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ack.Content.ReadFromJsonAsync<FeedAckResponse>(ApiFactory.Json, Ct))!.Acknowledged.ShouldBe(2);
        (await (await admin.PostAsJsonAsync($"/api/v1/feeds/{setup.Destination}:ack", new { upTo = 2 }, Ct)).Content.ReadFromJsonAsync<FeedAckResponse>(ApiFactory.Json, Ct))!.Acknowledged.ShouldBe(0, "acknowledging twice is not an error");

        var rest = (await admin.GetFromJsonAsync<FeedPage>($"/api/v1/feeds/{setup.Destination}", ApiFactory.Json, Ct))!;
        rest.Items.ShouldHaveSingleItem().Sequence.ShouldBe(3, "without a cursor the feed starts at the oldest entry not acknowledged");
        var replay = (await admin.GetFromJsonAsync<FeedPage>($"/api/v1/feeds/{setup.Destination}?after=0", ApiFactory.Json, Ct))!;
        replay.Items.Select(e => e.Acknowledged).ShouldBe([true, true, false], "a client that lost its cursor reads again from 0");

        factory.Query(db => db.OutboxDeliveries.Where(d => entries.Contains(d.Id)).OrderBy(d => d.FeedSequence).Select(d => d.Status).ToList())
            .ShouldBe([DeliveryStatus.Delivered, DeliveryStatus.Delivered, DeliveryStatus.Sending]);
        factory.Query(db => db.DocumentEvents.Count(e => e.DocumentId == setup.Document && e.EventType == DocumentEventTypes.DeliverySucceeded)).ShouldBe(2);

        (await (await admin.PostAsJsonAsync($"/api/v1/feeds/{setup.Destination}:ack", new { deliveryIds = new[] { entries[2] } }, Ct)).Content.ReadFromJsonAsync<FeedAckResponse>(ApiFactory.Json, Ct))!.Acknowledged.ShouldBe(1);
        var empty = (await admin.GetFromJsonAsync<FeedPage>($"/api/v1/feeds/{setup.Destination}", ApiFactory.Json, Ct))!;
        empty.Items.ShouldBeEmpty();
        empty.Next.ShouldBe(3, "the caller polls after the last sequence handed out");
        (await admin.PostAsJsonAsync($"/api/v1/feeds/{setup.Destination}:ack", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_feed_takes_deliveries_read_keys_and_a_restricted_key_reads_its_own_destination_only()
    {
        var setup = await SetupAsync("Feed keys", OutputTransports.Pull);
        await AddDeliveryAsync(setup, DeliveryStatus.Sending, """{"n":1}""", feedSequence: 1);
        var second = Guid.Empty;
        factory.Run(db =>
        {
            var destination = new OutputDestination { Id = Guid.CreateVersion7(), TenantId = setup.Tenant, Name = "feed-2", Transport = OutputTransports.Pull, CreatedBy = "test", CreatedAt = factory.Clock.GetUtcNow(), UpdatedAt = factory.Clock.GetUtcNow() };
            db.OutputDestinations.Add(destination);
            second = destination.Id;
        });
        using var admin = factory.TenantAdminOf(setup.Tenant);

        var created = await admin.PostAsJsonAsync("/api/v1/api-keys", new { name = "feed reader", scopes = new[] { "deliveries:read" }, destinationId = setup.Destination }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var key = (await created.Content.ReadFromJsonAsync<CreatedApiKey>(ApiFactory.Json, Ct))!;
        key.Scopes.ShouldBe(["deliveries:read"]);
        key.DestinationId.ShouldBe(setup.Destination);
        (await admin.GetFromJsonAsync<List<ApiKeySummary>>("/api/v1/api-keys", ApiFactory.Json, Ct))!.Single(k => k.Id == key.Id).DestinationId.ShouldBe(setup.Destination);

        using (var restricted = factory.ClientForApiKey(key.Key))
        {
            (await restricted.GetAsync($"/api/v1/feeds/{setup.Destination}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await restricted.PostAsJsonAsync($"/api/v1/feeds/{setup.Destination}:ack", new { upTo = 1 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await restricted.GetAsync($"/api/v1/feeds/{second}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await restricted.GetAsync("/api/v1/deliveries", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the scope reads feeds, not the delivery log");
        }

        var any = factory.InsertKey(setup.Tenant, "all feeds", ApiKeyScope.DeliveriesRead);
        using (var unrestricted = factory.ClientForApiKey(any))
        {
            (await unrestricted.GetAsync($"/api/v1/feeds/{second}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var read = factory.InsertKey(setup.Tenant, "reader", ApiKeyScope.Read);
        using (var reader = factory.ClientForApiKey(read))
        {
            (await reader.GetAsync($"/api/v1/feeds/{setup.Destination}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "read is not deliveries:read");
        }

        using var reviewer = factory.ClientFor(new TokenSpec("rev", [Roles.Reviewer], setup.Tenant.ToString()));
        (await reviewer.GetAsync($"/api/v1/feeds/{setup.Destination}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await admin.PostAsJsonAsync("/api/v1/api-keys", new { name = "x", scopes = new[] { "read" }, destinationId = setup.Destination }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "destinationId goes with deliveries:read");
        var http = await SetupAsync("Feed keys http", OutputTransports.Http);
        using var httpAdmin = factory.TenantAdminOf(http.Tenant);
        (await httpAdmin.PostAsJsonAsync("/api/v1/api-keys", new { name = "x", scopes = new[] { "deliveries:read" }, destinationId = http.Destination }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "only a pull destination has a feed");
        (await httpAdmin.GetAsync($"/api/v1/feeds/{http.Destination}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_binding_fires_on_approvals_and_rejections_only()
    {
        var setup = await SetupAsync("Binding events", OutputTransports.Http);
        using var admin = factory.TenantAdminOf(setup.Tenant);
        foreach (var events in new[] { new[] { "document.needs_review" }, ["document.updated"] })
        {
            var refused = await admin.PostAsJsonAsync("/api/v1/output-bindings", new { name = "b-" + events[0], events, mappingKey = "std.event", destinationId = setup.Destination }, Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, events[0]);
        }

        (await admin.PostAsJsonAsync("/api/v1/output-bindings", new { name = "rejections", events = new[] { "document.rejected" }, mappingKey = "std.event", destinationId = setup.Destination }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    private sealed record Setup(Guid Tenant, Guid Document, Guid Destination, Guid Binding, Guid MappingVersion, Guid Outbox);

    private async Task<Setup> SetupAsync(string name, string transport)
    {
        var tenant = factory.CreateTenant(name);
        factory.Run(db => db.Tenants.Single(t => t.Id == tenant).Settings.Features!.Outputs = true);
        var document = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, Status = DocumentStatus.Approved });
        var now = factory.Clock.GetUtcNow();
        var mappingId = OutputTemplates.PlatformMappingId("std.event");
        var setup = default(Setup);
        factory.Run(db =>
        {
            var version = db.OutputMappingVersions.Single(v => v.MappingId == mappingId && v.Version == 1).Id;
            var destination = new OutputDestination
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant,
                Name = transport == OutputTransports.Pull ? "feed" : "erp",
                Transport = transport,
                ConfigJson = transport == OutputTransports.Http ? """{"urlTemplate":"https://erp.example/api/ap/invoices"}""" : "{}",
                CreatedBy = "test",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var binding = new OutputBinding { Id = Guid.CreateVersion7(), TenantId = tenant, Name = "ap", MappingId = mappingId, DestinationId = destination.Id, CreatedBy = "test", CreatedAt = now, UpdatedAt = now };
            var settled = db.Documents.Single(d => d.Id == document.DocumentId);
            settled.ApprovedAt = now;
            var outbox = DocumentOutbox.Add(db, settled, now);
            outbox.FannedOutAt = now;
            db.OutputDestinations.Add(destination);
            db.OutputBindings.Add(binding);
            setup = new Setup(tenant, document.DocumentId, destination.Id, binding.Id, version, outbox.Id);
        });
        return setup!;
    }

    private async Task<Guid> AddDeliveryAsync(Setup setup, DeliveryStatus status, string payload, int attempts = 1, int version = 1, long? feedSequence = null)
    {
        var blobs = factory.Services.GetRequiredService<IContainerBlobStore>();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var stored = await blobs.PutAsync(BlobContainers.Exports, setup.Tenant, stream, "application/json", Ct);
        var id = Guid.CreateVersion7();
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        factory.Run(db => db.OutboxDeliveries.Add(new OutboxDelivery
        {
            Id = id,
            TenantId = setup.Tenant,
            OutboxMessageId = setup.Outbox,
            DocumentId = setup.Document,
            DocumentVersion = version,
            BindingId = setup.Binding,
            DestinationId = setup.Destination,
            MappingVersionId = setup.MappingVersion,
            IdempotencyKey = DeliveryFanOut.IdempotencyKey(setup.Document, version, setup.Binding) + (feedSequence is null ? string.Empty : $"-{feedSequence}"),
            Status = status,
            Attempts = attempts,
            FeedSequence = feedSequence,
            PayloadBlobKey = stored.Key,
            PayloadSha256 = stored.Sha256,
            ContentType = "application/json",
            CreatedAt = factory.Clock.GetUtcNow(),
            LastError = status == DeliveryStatus.DeadLettered ? "The receiver answered 503." : null,
        }));
        return id;
    }
}
