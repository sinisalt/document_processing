using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Api.Endpoints;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Api.Tests;

/// <summary>
/// Acknowledgements from the receiving systems (docs/11 §13.8): a destination's callback settings and secret (two
/// active for a rotation), the anonymous callback authenticated by an HMAC of the timestamp and the body or a bearer
/// token, mapped by the destination's JSONata; a rejection raises <c>EXTERNAL_REJECTED</c> and opens an integration-issue
/// review task while the document stays settled, an acceptance resolves it, and a reviewer can resolve it with a note.
/// </summary>
public sealed class IntegrationCallbackEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Secret = "callback-secret-0123456789";

    private const string Mapping = """$.{ "externalId": ref, "status": state = "ERROR" ? "rejected" : (state = "BOOKED" ? "posted" : "accepted"), "message": text }""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(Guid Tenant, Guid Document, Guid Destination, string ExternalId);

    private async Task<Setup> SetupAsync(string name)
    {
        var tenant = factory.CreateTenant(name);
        factory.Run(db => db.Tenants.Single(t => t.Id == tenant).Settings.Features!.Outputs = true);
        var document = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, Status = DocProc.Domain.Documents.DocumentStatus.Approved });
        var now = factory.Clock.GetUtcNow();
        var destinationId = Guid.CreateVersion7();
        var externalId = "AP-" + destinationId.ToString("N")[..8];
        factory.Run(db =>
        {
            db.OutputDestinations.Add(new OutputDestination
            {
                Id = destinationId,
                TenantId = tenant,
                Name = "erp",
                Transport = OutputTransports.Http,
                ConfigJson = """{"urlTemplate":"https://erp.example/api/ap/invoices"}""",
                CreatedBy = "test",
                CreatedAt = now,
                UpdatedAt = now,
            });
            // What the delivery processor leaves for a destination that reports back: the receiver's id, Pending.
            db.DocumentExternalRefs.Add(new DocumentExternalRef { Id = Guid.CreateVersion7(), TenantId = tenant, DocumentId = document.DocumentId, DestinationId = destinationId, ExternalId = externalId, Status = ExternalRefStatus.Pending, UpdatedAt = now });
        });
        return new Setup(tenant, document.DocumentId, destinationId, externalId);
    }

    private static HttpRequestMessage Callback(Guid destination, string body, string? secret, DateTimeOffset at, string auth = "hmacSha256")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/integrations/callbacks/{destination}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (secret is not null && auth == "hmacSha256")
        {
            var timestamp = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            request.Headers.Add(IntegrationEndpoints.TimestampHeader, timestamp);
            request.Headers.Add(IntegrationEndpoints.SignatureHeader, "sha256=" + Convert.ToHexStringLower(IntegrationEndpoints.Sign(secret, timestamp, Encoding.UTF8.GetBytes(body))));
        }
        else if (secret is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
        }

        return request;
    }

    private List<DocumentFlagRecord> Flags(Guid document) => factory.Query(db => db.DocumentFlags.Where(f => f.DocumentId == document && f.Code == FlagCodes.ExternalRejected).ToList());

    [Fact]
    public async Task A_rejection_by_callback_raises_EXTERNAL_REJECTED_and_an_integration_issue_and_an_acceptance_resolves_it()
    {
        var setup = await SetupAsync("Callbacks");
        using var admin = factory.TenantAdminOf(setup.Tenant);
        using var anonymous = factory.CreateClient();

        // No callback configured: nothing answers at the address.
        (await anonymous.SendAsync(Callback(setup.Destination, """{"ref":"x","state":"OK"}""", Secret, factory.Clock.GetUtcNow()), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var configured = await admin.PutAsJsonAsync($"/api/v1/destinations/{setup.Destination}/acknowledgements", new { callback = new { auth = "hmacSha256", mapping = Mapping } }, Ct);
        configured.StatusCode.ShouldBe(HttpStatusCode.OK, await configured.Content.ReadAsStringAsync(Ct));
        (await anonymous.SendAsync(Callback(setup.Destination, """{"ref":"x","state":"OK"}""", Secret, factory.Clock.GetUtcNow()), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "no secret yet");
        var secret = await admin.PutAsJsonAsync($"/api/v1/destinations/{setup.Destination}/callback-secret", new { secret = Secret }, Ct);
        (await secret.Content.ReadFromJsonAsync<DestinationResponse>(ApiFactory.Json, Ct))!.HasCallbackSecret.ShouldBeTrue();

        var rejection = $$"""{"ref":"{{setup.ExternalId}}","state":"ERROR","text":"Vendor blocked"}""";
        (await anonymous.SendAsync(Callback(setup.Destination, rejection, null, factory.Clock.GetUtcNow()), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "unsigned");
        (await anonymous.SendAsync(Callback(setup.Destination, rejection, "another-secret-0123456789", factory.Clock.GetUtcNow()), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "signed with another secret");
        (await anonymous.SendAsync(Callback(setup.Destination, rejection, Secret, factory.Clock.GetUtcNow().AddMinutes(-10)), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "a replay outside the window");
        Flags(setup.Document).ShouldBeEmpty();

        var accepted = await anonymous.SendAsync(Callback(setup.Destination, rejection, Secret, factory.Clock.GetUtcNow()), Ct);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync(Ct));
        (await accepted.Content.ReadFromJsonAsync<CallbackResponse>(ApiFactory.Json, Ct))!.Applied.ShouldBe(1);
        var flag = Flags(setup.Document).ShouldHaveSingleItem();
        flag.ResolvedAt.ShouldBeNull();
        flag.Params!["message"].ShouldBe("Vendor blocked");
        flag.Params["destination"].ShouldBe("erp");
        var issue = factory.Query(db => db.ReviewTasks.Single(t => t.DocumentId == setup.Document));
        issue.Kind.ShouldBe(ReviewTaskKind.IntegrationIssue);
        issue.Status.ShouldBe(ReviewTaskStatus.Open);
        factory.Query(db => db.Documents.Single(d => d.Id == setup.Document).Status).ShouldBe(DocProc.Domain.Documents.DocumentStatus.Approved, "the document stays settled");
        factory.Query(db => db.DocumentExternalRefs.Single(r => r.DocumentId == setup.Document).Status).ShouldBe(ExternalRefStatus.Rejected);
        factory.Query(db => db.DocumentEvents.Count(e => e.DocumentId == setup.Document && e.EventType == "document.externally_rejected")).ShouldBe(1);

        // The same rejection again raises nothing new; the receiver then books the corrected document.
        await anonymous.SendAsync(Callback(setup.Destination, rejection, Secret, factory.Clock.GetUtcNow()), Ct);
        Flags(setup.Document).Count.ShouldBe(1);
        var booked = await anonymous.SendAsync(Callback(setup.Destination, $$"""[{"ref":"{{setup.ExternalId}}","state":"BOOKED","text":null},{"ref":"unknown","state":"OK"}]""", Secret, factory.Clock.GetUtcNow()), Ct);
        var answer = (await booked.Content.ReadFromJsonAsync<CallbackResponse>(ApiFactory.Json, Ct))!;
        answer.Applied.ShouldBe(1);
        answer.Unknown.ShouldBe(1, "an id the destination was never sent is not a document");
        Flags(setup.Document).Single().ResolvedAt.ShouldNotBeNull();
        factory.Query(db => db.ReviewTasks.Single(t => t.DocumentId == setup.Document).Status).ShouldBe(ReviewTaskStatus.Done);
        factory.Query(db => db.DocumentExternalRefs.Single(r => r.DocumentId == setup.Document).Status).ShouldBe(ExternalRefStatus.Posted);
    }

    [Fact]
    public async Task A_replayed_or_late_signed_callback_changes_nothing()
    {
        var setup = await SetupAsync("Callback replay");
        using var admin = factory.TenantAdminOf(setup.Tenant);
        using var anonymous = factory.CreateClient();
        (await admin.PutAsJsonAsync($"/api/v1/destinations/{setup.Destination}/acknowledgements", new { callback = new { auth = "hmacSha256", mapping = Mapping } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await admin.PutAsJsonAsync($"/api/v1/destinations/{setup.Destination}/callback-secret", new { secret = Secret }, Ct);

        var now = factory.Clock.GetUtcNow();
        var rejection = $$"""{"ref":"{{setup.ExternalId}}","state":"ERROR","text":"Vendor blocked"}""";
        var acceptance = $$"""{"ref":"{{setup.ExternalId}}","state":"OK","text":null}""";
        (await anonymous.SendAsync(Callback(setup.Destination, rejection, Secret, now.AddMinutes(-2)), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var accepted = await anonymous.SendAsync(Callback(setup.Destination, acceptance, Secret, now.AddMinutes(-1)), Ct);
        (await accepted.Content.ReadFromJsonAsync<CallbackResponse>(ApiFactory.Json, Ct))!.Applied.ShouldBe(1);
        Flags(setup.Document).Single().ResolvedAt.ShouldNotBeNull();
        factory.Query(db => db.ReviewTasks.Single(t => t.DocumentId == setup.Document).Status).ShouldBe(ReviewTaskStatus.Done);

        // The captured rejection sent again inside the window: authenticated, but older than the acceptance.
        var replay = await anonymous.SendAsync(Callback(setup.Destination, rejection, Secret, now.AddMinutes(-2)), Ct);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        var answer = (await replay.Content.ReadFromJsonAsync<CallbackResponse>(ApiFactory.Json, Ct))!;
        answer.Applied.ShouldBe(0);
        answer.Replayed.ShouldBe(1);
        Flags(setup.Document).ShouldHaveSingleItem().ResolvedAt.ShouldNotBeNull("no new EXTERNAL_REJECTED");
        factory.Query(db => db.ReviewTasks.Count(t => t.DocumentId == setup.Document)).ShouldBe(1, "no new integration issue");
        factory.Query(db => db.DocumentExternalRefs.Single(r => r.DocumentId == setup.Document).Status).ShouldBe(ExternalRefStatus.Accepted);

        // The acceptance itself again is a replay too; a later, different callback still applies.
        (await (await anonymous.SendAsync(Callback(setup.Destination, acceptance, Secret, now.AddMinutes(-1)), Ct)).Content.ReadFromJsonAsync<CallbackResponse>(ApiFactory.Json, Ct))!.Replayed.ShouldBe(1);
        factory.Query(db => db.DocumentEvents.Count(e => e.DocumentId == setup.Document && e.EventType == "document.externally_accepted")).ShouldBe(1);
        var later = await anonymous.SendAsync(Callback(setup.Destination, rejection, Secret, now), Ct);
        (await later.Content.ReadFromJsonAsync<CallbackResponse>(ApiFactory.Json, Ct))!.Applied.ShouldBe(1);
        factory.Query(db => db.DocumentExternalRefs.Single(r => r.DocumentId == setup.Document).Status).ShouldBe(ExternalRefStatus.Rejected);
    }

    [Fact]
    public async Task A_destination_with_a_status_poll_does_not_move_to_another_host()
    {
        var setup = await SetupAsync("Callback poll host");
        factory.Run(db => db.Tenants.Single(t => t.Id == setup.Tenant).Settings.Features!.Outputs = true);
        using var admin = factory.TenantAdminOf(setup.Tenant);
        (await admin.PutAsJsonAsync($"/api/v1/destinations/{setup.Destination}/acknowledgements", new { statusPoll = new { urlTemplate = "https://erp.example/api/ap/status/{externalId}" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var moved = await admin.PatchAsJsonAsync($"/api/v1/destinations/{setup.Destination}", new { config = new { urlTemplate = "https://erp.elsewhere.example/api/ap/invoices" } }, Ct);
        moved.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await moved.Content.ReadAsStringAsync(Ct));
        (await moved.Content.ReadAsStringAsync(Ct)).ShouldContain("status poll");
        var samePlace = await admin.PatchAsJsonAsync($"/api/v1/destinations/{setup.Destination}", new { config = new { urlTemplate = "https://erp.example/api/v2/ap/invoices" } }, Ct);
        samePlace.StatusCode.ShouldBe(HttpStatusCode.OK, await samePlace.Content.ReadAsStringAsync(Ct));

        // With the poll turned off, the destination moves.
        (await admin.PutAsJsonAsync($"/api/v1/destinations/{setup.Destination}/acknowledgements", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.PatchAsJsonAsync($"/api/v1/destinations/{setup.Destination}", new { config = new { urlTemplate = "https://erp.elsewhere.example/api/ap/invoices" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_callback_secret_rotates_with_the_old_one_accepted_until_the_next_rotation_and_bearer_works_too()
    {
        var setup = await SetupAsync("Callback rotation");
        using var admin = factory.TenantAdminOf(setup.Tenant);
        using var anonymous = factory.CreateClient();
        (await admin.PutAsJsonAsync($"/api/v1/destinations/{setup.Destination}/acknowledgements", new { callback = new { auth = "bearer" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.PutAsJsonAsync($"/api/v1/destinations/{setup.Destination}/callback-secret", new { secret = "short" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await admin.PutAsJsonAsync($"/api/v1/destinations/{setup.Destination}/callback-secret", new { secret = "first-secret-0123456789" }, Ct);
        var rotated = await admin.PutAsJsonAsync($"/api/v1/destinations/{setup.Destination}/callback-secret", new { secret = "second-secret-0123456789" }, Ct);
        (await rotated.Content.ReadFromJsonAsync<DestinationResponse>(ApiFactory.Json, Ct))!.HasPreviousCallbackSecret.ShouldBeTrue();

        // Without a mapping the body is the acknowledgement itself.
        var body = $$"""{"externalId":"{{setup.ExternalId}}","status":"accepted"}""";
        (await anonymous.SendAsync(Callback(setup.Destination, body, "first-secret-0123456789", factory.Clock.GetUtcNow(), auth: "bearer"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "the replaced secret is still accepted");
        (await anonymous.SendAsync(Callback(setup.Destination, body, "second-secret-0123456789", factory.Clock.GetUtcNow(), auth: "bearer"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await admin.PutAsJsonAsync($"/api/v1/destinations/{setup.Destination}/callback-secret", new { secret = "third-secret-0123456789" }, Ct);
        (await anonymous.SendAsync(Callback(setup.Destination, body, "first-secret-0123456789", factory.Clock.GetUtcNow(), auth: "bearer"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "two rotations later");
        (await anonymous.SendAsync(Callback(setup.Destination, """{"status":"accepted"}""", "third-secret-0123456789", factory.Clock.GetUtcNow(), auth: "bearer"), Ct)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "no id");
        factory.Query(db => db.DocumentExternalRefs.Single(r => r.DocumentId == setup.Document).Status).ShouldBe(ExternalRefStatus.Accepted);
    }

    [Fact]
    public async Task Acknowledgement_settings_are_checked_and_a_reviewer_resolves_an_integration_issue_with_a_note()
    {
        var setup = await SetupAsync("Callback settings");
        using var admin = factory.TenantAdminOf(setup.Tenant);
        using var reviewer = factory.ClientFor(new TokenSpec("rev", [Roles.Reviewer], setup.Tenant.ToString()));
        foreach (var bad in new object[]
        {
            new { callback = new { auth = "basic" } },
            new { statusPoll = new { urlTemplate = "https://{externalId}.erp.example/status" } },
            new { statusPoll = new { urlTemplate = "ftp://erp.example/{externalId}" } },
            new { statusPoll = new { urlTemplate = "https://erp.example/status/{externalId}", intervalMinutes = 0 } },
            new { statusPoll = new { urlTemplate = "https://elsewhere.example/status/{externalId}" } },
            new { callback = new { mapping = "{ status: " } },
            new { webhook = true },
        })
        {
            (await admin.PutAsJsonAsync($"/api/v1/destinations/{setup.Destination}/acknowledgements", bad, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, JsonSerializer.Serialize(bad));
        }

        (await reviewer.PutAsJsonAsync($"/api/v1/destinations/{setup.Destination}/acknowledgements", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var polled = await admin.PutAsJsonAsync($"/api/v1/destinations/{setup.Destination}/acknowledgements", new { statusPoll = new { urlTemplate = "https://erp.example/api/ap/status/{externalId}", intervalMinutes = 5 } }, Ct);
        polled.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await polled.Content.ReadFromJsonAsync<DestinationResponse>(ApiFactory.Json, Ct))!.Acknowledgements!.Value.GetProperty("statusPoll").GetProperty("intervalMinutes").GetInt32().ShouldBe(5);

        (await reviewer.PostAsJsonAsync($"/api/v1/documents/{setup.Document}:resolve-integration-issue", new { note = "Booked by hand in the ERP." }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "nothing to resolve");
        var now = factory.Clock.GetUtcNow();
        factory.Run(db =>
        {
            var destination = db.OutputDestinations.Single(d => d.Id == setup.Destination);
            ExternalAcknowledgementsApply(db, destination, setup, now);
        });
        (await reviewer.PostAsJsonAsync($"/api/v1/documents/{setup.Document}:resolve-integration-issue", new { note = " " }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var resolved = await reviewer.PostAsJsonAsync($"/api/v1/documents/{setup.Document}:resolve-integration-issue", new { note = "Booked by hand in the ERP." }, Ct);
        resolved.StatusCode.ShouldBe(HttpStatusCode.OK, await resolved.Content.ReadAsStringAsync(Ct));
        Flags(setup.Document).Single().ResolvedBy.ShouldBe("rev");
        factory.Query(db => db.ReviewTasks.Single(t => t.DocumentId == setup.Document).Status).ShouldBe(ReviewTaskStatus.Done);
        factory.Query(db => db.DocumentEvents.Single(e => e.DocumentId == setup.Document && e.EventType == "document.integration_issue_resolved").DetailsJson)!.ShouldContain("Booked by hand");
        var tasks = (await reviewer.GetFromJsonAsync<Page<ReviewTaskDto>>("/api/v1/review-tasks?status=Done", ApiFactory.Json, Ct))!;
        tasks.Items.ShouldContain(t => t.DocumentId == setup.Document && t.Kind == ReviewTaskKind.IntegrationIssue);
    }

    private static void ExternalAcknowledgementsApply(DocProcDbContext db, OutputDestination destination, Setup setup, DateTimeOffset now) =>
        DocProc.Infrastructure.Outputs.Acknowledgements.ExternalAcknowledgements.ApplyAsync(
            db,
            destination,
            new DocProc.Infrastructure.Outputs.Acknowledgements.AcknowledgementReport(null, setup.ExternalId, ExternalRefStatus.Rejected, "Duplicate in ERP"),
            DocProc.Infrastructure.Outputs.Acknowledgements.AcknowledgementSources.StatusPoll,
            now,
            CancellationToken.None).GetAwaiter().GetResult();
}
