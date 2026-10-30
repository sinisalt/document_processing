using System.Net;
using System.Net.Http.Json;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Api.Endpoints;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Api.Tests;

public sealed class ReviewTaskEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly string TenantA = ApiFactory.TenantA.ToString();
    private static readonly string TenantB = ApiFactory.TenantB.ToString();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri Tasks(string query = "") => new("/api/v1/review-tasks" + query, UriKind.Relative);

    private static Uri Task(Guid id, string action) => new($"/api/v1/review-tasks/{id}:{action}", UriKind.Relative);

    [Fact]
    public async Task Queue_lists_open_tasks_oldest_first_with_the_document_summary_and_filters()
    {
        var tenant = Guid.NewGuid();
        factory.Query(db =>
        {
            db.Tenants.Add(new Tenant { Id = tenant, Name = "Queue", Slug = "queue-" + tenant.ToString("N")[..8], LegalName = "Queue GmbH", DefaultCurrency = "EUR", CountryCode = "DE", TimeZone = "Europe/Berlin", CreatedAt = factory.Clock.GetUtcNow(), UpdatedAt = factory.Clock.GetUtcNow() });
            return db.SaveChanges();
        });
        var t0 = factory.Clock.GetUtcNow();
        var first = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, ReviewTask = true, CreatedAt = t0, Flags = [FlagCodes.Create(FlagCodes.TotalsMismatch, "off", "totalGross")] });
        var second = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, ReviewTask = true, CreatedAt = t0.AddMinutes(1), AssignedTo = "rev-1" });
        var third = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, ReviewTask = true, CreatedAt = t0.AddMinutes(2) });
        _ = await factory.SeedDocumentAsync(new DocumentSeed { TenantId = tenant, ReviewTask = false, CreatedAt = t0.AddMinutes(3) });

        using var reviewer = factory.ClientFor(new TokenSpec("rev-1", [Roles.Reviewer], tenant.ToString()));

        var queue = (await reviewer.GetFromJsonAsync<Page<ReviewTaskDto>>(Tasks(), ApiFactory.Json, Ct))!;
        queue.Items.Select(t => t.Id).ShouldBe([first.ReviewTaskId!.Value, second.ReviewTaskId!.Value, third.ReviewTaskId!.Value]);
        queue.NextCursor.ShouldBeNull();
        var head = queue.Items[0];
        head.DocumentId.ShouldBe(first.DocumentId);
        head.Status.ShouldBe(ReviewTaskStatus.Open);
        head.Reason.ShouldBe(FlagCodes.TotalsMismatch);
        head.Document!.SupplierName.ShouldBe("METRO Deutschland GmbH");
        head.Document.BlockingFlagCount.ShouldBe(1);
        queue.Items[1].AssignedTo.ShouldBe("rev-1");
        queue.Items[1].Status.ShouldBe(ReviewTaskStatus.InProgress);

        (await reviewer.GetFromJsonAsync<Page<ReviewTaskDto>>(Tasks("?assignedTo=me"), ApiFactory.Json, Ct))!.Items.Select(t => t.Id).ShouldBe([second.ReviewTaskId.Value]);
        (await reviewer.GetFromJsonAsync<Page<ReviewTaskDto>>(Tasks("?status=InProgress"), ApiFactory.Json, Ct))!.Items.Select(t => t.Id).ShouldBe([second.ReviewTaskId.Value]);
        (await reviewer.GetFromJsonAsync<Page<ReviewTaskDto>>(Tasks("?status=Done"), ApiFactory.Json, Ct))!.Items.ShouldBeEmpty();
        (await reviewer.GetAsync(Tasks("?status=Whatever"), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var pageOne = (await reviewer.GetFromJsonAsync<Page<ReviewTaskDto>>(Tasks("?limit=2"), ApiFactory.Json, Ct))!;
        pageOne.Items.Count.ShouldBe(2);
        pageOne.NextCursor.ShouldNotBeNull();
        var pageTwo = (await reviewer.GetFromJsonAsync<Page<ReviewTaskDto>>(Tasks($"?limit=2&cursor={pageOne.NextCursor}"), ApiFactory.Json, Ct))!;
        pageTwo.Items.Select(t => t.Id).ShouldBe([third.ReviewTaskId.Value]);
        pageTwo.NextCursor.ShouldBeNull();

        using var viewer = factory.ClientFor(new TokenSpec("viewer", [Roles.Viewer], tenant.ToString()));
        (await viewer.GetAsync(Tasks(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var otherTenant = factory.ClientFor(new TokenSpec("rev-b", [Roles.Reviewer], TenantB));
        (await otherTenant.GetFromJsonAsync<Page<ReviewTaskDto>>(Tasks(), ApiFactory.Json, Ct))!.Items.ShouldNotContain(t => t.DocumentId == first.DocumentId);

        using var operatorClient = factory.ClientFor(new TokenSpec("ops", [Roles.Operator]));
        (await operatorClient.GetFromJsonAsync<Page<ReviewTaskDto>>(Tasks("?limit=200"), ApiFactory.Json, Ct))!.Items.ShouldContain(t => t.DocumentId == first.DocumentId, "the global queue spans tenants");
    }

    [Fact]
    public async Task Claim_is_first_come_first_served_and_release_returns_the_task_to_the_queue()
    {
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { ReviewTask = true });
        var taskId = seeded.ReviewTaskId!.Value;
        using var anna = factory.ClientFor(new TokenSpec("anna", [Roles.Reviewer], TenantA));
        using var ben = factory.ClientFor(new TokenSpec("ben", [Roles.Reviewer], TenantA));

        var claimed = await anna.PostAsync(Task(taskId, "claim"), null, Ct);
        claimed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var dto = (await claimed.Content.ReadFromJsonAsync<ReviewTaskDto>(ApiFactory.Json, Ct))!;
        dto.AssignedTo.ShouldBe("anna");
        dto.Status.ShouldBe(ReviewTaskStatus.InProgress);
        dto.Document!.Id.ShouldBe(seeded.DocumentId);

        (await anna.PostAsync(Task(taskId, "claim"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "re-claiming your own task is idempotent");

        var contested = await ben.PostAsync(Task(taskId, "claim"), null, Ct);
        contested.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await contested.Content.ReadAsStringAsync(Ct)).ShouldContain("anna");
        (await ben.PostAsync(Task(taskId, "release"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "only the assignee or an admin releases");

        var released = await anna.PostAsync(Task(taskId, "release"), null, Ct);
        released.StatusCode.ShouldBe(HttpStatusCode.OK);
        var open = (await released.Content.ReadFromJsonAsync<ReviewTaskDto>(ApiFactory.Json, Ct))!;
        open.AssignedTo.ShouldBeNull();
        open.Status.ShouldBe(ReviewTaskStatus.Open);

        (await ben.PostAsync(Task(taskId, "claim"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var admin = factory.ClientFor(new TokenSpec("admin-a", [Roles.TenantAdmin], TenantA));
        (await admin.PostAsync(Task(taskId, "release"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "a tenant admin may release anyone's task");
        factory.Query(db => db.ReviewTasks.AsNoTracking().Single(t => t.Id == taskId).AssignedTo).ShouldBeNull();

        // Approving the document closes the task; a closed task can no longer be claimed.
        (await anna.PostAsync(new Uri($"/api/v1/documents/{seeded.DocumentId}:approve", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await anna.PostAsync(Task(taskId, "claim"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await anna.PostAsync(Task(Guid.NewGuid(), "claim"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var tenantB = factory.ClientFor(new TokenSpec("rev-b", [Roles.Reviewer], TenantB));
        (await tenantB.PostAsync(Task(taskId, "claim"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Release_only_undoes_the_assignment_the_caller_saw()
    {
        var seeded = await factory.SeedDocumentAsync(new DocumentSeed { ReviewTask = true, AssignedTo = "holder" });
        var taskId = seeded.ReviewTaskId!.Value;
        using var holder = factory.ClientFor(new TokenSpec("holder", [Roles.Reviewer], TenantA));
        using var admin = factory.ClientFor(new TokenSpec("admin-a", [Roles.TenantAdmin], TenantA));

        (await holder.PostAsync(Task(taskId, "release"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await holder.PostAsync(Task(taskId, "release"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "releasing an open task is idempotent");

        // A task that was closed in the meantime (the document got decided) is refused and left alone, assignment included.
        (await holder.PostAsync(Task(taskId, "claim"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Run(db =>
        {
            var task = db.ReviewTasks.Single(t => t.Id == taskId);
            task.Status = ReviewTaskStatus.Done;
            task.CompletedAt = factory.Clock.GetUtcNow();
        });
        var closed = await admin.PostAsync(Task(taskId, "release"), null, Ct);
        closed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        factory.Query(db => db.ReviewTasks.AsNoTracking().Single(t => t.Id == taskId).AssignedTo).ShouldBe("holder", "a closed task is left alone");
    }
}
