using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Outputs.Delivery;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Runner;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Pipeline.Tests.Delivery;

/// <summary>
/// Delivery in the worker's composition (docs/11 §13.5, §13.6): the publish stage fans an approval out to the
/// tenant's bindings, renders and stores the snapshot, and the <c>ProcessDelivery</c> handler sends it to a fake
/// receiver on loopback; retries with backoff, dead letters, the delivery sweeper after a lost message or a crash
/// mid-send, per-destination ordering, the re-approval modes, conditions, exclusive groups, the file drop and the pull
/// feed, and the timeline the document keeps of it all. A tenant without the outputs switch publishes as before.
/// </summary>
public sealed class DeliveryPipelineTests
{
    private const string XmlMime = "application/xml";

    private const string ApExpression = """{"docNo": roles.documentNumber, "event": meta.event, "revision": meta.version, "gross": roles.total.amount}""";

    private static readonly Guid TenantA = PipelineTestHost.TenantA;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_auto_approval_is_rendered_snapshotted_and_delivered_with_its_timeline()
    {
        using var receiver = new FakeReceiver();
        await using var host = await StartAsync(receiver);
        var binding = await BindAsync(host, receiver, ApExpression);

        var document = await AutoApproveAsync(host);

        var delivery = (await DeliveriesAsync(host, document.Id)).ShouldHaveSingleItem();
        delivery.Status.ShouldBe(DeliveryStatus.Delivered);
        delivery.Attempts.ShouldBe(1);
        delivery.Event.ShouldBe(OutputEvents.Approved);
        delivery.DocumentVersion.ShouldBe(document.Version);
        delivery.IdempotencyKey.ShouldBe($"{document.Id:D}:{document.Version}:{binding.BindingId:D}");
        delivery.PayloadSha256.ShouldNotBeNull();
        delivery.DeliveredAt.ShouldNotBeNull();

        var request = receiver.Requests.ShouldHaveSingleItem();
        request.Header("Idempotency-Key").ShouldBe(delivery.IdempotencyKey);
        request.Header("X-DocProc-Revision").ShouldBe(document.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var body = JsonNode.Parse(request.Text)!.AsObject();
        body["event"]!.GetValue<string>().ShouldBe(OutputEvents.Approved);
        body["docNo"]!.GetValue<string>().ShouldNotBeNullOrEmpty();

        // The snapshot in the exports store is what was sent.
        var blobs = host.Services.GetRequiredService<IContainerBlobStore>();
        var stored = await blobs.FindAsync(BlobContainers.Exports, delivery.PayloadBlobKey!, Ct);
        stored.ShouldNotBeNull().Sha256.ShouldBe(delivery.PayloadSha256);

        var events = await host.EventsAsync(document.Id);
        var publish = events.Last(e => e.EventType == DocumentEventTypes.StageCompleted);
        var entry = Details(publish).GetProperty("deliveries").EnumerateArray().ShouldHaveSingleItem();
        entry.GetProperty("outcome").GetString().ShouldBe("queued");
        entry.GetProperty("deliveryId").GetGuid().ShouldBe(delivery.Id);
        var succeeded = events.Single(e => e.EventType == DocumentEventTypes.DeliverySucceeded);
        Details(succeeded).GetProperty("statusCode").GetInt32().ShouldBe(201);
        Details(succeeded).GetProperty("destination").GetString().ShouldBe("erp");
        (await AttemptsAsync(host, delivery.Id)).ShouldHaveSingleItem().ErrorClass.ShouldBe("delivered");
    }

    [Fact]
    public async Task A_tenant_without_the_outputs_switch_publishes_exactly_as_before()
    {
        using var receiver = new FakeReceiver();
        await using var host = await StartAsync(receiver);
        await BindAsync(host, receiver, ApExpression, enableOutputs: false);

        var document = await AutoApproveAsync(host);

        (await DeliveriesAsync(host, document.Id)).ShouldBeEmpty();
        receiver.Requests.ShouldBeEmpty();
        var publish = (await host.EventsAsync(document.Id))[^1];
        publish.EventType.ShouldBe(DocumentEventTypes.StageCompleted);
        Details(publish).EnumerateObject().Select(p => p.Name).ShouldBe(["stage", "eventType", "outboxMessageId"]);
    }

    [Fact]
    public async Task A_503_is_retried_after_its_retry_after_and_then_succeeds()
    {
        using var receiver = new FakeReceiver();
        receiver.Script(new ReceiverAnswer(503, """{"error":"unavailable"}""", new Dictionary<string, string> { ["Retry-After"] = "1" }));
        await using var host = await StartAsync(receiver);
        await BindAsync(host, receiver, ApExpression);

        var document = await AutoApproveAsync(host);

        var delivery = (await DeliveriesAsync(host, document.Id)).ShouldHaveSingleItem();
        delivery.Status.ShouldBe(DeliveryStatus.Delivered);
        delivery.Attempts.ShouldBe(2);
        receiver.Requests.Count.ShouldBe(2);
        receiver.Requests.Select(r => r.Header("Idempotency-Key")).Distinct().ShouldHaveSingleItem("a retry sends the same key");
        var attempts = await AttemptsAsync(host, delivery.Id);
        attempts.Select(a => a.StatusCode).ShouldBe([503, 201]);
        attempts[0].ErrorClass.ShouldBe("retryable:status");
        var events = await host.EventsAsync(document.Id);
        var failed = events.Single(e => e.EventType == DocumentEventTypes.DeliveryFailed);
        Details(failed).GetProperty("nextAttemptAt").GetDateTimeOffset().ShouldBe(failed.At + TimeSpan.FromSeconds(1));
        events.Count(e => e.EventType == DocumentEventTypes.DeliverySucceeded).ShouldBe(1);
        (await host.DocumentAsync(document.Id)).Flags.ShouldNotContain(f => f.Code == FlagCodes.DeliveryFailed);
    }

    [Fact]
    public async Task A_permanent_refusal_is_dead_lettered_at_once_with_DELIVERY_FAILED()
    {
        using var receiver = new FakeReceiver { Default = new ReceiverAnswer(422, """{"error":"vendor blocked"}""") };
        await using var host = await StartAsync(receiver);
        await BindAsync(host, receiver, ApExpression);

        var document = await AutoApproveAsync(host);

        var delivery = (await DeliveriesAsync(host, document.Id)).ShouldHaveSingleItem();
        delivery.Status.ShouldBe(DeliveryStatus.DeadLettered);
        delivery.Attempts.ShouldBe(1);
        delivery.LastStatusCode.ShouldBe(422);
        delivery.NextAttemptAt.ShouldBeNull();
        var flag = (await host.DocumentAsync(document.Id)).Flags.Where(f => f.Code == FlagCodes.DeliveryFailed).ShouldHaveSingleItem();
        flag.ResolvedAt.ShouldBeNull();
        flag.Params!["destination"].ShouldBe("erp");
        var failed = (await host.EventsAsync(document.Id)).Single(e => e.EventType == DocumentEventTypes.DeliveryFailed);
        Details(failed).GetProperty("deadLettered").GetBoolean().ShouldBeTrue();
        (await AttemptsAsync(host, delivery.Id)).ShouldHaveSingleItem().ResponseExcerpt.ShouldBe("""{"error":"vendor blocked"}""");
    }

    [Fact]
    public async Task Retries_run_out_after_the_attempt_limit_and_the_delivery_is_dead_lettered()
    {
        using var receiver = new FakeReceiver { Default = new ReceiverAnswer(503, "{}", new Dictionary<string, string> { ["Retry-After"] = "1" }) };
        await using var host = await StartAsync(receiver, settings => settings["Delivery:MaxAttempts"] = "3");
        await BindAsync(host, receiver, ApExpression);

        var document = await AutoApproveAsync(host);

        var delivery = (await DeliveriesAsync(host, document.Id)).ShouldHaveSingleItem();
        delivery.Status.ShouldBe(DeliveryStatus.DeadLettered);
        delivery.Attempts.ShouldBe(3);
        receiver.Requests.Count.ShouldBe(3);
        var failures = (await host.EventsAsync(document.Id)).Where(e => e.EventType == DocumentEventTypes.DeliveryFailed).ToList();
        failures.Count.ShouldBe(3);
        Details(failures[^1]).GetProperty("exhausted").GetString().ShouldBe("attempts");
        (await host.DocumentAsync(document.Id)).Flags.ShouldContain(f => f.Code == FlagCodes.DeliveryFailed);
    }

    [Fact]
    public async Task A_delivery_whose_message_was_lost_after_the_commit_is_sent_by_the_delivery_sweeper_once()
    {
        using var receiver = new FakeReceiver();
        var dropping = new DroppingQueue();
        await using var host = await StartAsync(receiver, services: services => services.AddSingleton<IPipelineQueue>(sp => dropping.Attach(sp.GetRequiredService<Queue.InProcessPipelineQueue>())));
        await BindAsync(host, receiver, ApExpression);

        // The process "dies" between the publish stage's commit and the delivery's nudge: the message never arrives.
        dropping.DropDeliveries = true;
        var document = await AutoApproveAsync(host);
        var pending = (await DeliveriesAsync(host, document.Id)).ShouldHaveSingleItem();
        pending.Status.ShouldBe(DeliveryStatus.Pending);
        dropping.Dropped.ShouldBe(1);
        receiver.Requests.ShouldBeEmpty();
        dropping.DropDeliveries = false;

        var sweeper = host.Services.GetRequiredService<DeliverySweeperHostedService>();
        (await sweeper.SweepOnceAsync(Ct)).ShouldBe(0, "within the grace the nudge may still come");
        host.Clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        (await sweeper.SweepOnceAsync(Ct)).ShouldBe(1);
        (await sweeper.SweepOnceAsync(Ct)).ShouldBe(0, "the sweep claimed it");
        await host.RunUntilIdleAsync();

        var delivered = (await DeliveriesAsync(host, document.Id)).ShouldHaveSingleItem();
        delivered.Status.ShouldBe(DeliveryStatus.Delivered);
        receiver.Requests.ShouldHaveSingleItem();

        // A late copy of the lost message is for an attempt the row has made: dropped.
        await host.PipelineQueue.EnqueueAsync(new ProcessDelivery(TenantA, delivered.Id, delivered.DestinationId, delivered.DocumentId, 1), TimeSpan.Zero, Ct);
        await host.RunUntilIdleAsync();
        receiver.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task An_attempt_cut_off_mid_send_is_picked_up_again_once_its_lease_ran_out()
    {
        using var receiver = new FakeReceiver();
        await using var host = await StartAsync(receiver);
        var binding = await BindAsync(host, receiver, ApExpression);
        var document = await AutoApproveAsync(host);
        var first = (await DeliveriesAsync(host, document.Id)).Single();

        // A delivery claimed by a process that died: Sending, one attempt counted, its lease in the future.
        var stuck = await AddDeliveryAsync(host, document.Id, binding, first.MappingVersionId, first.PayloadBlobKey!, version: document.Version + 1, status: DeliveryStatus.Sending, attempts: 1, nextAttemptAt: host.Clock.GetUtcNow() + TimeSpan.FromMinutes(5));
        var sweeper = host.Services.GetRequiredService<DeliverySweeperHostedService>();
        (await sweeper.SweepOnceAsync(Ct)).ShouldBe(0, "the lease still holds");

        host.Clock.Advance(TimeSpan.FromMinutes(6));
        (await sweeper.SweepOnceAsync(Ct)).ShouldBe(1);
        await host.RunUntilIdleAsync();

        var row = (await DeliveriesAsync(host, document.Id)).Single(d => d.Id == stuck);
        row.Status.ShouldBe(DeliveryStatus.Delivered);
        row.Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task An_older_revision_still_waiting_is_skipped_when_a_newer_one_exists()
    {
        using var receiver = new FakeReceiver();
        await using var host = await StartAsync(receiver);
        var binding = await BindAsync(host, receiver, ApExpression);
        var document = await AutoApproveAsync(host);
        var sent = (await DeliveriesAsync(host, document.Id)).Single();
        receiver.Requests.ShouldHaveSingleItem();

        var older = await AddDeliveryAsync(host, document.Id, binding, sent.MappingVersionId, sent.PayloadBlobKey!, version: document.Version - 1, status: DeliveryStatus.Failed, attempts: 1, nextAttemptAt: host.Clock.GetUtcNow());
        await host.PipelineQueue.EnqueueAsync(new ProcessDelivery(TenantA, older, binding.DestinationId, document.Id, 2), TimeSpan.Zero, Ct);
        await host.RunUntilIdleAsync();

        var skipped = (await DeliveriesAsync(host, document.Id)).Single(d => d.Id == older);
        skipped.Status.ShouldBe(DeliveryStatus.Skipped);
        skipped.LastError.ShouldNotBeNull().ShouldContain("Superseded");
        receiver.Requests.ShouldHaveSingleItem("the older revision is never sent after the newer one");
        (await host.EventsAsync(document.Id)).ShouldContain(e => e.EventType == DocumentEventTypes.DeliverySkipped);
    }

    [Theory]
    [InlineData(OnReapproval.Resend)]
    [InlineData(OnReapproval.Skip)]
    [InlineData(OnReapproval.Update)]
    public async Task A_reapproval_after_a_reprocess_follows_the_bindings_OnReapproval(OnReapproval mode)
    {
        using var receiver = new FakeReceiver { Respond = r => new ReceiverAnswer(r.Method == "PUT" ? 200 : 201) };
        await using var host = await StartAsync(receiver);
        var config = new JsonObject
        {
            ["urlTemplate"] = receiver.BaseUrl + "/api/article-prices",
            ["updateUrlTemplate"] = receiver.BaseUrl + "/api/article-prices/{{output.docNo}}",
        };
        var binding = await BindAsync(host, receiver, ApExpression, config: config, configure: b => b.OnReapproval = mode, updateExpression: """{"docNo": roles.documentNumber, "event": meta.event, "update": true}""");
        var document = await AutoApproveAsync(host);
        var first = (await DeliveriesAsync(host, document.Id)).ShouldHaveSingleItem();
        first.Status.ShouldBe(DeliveryStatus.Delivered);

        var reapproved = await ReapproveAsync(host, document.Id);

        var second = (await DeliveriesAsync(host, document.Id)).Single(d => d.Id != first.Id);
        second.DocumentVersion.ShouldBe(reapproved);
        second.IdempotencyKey.ShouldBe($"{document.Id:D}:{reapproved}:{binding.BindingId:D}", "a new revision, a new key");
        switch (mode)
        {
            case OnReapproval.Resend:
                second.Status.ShouldBe(DeliveryStatus.Delivered);
                second.Event.ShouldBe(OutputEvents.Approved);
                receiver.Requests.Select(r => r.Method).ShouldBe(["POST", "POST"]);
                break;
            case OnReapproval.Skip:
                second.Status.ShouldBe(DeliveryStatus.Skipped);
                second.LastError.ShouldNotBeNull().ShouldContain("Skip");
                receiver.Requests.Count.ShouldBe(1);
                break;
            case OnReapproval.Update:
                second.Status.ShouldBe(DeliveryStatus.Delivered);
                second.Event.ShouldBe(OutputEvents.Updated);
                second.MappingVersionId.ShouldNotBe(first.MappingVersionId, "rendered with the update mapping");
                var update = receiver.Requests[^1];
                update.Method.ShouldBe("PUT");
                update.PathAndQuery.ShouldStartWith("/api/article-prices/");
                var body = JsonNode.Parse(update.Text)!;
                body["event"]!.GetValue<string>().ShouldBe(OutputEvents.Updated);
                body["update"]!.GetValue<bool>().ShouldBeTrue();
                break;
        }
    }

    [Fact]
    public async Task A_false_condition_lists_the_binding_as_not_applicable_and_an_exclusive_group_sends_only_the_most_specific()
    {
        using var receiver = new FakeReceiver();
        await using var host = await StartAsync(receiver);
        var never = await BindAsync(host, receiver, ApExpression, name: "never", configure: b => b.Condition = "roles.total.amount < 0");
        var tenantWide = await BindAsync(host, receiver, """{"who":"tenant"}""", name: "tenant-wide", configure: b => { b.ExclusiveGroup = "ap"; b.Priority = 100; });
        var typed = await BindAsync(host, receiver, """{"who":"typed"}""", name: "typed", configure: b => { b.ExclusiveGroup = "ap"; b.DocumentTypeKey = "Invoice"; });
        var otherType = await BindAsync(host, receiver, """{"who":"price-list"}""", name: "price-lists", configure: b => b.DocumentTypeKey = "price-list");

        var document = await AutoApproveAsync(host);

        var deliveries = await DeliveriesAsync(host, document.Id);
        deliveries.Single(d => d.BindingId == never.BindingId).Status.ShouldBe(DeliveryStatus.Skipped);
        deliveries.Single(d => d.BindingId == never.BindingId).LastError.ShouldNotBeNull().ShouldContain("notApplicable");
        deliveries.Single(d => d.BindingId == typed.BindingId).Status.ShouldBe(DeliveryStatus.Delivered);
        deliveries.ShouldNotContain(d => d.BindingId == tenantWide.BindingId, "the type-scoped binding is more specific, whatever the priority");
        deliveries.ShouldNotContain(d => d.BindingId == otherType.BindingId, "out of scope");
        receiver.Requests.ShouldHaveSingleItem().Text.ShouldContain("typed");

        var publish = (await host.EventsAsync(document.Id)).Last(e => e.EventType == DocumentEventTypes.StageCompleted);
        var entries = Details(publish).GetProperty("deliveries").EnumerateArray().ToList();
        entries.ShouldContain(e => e.GetProperty("binding").GetString() == "tenant-wide" && e.GetProperty("outcome").GetString() == "notApplicable");
        entries.ShouldContain(e => e.GetProperty("binding").GetString() == "never" && e.GetProperty("outcome").GetString() == "notApplicable");
    }

    [Fact]
    public async Task A_render_that_fails_for_good_dead_letters_the_delivery_at_the_fan_out()
    {
        using var receiver = new FakeReceiver();
        await using var host = await StartAsync(receiver);
        await BindAsync(host, receiver, """{"vendor": $required(supplier.attributes.erpVendorId, "Supplier has no ERP vendor id")}""");

        var document = await AutoApproveAsync(host);

        var delivery = (await DeliveriesAsync(host, document.Id)).ShouldHaveSingleItem();
        delivery.Status.ShouldBe(DeliveryStatus.DeadLettered);
        delivery.Attempts.ShouldBe(0, "nothing was sent");
        delivery.LastError.ShouldNotBeNull().ShouldContain("Supplier has no ERP vendor id");
        delivery.PayloadBlobKey.ShouldBeNull();
        receiver.Requests.ShouldBeEmpty();
        (await host.DocumentAsync(document.Id)).Flags.ShouldContain(f => f.Code == FlagCodes.DeliveryFailed);
        (await AttemptsAsync(host, delivery.Id)).ShouldHaveSingleItem().ErrorClass.ShouldBe("permanent:render");
        (await host.DocumentAsync(document.Id)).Status.ShouldBe(DocumentStatus.AutoApproved, "the document stays settled");
    }

    [Fact]
    public async Task A_file_drop_destination_gets_the_file_and_a_pull_destination_a_feed_entry()
    {
        using var receiver = new FakeReceiver();
        var drop = Path.Combine(Path.GetTempPath(), "docproc-pipeline-drop", Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await StartAsync(receiver, settings => settings["Delivery:DropRoot"] = drop);
            await BindAsync(host, receiver, ApExpression, name: "drop", transport: OutputTransports.Blob, config: new JsonObject { ["path"] = "ap", ["fileNameTemplate"] = "{{meta.documentId}}-{{meta.version}}.json" });
            var pull = await BindAsync(host, receiver, ApExpression, name: "feed", transport: OutputTransports.Pull, config: new JsonObject());

            var document = await AutoApproveAsync(host);

            var deliveries = await DeliveriesAsync(host, document.Id);
            deliveries.Single(d => d.DestinationId != pull.DestinationId).Status.ShouldBe(DeliveryStatus.Delivered);
            var file = Path.Combine(drop, TenantA.ToString("D"), "ap", $"{document.Id:D}-{document.Version}.json");
            JsonNode.Parse(File.ReadAllText(file))!["event"]!.GetValue<string>().ShouldBe(OutputEvents.Approved);

            var entry = deliveries.Single(d => d.DestinationId == pull.DestinationId);
            entry.Status.ShouldBe(DeliveryStatus.Sending, "it waits in the feed for the client's acknowledgement");
            entry.FeedSequence.ShouldBe(1);
            entry.NextAttemptAt.ShouldBeNull("the sweeper leaves feed entries alone");
            (await host.WithDbAsync(db => db.OutputDestinations.Where(d => d.Id == pull.DestinationId).Select(d => d.FeedSequence).SingleAsync())).ShouldBe(1);
            receiver.Requests.ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(drop))
            {
                Directory.Delete(drop, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Deliveries_and_their_timeline_stay_in_their_tenant()
    {
        using var receiver = new FakeReceiver();
        await using var host = await StartAsync(receiver);
        await BindAsync(host, receiver, ApExpression);
        var document = await AutoApproveAsync(host);
        var delivery = (await DeliveriesAsync(host, document.Id)).Single();

        using var other = host.CreateTenantScope(DevSeed.TenantB);
        var db = other.ServiceProvider.GetRequiredService<DocProcDbContext>();
        (await db.OutboxDeliveries.AnyAsync(Ct)).ShouldBeFalse();
        (await db.DeliveryAttempts.AnyAsync(Ct)).ShouldBeFalse();

        // A tenant B message naming tenant A's delivery finds nothing in tenant B's scope.
        var run = await other.ServiceProvider.GetRequiredService<DeliveryProcessor>().ProcessAsync(new ProcessDelivery(DevSeed.TenantB, delivery.Id, delivery.DestinationId, document.Id, 2), Ct);
        run.Result.ShouldStartWith("dropped");
        receiver.Requests.ShouldHaveSingleItem();
    }

    private static async Task<PipelineTestHost> StartAsync(FakeReceiver receiver, Action<Dictionary<string, string?>>? configure = null, Action<IServiceCollection>? services = null) =>
        await PipelineTestHost.StartAsync(
            settings =>
            {
                settings["Delivery:SweepInterval"] = "00:00:00";
                settings["Pipeline:PublishSweepInterval"] = "00:00:00";
                configure?.Invoke(settings);
            },
            collection =>
            {
                collection.AddSingleton(new DeliveryEndpointPolicy([receiver.Host]));
                services?.Invoke(collection);
            });

    /// <summary>An auto-approved e-invoice of tenant A, after the whole pipeline (publish and deliveries) has run.</summary>
    private static async Task<Document> AutoApproveAsync(PipelineTestHost host)
    {
        var ingestion = await host.IngestAsync(TenantA, [new IngestFile($"{Guid.NewGuid():N}.xml", XmlMime, PipelineTestHost.FixtureVariant(Guid.NewGuid().ToString("N")))]);
        await host.RunUntilIdleAsync();
        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.Status.ShouldBe(DocumentStatus.AutoApproved);
        return document;
    }

    /// <summary>What a reprocess and a second approval leave: a new document version, approved again, its outbox row, and the publish stage run.</summary>
    private static async Task<int> ReapproveAsync(PipelineTestHost host, Guid documentId)
    {
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        var version = await host.WithDbAsync(async db =>
        {
            var document = await db.Documents.SingleAsync(d => d.Id == documentId);
            var now = host.Clock.GetUtcNow();
            document.Status = DocumentStatus.Approved;
            document.ApprovedAt = now;
            document.UpdatedAt = now;
            DocumentOutbox.Add(db, document, now);
            await db.SaveChangesAsync();
            return document.Version;
        });
        await host.PipelineQueue.EnqueueAsync(new ProcessDocument(TenantA, documentId, PipelineStage.Publish), Ct);
        await host.RunUntilIdleAsync();
        return version;
    }

    private sealed record Bound(Guid BindingId, Guid DestinationId, Guid MappingId);

    private static async Task<Bound> BindAsync(
        PipelineTestHost host,
        FakeReceiver receiver,
        string expression,
        string name = "ap",
        string transport = OutputTransports.Http,
        JsonObject? config = null,
        Action<OutputBinding>? configure = null,
        string? updateExpression = null,
        bool enableOutputs = true)
    {
        return await host.WithDbAsync(async db =>
        {
            var now = host.Clock.GetUtcNow();
            var tenant = await db.Tenants.SingleAsync(t => t.Id == TenantA);
            tenant.Settings.Features!.Outputs = enableOutputs;
            var mapping = Mapping(name, expression, now);
            db.OutputMappings.Add(mapping);
            Guid? update = null;
            if (updateExpression is not null)
            {
                var updateMapping = Mapping(name + "-update", updateExpression, now);
                db.OutputMappings.Add(updateMapping);
                update = updateMapping.Id;
            }

            var destination = new OutputDestination
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantA,
                Name = name == "ap" ? "erp" : name,
                Transport = transport,
                ConfigJson = (config ?? new JsonObject { ["urlTemplate"] = receiver.BaseUrl + "/api/ap/invoices" }).ToJsonString(),
                CreatedAt = now,
                CreatedBy = "test",
                UpdatedAt = now,
            };
            db.OutputDestinations.Add(destination);
            var binding = new OutputBinding
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantA,
                Name = name,
                MappingId = mapping.Id,
                DestinationId = destination.Id,
                UpdateMappingId = update,
                CreatedAt = now,
                CreatedBy = "test",
                UpdatedAt = now,
            };
            configure?.Invoke(binding);
            db.OutputBindings.Add(binding);
            await db.SaveChangesAsync();
            return new Bound(binding.Id, destination.Id, mapping.Id);
        });
    }

    private static OutputMapping Mapping(string key, string expression, DateTimeOffset now)
    {
        var mapping = new OutputMapping
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantA,
            Key = key,
            Name = key,
            Origin = OutputMappingOrigin.Custom,
            PublishedVersion = 1,
            CreatedAt = now,
            CreatedBy = "test",
            UpdatedAt = now,
        };
        mapping.Versions.Add(new OutputMappingVersion
        {
            Id = Guid.CreateVersion7(),
            MappingId = mapping.Id,
            TenantId = TenantA,
            Version = 1,
            Engine = "jsonata",
            Expression = expression,
            State = OutputMappingVersionState.Published,
            CreatedAt = now,
            CreatedBy = "test",
            UpdatedAt = now,
            PublishedAt = now,
            PublishedBy = "test",
        });
        return mapping;
    }

    private static Task<Guid> AddDeliveryAsync(PipelineTestHost host, Guid documentId, Bound binding, Guid mappingVersionId, string payloadKey, int version, DeliveryStatus status, int attempts, DateTimeOffset nextAttemptAt) =>
        host.WithDbAsync(async db =>
        {
            var outbox = await db.OutboxMessages.Where(m => m.DocumentId == documentId).Select(m => m.Id).FirstAsync();
            var blob = await db.OutboxDeliveries.Where(d => d.PayloadBlobKey == payloadKey).Select(d => new { d.PayloadSha256, d.ContentType }).FirstAsync();
            var row = new OutboxDelivery
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantA,
                OutboxMessageId = outbox,
                DocumentId = documentId,
                DocumentVersion = version,
                BindingId = binding.BindingId,
                DestinationId = binding.DestinationId,
                MappingVersionId = mappingVersionId,
                IdempotencyKey = DeliveryFanOut.IdempotencyKey(documentId, version, binding.BindingId),
                Status = status,
                Attempts = attempts,
                NextAttemptAt = nextAttemptAt,
                PayloadBlobKey = payloadKey,
                PayloadSha256 = blob.PayloadSha256,
                ContentType = blob.ContentType,
                CreatedAt = host.Clock.GetUtcNow(),
            };
            db.OutboxDeliveries.Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        });

    private static Task<List<OutboxDelivery>> DeliveriesAsync(PipelineTestHost host, Guid documentId) =>
        host.WithDbAsync(async db => (await db.OutboxDeliveries.AsNoTracking().Where(d => d.DocumentId == documentId).ToListAsync()).OrderBy(d => d.CreatedAt).ThenBy(d => d.DocumentVersion).ToList());

    private static Task<List<DeliveryAttempt>> AttemptsAsync(PipelineTestHost host, Guid deliveryId) =>
        host.WithDbAsync(async db => (await db.DeliveryAttempts.AsNoTracking().Where(a => a.DeliveryId == deliveryId).ToListAsync()).OrderBy(a => a.Id).ToList());

    private static JsonElement Details(DocumentEvent e) => JsonDocument.Parse(e.DetailsJson!).RootElement;

    /// <summary>The host's queue, but a delivery message is dropped while <see cref="DropDeliveries"/> is set: the process ending between the commit and the nudge.</summary>
    private sealed class DroppingQueue : IPipelineQueue
    {
        private Queue.InProcessPipelineQueue? _inner;

        public bool DropDeliveries { get; set; }

        public int Dropped { get; private set; }

        private Queue.InProcessPipelineQueue Inner => _inner ?? throw new InvalidOperationException("Not attached.");

        public DroppingQueue Attach(Queue.InProcessPipelineQueue inner)
        {
            _inner = inner;
            return this;
        }

        public Task EnqueueAsync(ProcessIngestion message, CancellationToken cancellationToken) => Inner.EnqueueAsync(message, cancellationToken);

        public Task EnqueueAsync(ProcessDocument message, CancellationToken cancellationToken) => Inner.EnqueueAsync(message, cancellationToken);

        public Task EnqueueAsync(ProcessDocument message, TimeSpan delay, CancellationToken cancellationToken) => Inner.EnqueueAsync(message, delay, cancellationToken);

        public Task EnqueueAsync(ProcessDelivery message, TimeSpan delay, CancellationToken cancellationToken)
        {
            if (DropDeliveries)
            {
                Dropped++;
                return Task.CompletedTask;
            }

            return Inner.EnqueueAsync(message, delay, cancellationToken);
        }
    }
}
