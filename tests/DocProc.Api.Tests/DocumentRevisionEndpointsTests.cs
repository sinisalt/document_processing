using System.Net;
using System.Net.Http.Json;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Api.Endpoints;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Api.Tests;

/// <summary>
/// <c>POST /documents/{id}:resolve-revision</c> (docs/11 §11): a document with an open <c>REVISION_OF</c> is resolved
/// as replacing the earlier one (which gets <c>SupersededByDocumentId</c>, shown as <c>supersededBy</c> in the list),
/// as an additional document, or as a duplicate (rejected and published as a rejection).
/// </summary>
public sealed class DocumentRevisionEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Guid Tenant, Guid Earlier, Guid Later)> SeedPairAsync(string name)
    {
        var tenant = factory.CreateTenant(name);
        var earlier = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, Status = DocumentStatus.Approved });
        var later = await factory.SeedDocumentAsync(new DocumentSeed
        {
            TenantId = tenant,
            ReviewTask = true,
            Flags = [FlagCodes.Create(FlagCodes.RevisionOf, "A list with the same validity, scope and number exists.", null, FlagParams.Of(("documentId", earlier.DocumentId.ToString()), ("status", "Approved"))) with { Severity = FlagSeverity.Warning }],
        });
        return (tenant, earlier.DocumentId, later.DocumentId);
    }

    private HttpClient Reviewer(Guid tenant) => factory.ClientFor(new TokenSpec("rev-" + tenant.ToString("N")[..6], [Roles.Reviewer], tenant.ToString()));

    [Fact]
    public async Task Supersedes_marks_the_earlier_document_and_leaves_this_one_in_review_for_its_approval()
    {
        var (tenant, earlier, later) = await SeedPairAsync("Revision supersedes");
        using var reviewer = Reviewer(tenant);

        (await reviewer.PostAsJsonAsync($"/api/v1/documents/{later}:resolve-revision", new { resolution = "replace" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await reviewer.PostAsJsonAsync($"/api/v1/documents/{earlier}:resolve-revision", new { resolution = "supersedes" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "the earlier document is settled");

        var response = await reviewer.PostAsJsonAsync($"/api/v1/documents/{later}:resolve-revision", new { resolution = "supersedes", note = "Corrected list of the morning." }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var detail = (await response.Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!;
        detail.Status.ShouldBe(DocumentStatus.NeedsReview, "the reviewer still approves it");
        detail.RevisionOfDocumentId.ShouldBe(earlier);
        detail.RevisionResolution.ShouldBe(RevisionResolution.Supersedes);
        factory.Query(db => db.Documents.Single(d => d.Id == earlier).SupersededByDocumentId).ShouldBe(later);
        factory.Query(db => db.DocumentFlags.Single(f => f.DocumentId == later && f.Code == FlagCodes.RevisionOf).ResolvedBy).ShouldNotBeNull();
        factory.Query(db => db.DocumentEvents.Count(e => (e.DocumentId == later || e.DocumentId == earlier) && e.EventType == "document.revision_resolved")).ShouldBe(2);

        var page = (await reviewer.GetFromJsonAsync<Page<DocumentSummary>>("/api/v1/documents", ApiFactory.Json, Ct))!;
        page.Items.Single(d => d.Id == earlier).SupersededBy.ShouldBe(later);
        page.Items.Single(d => d.Id == later).SupersededBy.ShouldBeNull();
        (await reviewer.PostAsJsonAsync($"/api/v1/documents/{later}:resolve-revision", new { resolution = "supersedes" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "nothing left to resolve");
    }

    [Fact]
    public async Task Additional_lets_both_stand_and_duplicate_rejects_this_document()
    {
        var (tenant, earlier, later) = await SeedPairAsync("Revision additional");
        using var reviewer = Reviewer(tenant);
        var additional = (await (await reviewer.PostAsJsonAsync($"/api/v1/documents/{later}:resolve-revision", new { resolution = "additional" }, Ct)).Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!;
        additional.RevisionResolution.ShouldBe(RevisionResolution.Additional);
        factory.Query(db => db.Documents.Single(d => d.Id == earlier).SupersededByDocumentId).ShouldBeNull();

        var (otherTenant, _, duplicate) = await SeedPairAsync("Revision duplicate");
        using var otherReviewer = Reviewer(otherTenant);
        var queued = factory.Queue.Documents.Count;
        var rejected = (await (await otherReviewer.PostAsJsonAsync($"/api/v1/documents/{duplicate}:resolve-revision", new { resolution = "duplicate" }, Ct)).Content.ReadFromJsonAsync<DocumentDetail>(ApiFactory.Json, Ct))!;
        rejected.Status.ShouldBe(DocumentStatus.Rejected);
        factory.Query(db => db.ReviewTasks.Single(t => t.DocumentId == duplicate).Status).ShouldBe(ReviewTaskStatus.Done);
        factory.Query(db => db.OutboxMessages.Count(m => m.DocumentId == duplicate && m.EventType == "document.rejected")).ShouldBe(1, "the rejection is published like any other");
        factory.Queue.Documents.Skip(queued).ShouldContain(m => m.DocumentId == duplicate && m.Stage == DocProc.Application.Pipeline.PipelineStage.Publish);

        // Another tenant cannot touch the pair.
        (await Reviewer(factory.CreateTenant("Revision stranger")).PostAsJsonAsync($"/api/v1/documents/{later}:resolve-revision", new { resolution = "duplicate" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
