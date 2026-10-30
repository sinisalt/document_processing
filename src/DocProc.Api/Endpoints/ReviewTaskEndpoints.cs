using System.Security.Claims;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Api.Endpoints;

/// <summary>
/// The review queue (docs/05 "/review-tasks: queue, claim, release"): tenant reviewers see their tenant's tasks,
/// operators in platform scope see every tenant's. Claiming is first come, first served; a task someone else holds
/// answers 409 so two reviewers never work the same document.
/// </summary>
public static class ReviewTaskEndpoints
{
    private static readonly ReviewTaskStatus[] QueueStatuses = [ReviewTaskStatus.Open, ReviewTaskStatus.InProgress];

    public static RouteGroupBuilder MapReviewTasks(this RouteGroupBuilder group)
    {
        var tasks = group.MapGroup("/review-tasks").RequireAuthorization(Policies.Review).WithTags("Review");

        tasks.MapGet("/", ListAsync)
            .WithName("ListReviewTasks")
            .WithSummary("The review queue oldest first (Open and InProgress by default; filter by status or assignedTo=me), each with its document summary.");

        tasks.MapPost("/{id:guid}:claim", ClaimAsync)
            .WithName("ClaimReviewTask")
            .WithSummary("Assigns the task to the caller and marks it InProgress; 409 when another reviewer holds it.");

        tasks.MapPost("/{id:guid}:release", ReleaseAsync)
            .WithName("ReleaseReviewTask")
            .WithSummary("Returns the task to the queue. The assignee may release it; a tenant admin may release anyone's.");

        return group;
    }

    private static async Task<Results<Ok<Page<ReviewTaskDto>>, ValidationProblem>> ListAsync(
        string? status,
        string? assignedTo,
        string? cursor,
        int? limit,
        [FromServices] DocProcDbContext db,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var statuses = new List<ReviewTaskStatus>();
        if (string.IsNullOrWhiteSpace(status))
        {
            statuses.AddRange(QueueStatuses);
        }
        else
        {
            foreach (var token in status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Enum.TryParse<ReviewTaskStatus>(token, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
                {
                    statuses.Add(parsed);
                }
                else
                {
                    errors["status"] = [$"'{token}' is not one of {string.Join(", ", Enum.GetNames<ReviewTaskStatus>())}."];
                }
            }
        }

        PageCursor? after = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (PageCursor.TryDecode(cursor, out var decoded))
            {
                after = decoded;
            }
            else
            {
                errors["cursor"] = ["The cursor is not one this API issued."];
            }
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var query = db.ReviewTasks.AsNoTracking().Where(t => statuses.Contains(t.Status));
        if (!string.IsNullOrWhiteSpace(assignedTo))
        {
            var assignee = string.Equals(assignedTo.Trim(), "me", StringComparison.OrdinalIgnoreCase) ? DocumentQueries.ActorName(user) : assignedTo.Trim();
            query = query.Where(t => t.AssignedTo == assignee);
        }

        if (after is { } c)
        {
            query = query.Where(t => t.CreatedAt > c.At || (t.CreatedAt == c.At && t.Id.CompareTo(c.Id) > 0));
        }

        var size = PageCursor.ClampLimit(limit);
        var rows = await Rows(query.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).Take(size + 1)).ToListAsync(ct);
        var hasMore = rows.Count > size;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = rows.Select(ToDto).ToList();
        return TypedResults.Ok(new Page<ReviewTaskDto>(items, hasMore ? new PageCursor(items[^1].CreatedAt, items[^1].Id).Encode() : null));
    }

    private static async Task<Results<Ok<ReviewTaskDto>, ProblemHttpResult>> ClaimAsync(Guid id, [FromServices] DocProcDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        var me = DocumentQueries.ActorName(user);

        // First come, first served is decided by the database, not by a read followed by a write: the conditional
        // UPDATE matches only an unassigned open task, so of N simultaneous claims exactly one changes a row. It bumps
        // the Version token as a tracked save would, so a unit of work that loaded the task earlier (the pipeline
        // closing it, a document decision) cannot overwrite the claim unnoticed. One statement, so it needs no
        // transaction of its own; nothing else is written here.
        var claimed = await db.ReviewTasks
            .Where(t => t.Id == id && t.AssignedTo == null && t.Status == ReviewTaskStatus.Open)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.AssignedTo, me)
                    .SetProperty(t => t.Status, ReviewTaskStatus.InProgress)
                    .SetProperty(t => t.Version, t => t.Version + 1),
                ct);
        if (claimed == 0)
        {
            var task = await db.ReviewTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct);
            if (task is null)
            {
                return NotFound(id);
            }

            if (task.Status is ReviewTaskStatus.Done or ReviewTaskStatus.Skipped)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Task closed", detail: $"Review task {id} is {task.Status}.");
            }

            if (!string.Equals(task.AssignedTo, me, StringComparison.Ordinal))
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Task already claimed", detail: $"Review task {id} is assigned to {task.AssignedTo ?? "another reviewer"}.");
            }

            // Already mine: re-claiming is idempotent.
        }

        return TypedResults.Ok(await LoadDtoAsync(db, id, ct));
    }

    private static async Task<Results<Ok<ReviewTaskDto>, ProblemHttpResult>> ReleaseAsync(Guid id, [FromServices] DocProcDbContext db, ClaimsPrincipal user, IAuthorizationService authorization, CancellationToken ct)
    {
        var task = await db.ReviewTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct);
        if (task is null)
        {
            return NotFound(id);
        }

        if (task.Status is ReviewTaskStatus.Done or ReviewTaskStatus.Skipped)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Task closed", detail: $"Review task {id} is {task.Status}.");
        }

        var me = DocumentQueries.ActorName(user);
        if (task.AssignedTo is not null && !string.Equals(task.AssignedTo, me, StringComparison.Ordinal))
        {
            var admin = await authorization.AuthorizeAsync(user, Policies.TenantAdmin);
            if (!admin.Succeeded)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Task held by another reviewer", detail: $"Review task {id} is assigned to {task.AssignedTo}; only they or a tenant admin can release it.");
            }
        }

        if (task.AssignedTo is not null || task.Status != ReviewTaskStatus.Open)
        {
            // Release only the assignment the caller saw; if the task was claimed by someone else, closed or already
            // released in the meantime, the row no longer matches and the caller is told to reload.
            var assignee = task.AssignedTo;
            var status = task.Status;
            var released = await db.ReviewTasks
                .Where(t => t.Id == id && t.AssignedTo == assignee && t.Status == status)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(t => t.AssignedTo, (string?)null)
                        .SetProperty(t => t.Status, ReviewTaskStatus.Open)
                        .SetProperty(t => t.Version, t => t.Version + 1),
                    ct);
            if (released == 0)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Task changed", detail: $"Review task {id} was claimed, released or closed by someone else while this request ran; reload it.");
            }
        }

        return TypedResults.Ok(await LoadDtoAsync(db, id, ct));
    }

    private static async Task<ReviewTaskDto> LoadDtoAsync(DocProcDbContext db, Guid id, CancellationToken ct) =>
        ToDto(await Rows(db.ReviewTasks.AsNoTracking().Where(t => t.Id == id)).SingleAsync(ct));

    private static IQueryable<ReviewTaskRow> Rows(IQueryable<ReviewTask> tasks) =>
        tasks.Select(t => new ReviewTaskRow(
            t,
            t.Document == null
                ? null
                : new DocumentRow(
                    t.Document,
                    t.Document.Supplier != null ? t.Document.Supplier.Name : null,
                    t.Document.Flags.Count(f => f.ResolvedAt == null && f.Severity == FlagSeverity.Blocking),
                    t.Document.Flags.Count(f => f.ResolvedAt == null && f.Severity == FlagSeverity.Warning))));

    private static ReviewTaskDto ToDto(ReviewTaskRow row)
    {
        var t = row.Task;
        return new ReviewTaskDto(t.Id, t.DocumentId, t.Reason, t.AssignedTo, t.Status, t.CreatedAt, t.SlaDueAt, t.CompletedAt, row.Document is null ? null : DocumentQueries.ToSummary(row.Document));
    }

    private static ProblemHttpResult NotFound(Guid id) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Review task not found", detail: $"No review task {id} is visible in the current scope.");

    private sealed record ReviewTaskRow(ReviewTask Task, DocumentRow? Document);
}
