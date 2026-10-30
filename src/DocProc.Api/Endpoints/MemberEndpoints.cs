using DocProc.Api.Auth;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static DocProc.Api.Endpoints.EndpointSupport;

namespace DocProc.Api.Endpoints;

public sealed record MemberResponse(string UserId, string? Email, string? DisplayName, string Role, DateTimeOffset CreatedAt)
{
    internal static MemberResponse From(TenantMember m) => new(m.UserId, m.Email, m.DisplayName, m.Role.ToString(), m.CreatedAt);
}

public sealed record AddMemberRequest(string UserId, string? Email, string? DisplayName, string Role);

public sealed record ChangeMemberRoleRequest(string Role);

/// <summary>
/// Members of the current tenant (docs/02 §1 TenantMember; docs/05: TenantAdmin manages members). The tenant is the
/// resolved scope; the request context filters by it. A tenant must always keep one TenantAdmin, so the last one can
/// neither be removed nor demoted.
/// </summary>
public static class MemberEndpoints
{
    public static RouteGroupBuilder MapMembers(this RouteGroupBuilder group)
    {
        var members = group.MapGroup("/members").WithTags("Members");

        members.MapGet("/", async Task<Results<Ok<IReadOnlyList<MemberResponse>>, ProblemHttpResult>> ([FromServices] DocProcDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Members") is { } problem)
            {
                return problem;
            }

            var rows = await db.TenantMembers.AsNoTracking().OrderBy(m => m.UserId).ToListAsync(ct);
            return TypedResults.Ok<IReadOnlyList<MemberResponse>>(rows.Select(MemberResponse.From).ToList());
        })
        .RequireAuthorization(Policies.Read)
        .WithName("ListMembers")
        .WithSummary("Members of the current tenant and their roles.");

        members.MapPost("/", async Task<Results<Created<MemberResponse>, ValidationProblem, ProblemHttpResult>> (AddMemberRequest request, [FromServices] DocProcDbContext db, ITenantContext tenant, TimeProvider clock, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Members") is { } problem)
            {
                return problem;
            }

            var errors = new Errors().Require("userId", request.UserId, 200).Optional("email", request.Email, 320).Optional("displayName", request.DisplayName, 200);
            if (!TryParseEnum<TenantRole>(request.Role, out var role))
            {
                errors.Add("role", $"Unknown role. Allowed: {Names<TenantRole>()}.");
            }

            if (errors.ToProblem() is { } invalid)
            {
                return invalid;
            }

            if (await CurrentTenantAsync(db, ct) is null)
            {
                return UnknownTenant();
            }

            var userId = request.UserId.Trim();
            if (await db.TenantMembers.AnyAsync(m => m.UserId == userId, ct))
            {
                return Conflict("Already a member", $"User '{userId}' is already a member of this tenant; change the role with PATCH.");
            }

            var member = new TenantMember
            {
                TenantId = tenant.TenantId!.Value,
                UserId = userId,
                Email = TrimToNull(request.Email),
                DisplayName = TrimToNull(request.DisplayName),
                Role = role,
                CreatedAt = clock.GetUtcNow(),
            };
            db.TenantMembers.Add(member);
            await db.SaveChangesAsync(ct);
            return TypedResults.Created($"/api/v1/members/{Uri.EscapeDataString(userId)}", MemberResponse.From(member));
        })
        .RequireAuthorization(Policies.TenantAdmin)
        .WithName("AddMember")
        .WithSummary("Adds a user (by identity-provider subject) to the current tenant with a role.");

        members.MapPatch("/{userId}", async Task<Results<Ok<MemberResponse>, NotFound, ValidationProblem, ProblemHttpResult>> (string userId, ChangeMemberRoleRequest request, [FromServices] DocProcDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Members") is { } problem)
            {
                return problem;
            }

            if (!TryParseEnum<TenantRole>(request.Role, out var role))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["role"] = [$"Unknown role. Allowed: {Names<TenantRole>()}."] });
            }

            var member = await db.TenantMembers.SingleOrDefaultAsync(m => m.UserId == userId, ct);
            if (member is null)
            {
                return TypedResults.NotFound();
            }

            if (member.Role == TenantRole.TenantAdmin && role != TenantRole.TenantAdmin && await IsLastAdminAsync(db, ct))
            {
                return LastAdmin();
            }

            member.Role = role;
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(MemberResponse.From(member));
        })
        .RequireAuthorization(Policies.TenantAdmin)
        .WithName("ChangeMemberRole")
        .WithSummary("Changes a member's role. The last TenantAdmin cannot be demoted.");

        members.MapDelete("/{userId}", async Task<Results<NoContent, NotFound, ProblemHttpResult>> (string userId, [FromServices] DocProcDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (RequireTenant(tenant, "Members") is { } problem)
            {
                return problem;
            }

            var member = await db.TenantMembers.SingleOrDefaultAsync(m => m.UserId == userId, ct);
            if (member is null)
            {
                return TypedResults.NotFound();
            }

            if (member.Role == TenantRole.TenantAdmin && await IsLastAdminAsync(db, ct))
            {
                return LastAdmin();
            }

            db.TenantMembers.Remove(member);
            await db.SaveChangesAsync(ct);
            return TypedResults.NoContent();
        })
        .RequireAuthorization(Policies.TenantAdmin)
        .WithName("RemoveMember")
        .WithSummary("Removes a member. The last TenantAdmin cannot be removed.");

        return group;
    }

    private static async Task<bool> IsLastAdminAsync(DocProcDbContext db, CancellationToken ct) =>
        await db.TenantMembers.CountAsync(m => m.Role == TenantRole.TenantAdmin, ct) <= 1;

    private static ProblemHttpResult LastAdmin() =>
        Conflict("Last tenant admin", "A tenant must keep at least one TenantAdmin. Promote another member first.");
}
