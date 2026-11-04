using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Api.Endpoints;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Flags;
using DocProc.Domain.Text;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Layouts;
using Microsoft.AspNetCore.Mvc;

namespace DocProc.Api.Tests;

/// <summary>
/// The review flows of P4 part C (docs/11 §12.6, §12.9, §12.10, §18): <c>:learn-layout</c> by a reviewer (a Candidate, its
/// siblings attached, the <c>MappingAuthor</c>s notified) and by a <c>MappingAuthor</c> (Active after its replay, warm-up
/// 5), the refusals, <c>:approve {learnLayout}</c> and the <c>NEW_LAYOUT</c> resolutions, <c>identifiers:propose</c>,
/// <c>:match-layout</c>, the replay and matching-documents endpoints, "new version of X" and its confirmation, bulk
/// reprocessing, notifications, the inbox filter, the warm-up setting applied to Active versions, and the switch.
/// </summary>
public sealed class LayoutLearningEndpointsTests : IClassFixture<ApiFactory>
{
    private const string Vat = "DE312456789";
    private static readonly JsonSerializerOptions TextLayerJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() }, IgnoreReadOnlyProperties = true };

    private readonly ApiFactory _factory;

    public LayoutLearningEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.Run(db =>
        {
            var features = db.Tenants.Single(t => t.Id == ApiFactory.TenantA).Settings.Features!;
            features.SupplierLayouts = true;
            features.SourceRouting = true;
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Admin(Guid? tenant = null) => _factory.ClientFor(new TokenSpec("tenant-admin", [Roles.TenantAdmin], (tenant ?? ApiFactory.TenantA).ToString()));

    private HttpClient Reviewer() => _factory.ClientFor(new TokenSpec("tenant-a-reviewer", [Roles.Reviewer], ApiFactory.TenantA.ToString()));

    private HttpClient Author() => _factory.ClientFor(new TokenSpec("tenant-a-author", [Roles.Reviewer, Roles.MappingAuthor], ApiFactory.TenantA.ToString()));

    private static string Layer(params (double Y, string Text)[] lines)
    {
        var words = new List<PositionedWord>();
        foreach (var (y, text) in lines)
        {
            var x = 60.0;
            foreach (var word in text.Split(' '))
            {
                words.Add(new PositionedWord(word, new BoundingBox(x, y, x + (word.Length * 6), y + 10)));
                x += (word.Length * 6) + 4;
            }
        }

        return JsonSerializer.Serialize(new TextLayer(TextLayerSource.PdfText, [new TextPage(1, 1000, 1000, words)]), TextLayerJson);
    }

    /// <summary>A web-shop page that prints the seeded invoice's number and customer, and the supplier's VAT id.</summary>
    private static string WebShop(string number = "RE-2026-0042") => Layer((40, "Nordwind Webshop USt-IdNr. DE 312 456 789"), (200, "Rechnung"), (240, $"Rechnungs-Nr. {number}"), (260, "Kunde Gasthaus zur Post GmbH"));

    private static string Erp() => Layer((40, "Nordwind ERP USt-IdNr. DE 312 456 789"), (200, "Gutschrift"), (240, "Belegnummer GS-1"));

    private static DocumentFlag NewLayout(Guid supplierId) =>
        FlagCodes.Create(FlagCodes.NewLayout, "new design", parameters: FlagParams.Of(("stepId", "classify.identification"), ("supplierId", supplierId.ToString())));

    private Guid Supplier(string name)
    {
        var id = Guid.Empty;
        _factory.Run(db =>
        {
            var supplier = new Supplier { Id = Guid.CreateVersion7(), TenantId = ApiFactory.TenantA, Name = name, NormalizedName = name.ToUpperInvariant(), VatId = Vat, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            db.Suppliers.Add(supplier);
            id = supplier.Id;
        });
        return id;
    }

    private async Task<Guid> SeedAsync(string supplierName, string layer, bool newLayout = true, DocumentStatus status = DocumentStatus.NeedsReview, string number = "RE-2026-0042")
    {
        var supplierId = Guid.Empty;
        _factory.Run(db => supplierId = db.Suppliers.Single(s => s.TenantId == ApiFactory.TenantA && s.Name == supplierName).Id);
        var seeded = await _factory.SeedDocumentAsync(new DocumentSeed
        {
            SupplierName = supplierName,
            TextLayerJson = layer,
            Status = status,
            Invoice = TestInvoices.Valid(number),
            Flags = newLayout ? [NewLayout(supplierId)] : [],
            ReviewTask = status == DocumentStatus.NeedsReview,
        });
        // The seeded document went through the stages up to Validate, so it can be reprocessed from any of them.
        _factory.Run(db => db.Ingestions.Single(i => i.Id == seeded.IngestionId).SourceSender = "rechnung@nordwind.example");
        _factory.Run(db => db.DocumentEvents.Add(new DocumentEvent { TenantId = ApiFactory.TenantA, DocumentId = seeded.DocumentId, EventType = DocProc.Application.Abstractions.DocumentEventTypes.StageCompleted, FromStatus = DocumentStatus.Extracted, ToStatus = DocumentStatus.Validated, Actor = "pipeline", At = DateTimeOffset.UtcNow }));
        return seeded.DocumentId;
    }

    private static async Task<LearnLayoutResponse> LearnAsync(HttpClient client, Guid document, LearnLayoutRequest request, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/documents/{document}:learn-layout", request, ApiFactory.Json, Ct);
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync(Ct));
        return expected == HttpStatusCode.OK ? (await response.Content.ReadFromJsonAsync<LearnLayoutResponse>(ApiFactory.Json, Ct))! : null!;
    }

    [Fact]
    public async Task A_reviewers_learned_layout_is_a_Candidate_its_siblings_wait_for_it_and_the_MappingAuthors_are_notified()
    {
        var name = $"Nordwind {Guid.NewGuid():N}";
        var supplier = Supplier(name);
        var document = await SeedAsync(name, WebShop());
        var sibling = await SeedAsync(name, WebShop("RE-2026-0043"), number: "RE-2026-0043");
        using var reviewer = Reviewer();

        var learned = await LearnAsync(reviewer, document, new LearnLayoutRequest(LayoutKey: "nordwind-invoice-webshop"));

        learned.Status.ShouldBe("Candidate");
        learned.NotActivated.ShouldNotBeNull();
        learned.RuleProposed.ShouldBeTrue();
        learned.Describe.ShouldContain("Rechnung");
        learned.Identification.GetRawText().ShouldContain("supplierValue");
        learned.Fields.Select(f => f.Path).ShouldContain("invoiceNumber", string.Join(" | ", learned.Skipped.Select(x => x.Path + ": " + x.Reason)) + " FIELDS " + string.Join(",", learned.Fields.Select(f => f.Path)));
        learned.Fields.Single(f => f.Path == "invoiceNumber").Label.ShouldBe("Rechnungs-Nr.");
        learned.Fields.Select(f => f.Path).ShouldContain("customer.name", string.Join(" | ", learned.Skipped.Select(x => x.Path + ": " + x.Reason)) + " FIELDS " + string.Join(",", learned.Fields.Select(f => f.Path)));
        learned.Siblings.ShouldBe([sibling]);
        learned.ProposedSourceRules.ShouldContain(p => p.Conditions.SequenceEqual(new[] { "sender" }));
        learned.Layout.Versions.Single().Origin.ShouldBe(nameof(SupplierLayoutOrigin.NewLayoutReview));
        var versionId = learned.Layout.Versions.Single().Id;
        _factory.Run(db =>
        {
            db.Documents.Single(d => d.Id == sibling).PendingLayoutVersionId.ShouldBe(versionId);
            db.DocumentFlags.Single(f => f.DocumentId == sibling && f.Code == FlagCodes.NewLayout).Params!["pendingLayoutVersionId"].ShouldBe(versionId.ToString());
            db.DocumentFlags.Single(f => f.DocumentId == document && f.Code == FlagCodes.NewLayout).ResolvedAt.ShouldBeNull("learning alone does not resolve the flag; approval does");
        });

        // The inbox lists the documents waiting for the Candidate.
        var waiting = await reviewer.GetFromJsonAsync<Page<DocumentSummary>>($"/api/v1/documents?pendingLayoutVersionId={versionId}", ApiFactory.Json, Ct);
        waiting!.Items.Select(d => d.Id).ShouldBe([sibling, document], ignoreOrder: true);

        // The MappingAuthors (tenant admins, holders of the grant) see the notification; a plain reviewer does not.
        (await reviewer.GetFromJsonAsync<List<NotificationDto>>("/api/v1/notifications", ApiFactory.Json, Ct))!.ShouldNotContain(n => n.EntityId == versionId);
        using var admin = Admin();
        var notification = (await admin.GetFromJsonAsync<List<NotificationDto>>("/api/v1/notifications", ApiFactory.Json, Ct))!.Single(n => n.EntityId == versionId);
        notification.Kind.ShouldBe(LayoutLearning.CandidateWaiting);
        notification.Count.ShouldBe(2);

        // Activation by a MappingAuthor runs the replay (the source document is its set) and reprocesses the waiting documents.
        var activated = await admin.PostAsJsonAsync($"/api/v1/supplier-layouts/{learned.Layout.Id}/versions/1:activate", new ActivateLayoutRequest(ReprocessWaiting: true), Ct);
        activated.StatusCode.ShouldBe(HttpStatusCode.OK, await activated.Content.ReadAsStringAsync(Ct));
        var outcome = (await activated.Content.ReadFromJsonAsync<ActivateLayoutResponse>(ApiFactory.Json, Ct))!;
        outcome.Waiting.ShouldBe([sibling, document], ignoreOrder: true);
        outcome.Reprocessed.ShouldBe([sibling, document], ignoreOrder: true);
        _factory.Queue.Documents.ShouldContain(m => m.DocumentId == sibling && m.Stage == PipelineStage.Classify);
        (await admin.GetFromJsonAsync<List<NotificationDto>>("/api/v1/notifications", ApiFactory.Json, Ct))!.ShouldNotContain(n => n.EntityId == versionId, "activation resolves it");
        _ = supplier;
    }

    [Fact]
    public async Task A_MappingAuthors_learned_layout_is_Active_with_the_default_warm_up_after_its_replay()
    {
        var name = $"Nordwind {Guid.NewGuid():N}";
        Supplier(name);
        var document = await SeedAsync(name, WebShop());
        using var author = Author();

        var learned = await LearnAsync(author, document, new LearnLayoutRequest());

        learned.Status.ShouldBe("Active", learned.NotActivated);
        learned.Replay.ShouldNotBeNull().Passed.ShouldBeTrue();
        learned.Replay.Documents.ShouldHaveSingleItem().DocumentId.ShouldBe(document);
        learned.Layout.LayoutKey.ShouldStartWith("nordwind-invoice-");
        learned.Layout.Versions.Single().ReviewWarmup.ShouldBe(TenantSettings.DefaultLayoutReviewWarmup);

        // The warm-up setting lowered to 0 follows to the Active versions it set.
        using var admin = Admin();
        var settings = (await admin.GetFromJsonAsync<TenantSettingsResponse>("/api/v1/settings", ApiFactory.Json, Ct))!.Settings;
        try
        {
            (await admin.PutAsJsonAsync("/api/v1/settings", settings with { LayoutReviewWarmup = 0 }, ApiFactory.Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            _factory.Run(db => db.SupplierLayoutVersions.Single(v => v.LayoutId == learned.Layout.Id).ReviewWarmup.ShouldBe(0));
        }
        finally
        {
            (await admin.PutAsJsonAsync("/api/v1/settings", settings with { LayoutReviewWarmup = TenantSettings.DefaultLayoutReviewWarmup }, ApiFactory.Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Learning_is_refused_for_a_rule_that_does_not_hold_a_duplicate_key_and_without_the_switch()
    {
        var name = $"Nordwind {Guid.NewGuid():N}";
        Supplier(name);
        var document = await SeedAsync(name, WebShop());
        using var reviewer = Reviewer();

        var notHolding = await reviewer.PostAsJsonAsync($"/api/v1/documents/{document}:learn-layout", new LearnLayoutRequest(Identification: JsonDocument.Parse("""{ "all": [ { "text": "Gutschrift" } ] }""").RootElement), ApiFactory.Json, Ct);
        notHolding.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await notHolding.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Extensions["code"]!.ToString().ShouldBe(LearnCodes.RuleDoesNotHold);

        var invalid = await reviewer.PostAsJsonAsync($"/api/v1/documents/{document}:learn-layout", new LearnLayoutRequest(Identification: JsonDocument.Parse("""{ "none": [ { "text": "x" } ] }""").RootElement), ApiFactory.Json, Ct);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await LearnAsync(reviewer, document, new LearnLayoutRequest(LayoutKey: "nordwind-web"));
        var again = await reviewer.PostAsJsonAsync($"/api/v1/documents/{document}:learn-layout", new LearnLayoutRequest(LayoutKey: "nordwind-web"), ApiFactory.Json, Ct);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using var b = Admin(ApiFactory.TenantB);
        (await b.PostAsJsonAsync($"/api/v1/documents/{document}:learn-layout", new LearnLayoutRequest(), ApiFactory.Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Approve_with_learnLayout_saves_the_layout_in_the_approval_and_without_it_resolves_NEW_LAYOUT_as_approvedWithoutLayout()
    {
        var name = $"Nordwind {Guid.NewGuid():N}";
        Supplier(name);
        var learnedDoc = await SeedAsync(name, WebShop());
        var plainDoc = await SeedAsync(name, WebShop("RE-2026-0099"), number: "RE-2026-0099");
        using var author = Author();

        var approved = await author.PostAsJsonAsync($"/api/v1/documents/{learnedDoc}:approve", new ApproveDocumentRequest("ok", new LearnLayoutRequest(LayoutKey: "nordwind-approved")), ApiFactory.Json, Ct);
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
        (await approved.Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!.Status.ShouldBe(DocumentStatus.Approved);
        _factory.Run(db =>
        {
            var flag = db.DocumentFlags.Single(f => f.DocumentId == learnedDoc && f.Code == FlagCodes.NewLayout);
            flag.ResolvedAt.ShouldNotBeNull();
            flag.Params!["resolution"].ShouldBe("learned");
            flag.Params["layoutKey"].ShouldBe("nordwind-approved");
            db.SupplierLayouts.Single(l => l.LayoutKey == "nordwind-approved").CreatedFromDocumentId.ShouldBe(learnedDoc);
            db.DocumentEvents.Any(e => e.DocumentId == learnedDoc && e.EventType == "document.layout_learned").ShouldBeTrue();
        });

        var plain = await author.PostAsJsonAsync($"/api/v1/documents/{plainDoc}:approve", new ApproveDocumentRequest("no layout"), ApiFactory.Json, Ct);
        plain.StatusCode.ShouldBe(HttpStatusCode.OK);
        _factory.Run(db => db.DocumentFlags.Single(f => f.DocumentId == plainDoc && f.Code == FlagCodes.NewLayout).Params!["resolution"].ShouldBe("approvedWithoutLayout"));

        // A refused learnLayout refuses the approval: nothing is approved.
        var other = await SeedAsync(name, WebShop("RE-2026-0100"), number: "RE-2026-0100");
        var refused = await author.PostAsJsonAsync($"/api/v1/documents/{other}:approve", new ApproveDocumentRequest(null, new LearnLayoutRequest(LayoutKey: "nordwind-approved")), ApiFactory.Json, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        _factory.Run(db => db.Documents.Single(d => d.Id == other).Status.ShouldBe(DocumentStatus.NeedsReview));
    }

    [Fact]
    public async Task Identifiers_are_proposed_from_the_selection_with_their_location()
    {
        var name = $"Nordwind {Guid.NewGuid():N}";
        Supplier(name);
        var document = await SeedAsync(name, WebShop());
        using var reviewer = Reviewer();

        async Task<ProposeIdentifierResponse> Propose(ProposeIdentifierRequest request)
        {
            var response = await reviewer.PostAsJsonAsync($"/api/v1/documents/{document}/identifiers:propose", request, ApiFactory.Json, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            return (await response.Content.ReadFromJsonAsync<ProposeIdentifierResponse>(ApiFactory.Json, Ct))!;
        }

        var title = await Propose(new ProposeIdentifierRequest(1, new SourceBox(55, 195, 120, 212)));
        title.Identifier.GetRawText().ShouldBe("""{"text":"Rechnung","page":1,"region":"title"}""");
        title.Holds.ShouldBeTrue();

        // "DE 312 456 789" sits after "Nordwind Webshop USt-IdNr." on the first line: words 3 to 6 of the page.
        var vat = await Propose(new ProposeIdentifierRequest(1, Words: [3, 4, 5, 6]));
        vat.Identifier.GetRawText().ShouldBe("""{"supplierValue":"vatId","page":1}""");

        var tenant = await Propose(new ProposeIdentifierRequest(1, Words: [3, 4, 5, 6], Scope: "tenant"));
        tenant.Identifier.GetProperty("text").GetString().ShouldBe("DE 312 456 789");
        tenant.Warnings.ShouldNotBeEmpty();

        (await reviewer.PostAsJsonAsync($"/api/v1/documents/{document}/identifiers:propose", new ProposeIdentifierRequest(1), ApiFactory.Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await reviewer.PostAsJsonAsync($"/api/v1/documents/{document}/identifiers:propose", new ProposeIdentifierRequest(1, new SourceBox(900, 900, 950, 950)), ApiFactory.Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Match_layout_sets_the_reviewers_type_records_the_basis_and_reruns_the_prefill_only_when_it_changes()
    {
        var name = $"Nordwind {Guid.NewGuid():N}";
        Supplier(name);
        var document = await SeedAsync(name, WebShop());
        var other = await SeedAsync(name, WebShop("RE-2026-0050"), newLayout: false, number: "RE-2026-0050");
        using var author = Author();
        var basis = await LearnAsync(author, await SeedAsync(name, WebShop("RE-2026-0051"), number: "RE-2026-0051"), new LearnLayoutRequest(LayoutKey: "nordwind-basis"));
        using var reviewer = Reviewer();

        var confirmed = await reviewer.PostAsJsonAsync($"/api/v1/documents/{document}:match-layout", new MatchLayoutRequest("Invoice"), ApiFactory.Json, Ct);
        confirmed.StatusCode.ShouldBe(HttpStatusCode.Accepted, await confirmed.Content.ReadAsStringAsync(Ct));
        (await confirmed.Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!.TypeSource.ShouldBe(TypeSource.Human);
        _factory.Queue.Documents.ShouldNotContain(m => m.DocumentId == document, "the proposed type confirmed keeps the pre-fill");

        var based = await reviewer.PostAsJsonAsync($"/api/v1/documents/{document}:match-layout", new MatchLayoutRequest("Invoice", BasedOnLayoutId: basis.Layout.Id, SaveAs: "newVersion", TargetLayoutId: basis.Layout.Id), ApiFactory.Json, Ct);
        based.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        _factory.Queue.Documents.ShouldContain(m => m.DocumentId == document && m.Stage == PipelineStage.Extract);
        _factory.Run(db =>
        {
            var flag = db.DocumentFlags.Single(f => f.DocumentId == document && f.Code == FlagCodes.NewLayout);
            flag.Params!["basedOnLayoutId"].ShouldBe(basis.Layout.Id.ToString());
            flag.Params["saveAs"].ShouldBe("newVersion");
            db.Corrections.Count(c => c.DocumentId == document && c.Op == "setType").ShouldBe(2);
        });

        (await reviewer.PostAsJsonAsync($"/api/v1/documents/{other}:match-layout", new MatchLayoutRequest("Invoice"), ApiFactory.Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "no NEW_LAYOUT");
        (await reviewer.PostAsJsonAsync($"/api/v1/documents/{other}:match-layout", new MatchLayoutRequest("Unknown"), ApiFactory.Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_new_version_that_no_longer_recognises_the_layouts_documents_needs_a_confirmation_and_the_replay_and_siblings_are_reported()
    {
        var name = $"Nordwind {Guid.NewGuid():N}";
        Supplier(name);
        using var author = Author();
        var first = await SeedAsync(name, WebShop());
        var learned = await LearnAsync(author, first, new LearnLayoutRequest(LayoutKey: "nordwind-web"));
        learned.Status.ShouldBe("Active", learned.NotActivated);
        _factory.Run(db =>
        {
            var document = db.Documents.Single(d => d.Id == first);
            document.Status = DocumentStatus.Approved;
            document.SupplierLayoutVersionId = learned.Layout.Versions.Single().Id;
        });

        // A redesign of the same layout: a new rule that the layout's approved document does not satisfy.
        var redesign = await SeedAsync(name, Layer((40, "Nordwind USt-IdNr. DE 312 456 789"), (200, "INVOICE"), (240, "Rechnungs-Nr. RE-2026-0042"), (260, "Kunde Gasthaus zur Post GmbH")));
        var rule = JsonDocument.Parse("""{ "all": [ { "text": "INVOICE", "page": 1, "region": "title" }, { "supplierValue": "vatId", "page": 1 } ] }""").RootElement;
        var refused = await author.PostAsJsonAsync($"/api/v1/documents/{redesign}:learn-layout", new LearnLayoutRequest(SaveAs: "newVersion", TargetLayoutId: learned.Layout.Id, Identification: rule), ApiFactory.Json, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = (await refused.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!;
        problem.Extensions["code"]!.ToString().ShouldBe(LearnCodes.NotRecognised);
        problem.Extensions["documentIds"]!.ToString()!.ShouldContain(first.ToString());

        var confirmed = await LearnAsync(author, redesign, new LearnLayoutRequest(SaveAs: "newVersion", TargetLayoutId: learned.Layout.Id, Identification: rule, ConfirmNotRecognised: true));
        confirmed.Version.ShouldBe(2);
        confirmed.NoLongerRecognised.ShouldBe([first]);

        // The replay and the matching documents of version 2.
        var replay = await author.PostAsJsonAsync($"/api/v1/supplier-layouts/{learned.Layout.Id}/versions/2:replay", new ReplayLayoutRequest(), Ct);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK, await replay.Content.ReadAsStringAsync(Ct));
        var report = (await replay.Content.ReadFromJsonAsync<ReplayReport>(ApiFactory.Json, Ct))!;
        report.Documents.Select(d => d.DocumentId).ShouldContain(redesign);
        report.Documents.Select(d => d.DocumentId).ShouldNotContain(first, "a document of another design is not in the replay set");

        var notMatching = await author.GetFromJsonAsync<List<MatchingDocumentDto>>($"/api/v1/supplier-layouts/{learned.Layout.Id}/versions/2/matching-documents?state=approved&holds=false", ApiFactory.Json, Ct);
        notMatching!.Select(d => d.DocumentId).ShouldBe([first]);
        var pending = await author.GetFromJsonAsync<List<MatchingDocumentDto>>($"/api/v1/supplier-layouts/{learned.Layout.Id}/versions/2/matching-documents?state=pending", ApiFactory.Json, Ct);
        pending!.Select(d => d.DocumentId).ShouldContain(redesign);
    }

    [Fact]
    public async Task Bulk_reprocess_queues_each_document_and_reports_the_ones_it_cannot()
    {
        var name = $"Nordwind {Guid.NewGuid():N}";
        Supplier(name);
        var a = await SeedAsync(name, WebShop());
        var settled = await SeedAsync(name, WebShop(), newLayout: false, status: DocumentStatus.Approved);
        using var reviewer = Reviewer();

        var response = await reviewer.PostAsJsonAsync("/api/v1/documents:bulk-reprocess", new BulkReprocessRequest([a, settled, Guid.NewGuid()]), ApiFactory.Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var items = (await response.Content.ReadFromJsonAsync<List<BulkReprocessItem>>(ApiFactory.Json, Ct))!;
        items.Single(i => i.DocumentId == a).Queued.ShouldBeTrue();
        items.Single(i => i.DocumentId == settled).Queued.ShouldBeFalse();
        items.Count(i => i.Problem == "not found").ShouldBe(1);
        _factory.Queue.Documents.ShouldContain(m => m.DocumentId == a && m.Stage == PipelineStage.Classify);
        (await reviewer.PostAsJsonAsync("/api/v1/documents:bulk-reprocess", new BulkReprocessRequest([]), ApiFactory.Json, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_notification_is_dismissed_by_a_member_who_may_see_it()
    {
        var name = $"Nordwind {Guid.NewGuid():N}";
        Supplier(name);
        using var reviewer = Reviewer();
        var learned = await LearnAsync(reviewer, await SeedAsync(name, WebShop()), new LearnLayoutRequest());
        var versionId = learned.Layout.Versions.Single().Id;
        using var admin = Admin();
        var notification = (await admin.GetFromJsonAsync<List<NotificationDto>>("/api/v1/notifications", ApiFactory.Json, Ct))!.Single(n => n.EntityId == versionId);

        (await reviewer.PostAsync($"/api/v1/notifications/{notification.Id}:dismiss", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "a reviewer without the grant does not see it");
        var dismissed = await admin.PostAsync($"/api/v1/notifications/{notification.Id}:dismiss", null, Ct);
        dismissed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.GetFromJsonAsync<List<NotificationDto>>("/api/v1/notifications?all=true", ApiFactory.Json, Ct))!.Single(n => n.Id == notification.Id).ResolvedAt.ShouldNotBeNull();
        _ = Erp();
    }
}
