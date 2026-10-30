using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using DocProc.Pipeline.Intake;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

namespace DocProc.Api.Endpoints;

/// <summary>Upload limits of <c>POST /ingestions</c>; the multipart body limit in <c>FormOptions</c> is derived from them.</summary>
public static class IngestionLimits
{
    public const int MaxFilesPerUpload = 20;
    public const long MaxFileBytes = 50L * 1024 * 1024;

    /// <summary>A multipart body: every file at its maximum plus headroom for the framing.</summary>
    public const long MaxRequestBodyBytes = (MaxFilesPerUpload * MaxFileBytes) + (16L * 1024 * 1024);

    /// <summary>A JSON body carries one file as base64 (4/3 of the bytes) plus the few other properties; it is buffered, so it gets the tighter cap.</summary>
    public const long MaxJsonRequestBodyBytes = (MaxFileBytes / 3 * 4) + (4L * 1024 * 1024);

    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const int MaxIdempotencyKeyLength = 200;

    /// <summary>How long a key replays its first response; after that the key may be used for a new upload.</summary>
    public static readonly TimeSpan IdempotencyWindow = TimeSpan.FromHours(24);

    /// <summary>A key row younger than this at the moment our save collides with it was written by the request we raced; its response is replayed. An older one should have been found by the lookup, so that collision is a bug and surfaces as the error it is.</summary>
    public static readonly TimeSpan ConcurrentUploadWindow = TimeSpan.FromSeconds(60);
}

/// <summary>JSON alternative to multipart for a single file: integrations that cannot build multipart bodies.</summary>
public sealed record IngestionJsonUpload(string FileName, string? ContentType, string Base64, Guid? ChannelId);

public sealed record IngestionItemCreated(Guid Id, string FileName, long SizeBytes, string Sha256);

public sealed record IngestionCreated(Guid Id, IngestionStatus Status, IReadOnlyList<IngestionItemCreated> Items);

public sealed record IngestionSummary(Guid Id, Guid ChannelId, IngestionStatus Status, DateTimeOffset ReceivedAt, string? SourceFrom, string? SourceFileName, int ItemCount, DateTimeOffset UpdatedAt);

public sealed record IngestionDocumentRef(Guid Id, DocumentStatus Status);

public sealed record IngestionItemDto(Guid Id, string FileName, string MimeType, long SizeBytes, string Sha256, ItemKind Kind, ItemDisposition Disposition, string? DispositionReason, IReadOnlyList<IngestionDocumentRef> Documents);

public sealed record IngestionDetail(
    Guid Id,
    Guid ChannelId,
    IngestionStatus Status,
    DateTimeOffset ReceivedAt,
    string? SourceFrom,
    string? SourceSubject,
    string? SourceFileName,
    string? LastError,
    int Attempts,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<IngestionItemDto> Items);

/// <summary>
/// Stage 1 for API and portal uploads (docs/03 "API upload"): the API stores the bytes, records the ingestion and
/// hands the rest to the pipeline through <see cref="IPipelineQueue"/>. Uploads need a tenant (an API key's tenant or
/// a customer user's claim; a SuperAdmin sends X-Tenant-Id); the GETs also work in platform scope for operators,
/// where the data layer returns every tenant.
/// </summary>
public static class IngestionEndpoints
{
    private static readonly JsonSerializerOptions StoredResponseOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static RouteGroupBuilder MapIngestions(this RouteGroupBuilder group)
    {
        var ingestions = group.MapGroup("/ingestions").WithTags("Ingestions");

        ingestions.MapPost("/", UploadAsync)
            .RequireAuthorization(Policies.Ingest)
            .Accepts<IngestionJsonUpload>("application/json", "multipart/form-data")
            .AddOpenApiOperationTransformer(DescribeMultipartBody)
            .WithName("CreateIngestion")
            .WithSummary("Uploads one or more files (multipart/form-data, or a single base64 file as JSON) and queues them for processing. Send Idempotency-Key to make retries safe.");

        ingestions.MapGet("/", ListAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("ListIngestions")
            .WithSummary("Ingestions newest first, cursor-paginated.");

        ingestions.MapGet("/{id:guid}", GetAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("GetIngestion")
            .WithSummary("One ingestion with its items and the documents split from them.");

        return group;
    }

    private static async Task<Results<Accepted<IngestionCreated>, ValidationProblem, ProblemHttpResult>> UploadAsync(
        HttpContext context,
        [FromHeader(Name = IngestionLimits.IdempotencyKeyHeader)] string? idempotencyKey,
        [FromServices] DocProcDbContext db,
        ITenantContext tenant,
        IBlobStore blobs,
        IPipelineQueue queue,
        IdempotencyKeyPurge purge,
        TimeProvider clock,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (tenant.TenantId is not { } tenantId)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Tenant required", detail: "Uploads belong to a tenant. Platform users must send X-Tenant-Id.");
        }

        idempotencyKey = idempotencyKey?.Trim();
        if (idempotencyKey is { Length: > IngestionLimits.MaxIdempotencyKeyLength })
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { [IngestionLimits.IdempotencyKeyHeader] = [$"At most {IngestionLimits.MaxIdempotencyKeyLength} characters."] });
        }

        var maxBody = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (maxBody is { IsReadOnly: false })
        {
            // A JSON upload is one base64 file read into memory; a multipart one streams up to twenty files to the store.
            maxBody.MaxRequestBodySize = context.Request.HasJsonContentType() ? IngestionLimits.MaxJsonRequestBodyBytes : IngestionLimits.MaxRequestBodyBytes;
        }

        var (uploads, channelId, problem) = await ReadUploadsAsync(context.Request, ct);
        if (problem is not null)
        {
            return problem;
        }

        // Hash first, store later: the idempotency decision (replay, refuse, proceed) is taken from the request alone,
        // so a replayed or mismatched request never touches the blob store.
        var hashes = new List<string>(uploads.Count);
        foreach (var upload in uploads)
        {
            var content = upload.Open();
            await using (content.ConfigureAwait(false))
            {
                hashes.Add(Convert.ToHexStringLower(await SHA256.HashDataAsync(content, ct)));
            }
        }

        var now = clock.GetUtcNow();
        var requestHash = RequestHash(channelId, uploads, hashes);
        IdempotencyKey? expired = null;
        if (idempotencyKey is not null)
        {
            // Looked up without a time filter: an expired row must be replaced, not ignored, or the unique index would
            // refuse every later use of the key.
            var earlier = await db.IdempotencyKeys.SingleOrDefaultAsync(k => k.Key == idempotencyKey, ct);
            if (earlier is not null && earlier.CreatedAt >= now - IngestionLimits.IdempotencyWindow)
            {
                if (!IsSameRequest(earlier, requestHash))
                {
                    return KeyReused(idempotencyKey);
                }

                return Replay(earlier);
            }

            expired = earlier;
        }

        // Content-addressed storage: storing the same bytes twice is a no-op, so a retry after a failed save is safe. The
        // store-and-record half is shared with the development mode's demo data (UploadIntake), so both create the same rows.
        var stored = await UploadIntake.StoreAsync(
            blobs,
            tenantId,
            uploads.Select(u => new UploadFile(u.FileName, u.ContentType, ItemKinds.FromContentType(u.ContentType, u.FileName), u.Open)).ToList(),
            ct);

        var channel = await ResolveChannelAsync(db, tenantId, channelId, now, ct);
        if (channel is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["channelId"] = ["No such channel in this tenant."] });
        }

        var ingestion = UploadIntake.CreateIngestion(tenantId, channel.Id, DocumentQueries.ActorName(user), stored, now);
        db.Ingestions.Add(ingestion);

        var response = new IngestionCreated(ingestion.Id, ingestion.Status, ingestion.Items.Select(i => new IngestionItemCreated(i.Id, i.FileName, i.SizeBytes, i.Sha256)).ToList());
        var location = LocationOf(ingestion.Id);
        if (idempotencyKey is not null)
        {
            if (expired is not null)
            {
                // Same SaveChanges as the insert below: EF orders the delete before the insert that reuses the index value.
                db.IdempotencyKeys.Remove(expired);
            }

            db.IdempotencyKeys.Add(new IdempotencyKey
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Key = idempotencyKey,
                RequestHash = requestHash,
                StatusCode = StatusCodes.Status202Accepted,
                ResponseJson = JsonSerializer.Serialize(response, StoredResponseOptions),
                Location = location,
                CreatedAt = now,
            });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (idempotencyKey is not null && IdempotencyConflicts.IsConcurrentKeyWrite(ex, replacedExpiredRow: expired is not null))
        {
            // A request with the same key committed between our lookup and our save: either its insert made ours fire
            // the unique (TenantId, Key) index, or it replaced the expired row we were replacing too, so our delete of
            // that row affected nothing. Our own rows were rolled back with the failed save and the winner's are
            // committed, so the caller gets what its retry would get: the winner's response for the same request, 422
            // for a different one. Only a row written moments ago is that race; an older one means the lookup should
            // have found it, which is a bug worth a 500, not a "retry later".
            var winner = await db.IdempotencyKeys.AsNoTracking().SingleOrDefaultAsync(k => k.Key == idempotencyKey, ct);
            if (winner is null || winner.CreatedAt < now - IngestionLimits.ConcurrentUploadWindow)
            {
                throw;
            }

            if (!IsSameRequest(winner, requestHash))
            {
                return KeyReused(idempotencyKey);
            }

            return Replay(winner);
        }

        await queue.EnqueueAsync(new ProcessIngestion(tenantId, ingestion.Id), ct);
        await purge.RunIfDueAsync(db, tenantId, now, ct);
        return TypedResults.Accepted(location, response);
    }

    private static async Task<Results<Ok<Page<IngestionSummary>>, ValidationProblem>> ListAsync(string? cursor, int? limit, [FromServices] DocProcDbContext db, CancellationToken ct)
    {
        var query = db.Ingestions.AsNoTracking();
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!PageCursor.TryDecode(cursor, out var c))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["cursor"] = ["The cursor is not one this API issued."] });
            }

            query = query.Where(i => i.ReceivedAt < c.At || (i.ReceivedAt == c.At && i.Id.CompareTo(c.Id) < 0));
        }

        var size = PageCursor.ClampLimit(limit);
        var rows = await query
            .OrderByDescending(i => i.ReceivedAt)
            .ThenByDescending(i => i.Id)
            .Take(size + 1)
            .Select(i => new IngestionSummary(i.Id, i.ChannelId, i.Status, i.ReceivedAt, i.SourceFrom, i.SourceFileName, i.Items.Count, i.UpdatedAt))
            .ToListAsync(ct);

        var hasMore = rows.Count > size;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return TypedResults.Ok(new Page<IngestionSummary>(rows, hasMore ? new PageCursor(rows[^1].ReceivedAt, rows[^1].Id).Encode() : null));
    }

    private static async Task<Results<Ok<IngestionDetail>, ProblemHttpResult>> GetAsync(Guid id, [FromServices] DocProcDbContext db, CancellationToken ct)
    {
        var ingestion = await db.Ingestions.AsNoTracking()
            .Include(i => i.Items)
            .ThenInclude(i => i.Documents)
            .SingleOrDefaultAsync(i => i.Id == id, ct);
        if (ingestion is null)
        {
            return NotFound(id);
        }

        var items = ingestion.Items
            .OrderBy(i => i.CreatedAt)
            .ThenBy(i => i.Id)
            .Select(i => new IngestionItemDto(
                i.Id,
                i.FileName,
                i.MimeType,
                i.SizeBytes,
                i.Sha256,
                i.Kind,
                i.Disposition,
                i.DispositionReason,
                i.Documents.OrderBy(d => d.PageStart).ThenBy(d => d.Id).Select(d => new IngestionDocumentRef(d.Id, d.Status)).ToList()))
            .ToList();

        return TypedResults.Ok(new IngestionDetail(
            ingestion.Id,
            ingestion.ChannelId,
            ingestion.Status,
            ingestion.ReceivedAt,
            ingestion.SourceFrom,
            ingestion.SourceSubject,
            ingestion.SourceFileName,
            ingestion.LastError,
            ingestion.Attempts,
            ingestion.UpdatedAt,
            items));
    }

    private static ProblemHttpResult NotFound(Guid id) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Ingestion not found", detail: $"No ingestion {id} is visible in the current scope.");

    /// <summary>
    /// The handler reads the request itself, so the generator only sees the JSON shape; this adds the multipart one
    /// (one or more <c>files</c> parts, optional <c>channelId</c>) so generated clients can upload files.
    /// </summary>
    private static Task DescribeMultipartBody(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var body = operation.RequestBody as OpenApiRequestBody ?? new OpenApiRequestBody { Required = true };
        body.Content ??= new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal);
        operation.RequestBody = body;
        body.Content["multipart/form-data"] = new OpenApiMediaType
        {
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Required = new HashSet<string>(StringComparer.Ordinal) { "files" },
                Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
                {
                    ["files"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Array,
                        Description = $"One to {IngestionLimits.MaxFilesPerUpload} files, at most {IngestionLimits.MaxFileBytes} bytes each.",
                        Items = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" },
                    },
                    ["channelId"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String | JsonSchemaType.Null,
                        Format = "uuid",
                        Description = "An ingestion channel of the tenant; default: the tenant's API-upload channel.",
                    },
                },
            },
        };
        return Task.CompletedTask;
    }

    private static string LocationOf(Guid ingestionId) => $"/api/v1/ingestions/{ingestionId}";

    private static bool IsSameRequest(IdempotencyKey earlier, string requestHash) => string.Equals(earlier.RequestHash, requestHash, StringComparison.Ordinal);

    /// <summary>The first response again, exactly as stored, with its Location.</summary>
    private static Accepted<IngestionCreated> Replay(IdempotencyKey earlier)
    {
        var replay = JsonSerializer.Deserialize<IngestionCreated>(earlier.ResponseJson, StoredResponseOptions)!;
        return TypedResults.Accepted(earlier.Location ?? LocationOf(replay.Id), replay);
    }

    private static ProblemHttpResult KeyReused(string idempotencyKey) =>
        TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Idempotency key reused", detail: $"{IngestionLimits.IdempotencyKeyHeader} '{idempotencyKey}' was already used for a different upload.");

    /// <summary>The files of the request as deferred streams, from a multipart form or a JSON body; a problem result when the request is not an upload.</summary>
    private static async Task<(List<UploadedFile> Files, Guid? ChannelId, Results<Accepted<IngestionCreated>, ValidationProblem, ProblemHttpResult>? Problem)> ReadUploadsAsync(HttpRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var files = new List<UploadedFile>();
        Guid? channelId = null;

        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(ct);
            var channelText = form["channelId"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(channelText))
            {
                if (Guid.TryParse(channelText, out var parsed))
                {
                    channelId = parsed;
                }
                else
                {
                    errors["channelId"] = ["Must be a GUID."];
                }
            }

            foreach (var file in form.Files)
            {
                files.Add(new UploadedFile(SafeFileName(file.FileName), ItemKinds.Normalize(file.ContentType), file.Length, file.OpenReadStream));
            }

            if (files.Count == 0)
            {
                errors["files"] = ["At least one file part is required."];
            }
        }
        else if (request.HasJsonContentType())
        {
            var body = await request.ReadFromJsonAsync<IngestionJsonUpload>(ct);
            if (body is null)
            {
                errors["body"] = ["A JSON body with fileName and base64 is required."];
            }
            else
            {
                channelId = body.ChannelId;
                if (string.IsNullOrWhiteSpace(body.FileName))
                {
                    errors["fileName"] = ["Required."];
                }

                byte[]? bytes = null;
                if (string.IsNullOrWhiteSpace(body.Base64))
                {
                    errors["base64"] = ["Required."];
                }
                else
                {
                    try
                    {
                        bytes = Convert.FromBase64String(body.Base64);
                    }
                    catch (FormatException)
                    {
                        errors["base64"] = ["Not valid base64."];
                    }
                }

                if (bytes is not null && errors.Count == 0)
                {
                    files.Add(new UploadedFile(SafeFileName(body.FileName), ItemKinds.Normalize(body.ContentType), bytes.Length, () => new MemoryStream(bytes, writable: false)));
                }
            }
        }
        else
        {
            return (files, null, TypedResults.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Unsupported media type", detail: "Send multipart/form-data with one or more file parts, or application/json with {fileName, contentType, base64}."));
        }

        if (errors.Count > 0)
        {
            return (files, channelId, TypedResults.ValidationProblem(errors));
        }

        if (files.Count > IngestionLimits.MaxFilesPerUpload)
        {
            return (files, channelId, TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["files"] = [$"At most {IngestionLimits.MaxFilesPerUpload} files per upload."] }));
        }

        foreach (var file in files)
        {
            if (file.Length == 0)
            {
                return (files, channelId, TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["files"] = [$"'{file.FileName}' is empty."] }));
            }

            if (file.Length > IngestionLimits.MaxFileBytes)
            {
                return (files, channelId, TypedResults.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "File too large", detail: $"'{file.FileName}' is {file.Length} bytes; the limit is {IngestionLimits.MaxFileBytes} bytes per file."));
            }
        }

        return (files, channelId, null);
    }

    /// <summary>
    /// The named channel of the tenant, or its API-upload channel, created on first use so a fresh tenant can upload
    /// without set-up. Two simultaneous first uploads of a tenant can each create one: there is no unique index on
    /// (TenantId, Type) because a tenant may legitimately run several API channels, so the duplicate is tolerated on
    /// purpose. Every later upload picks the oldest, and the extra channel is an ordinary row a tenant admin can
    /// rename or delete through <c>/channels</c>. Tenants created through <c>POST /tenants</c> get their default
    /// channels at creation and never hit this path.
    /// </summary>
    private static async Task<IngestionChannel?> ResolveChannelAsync(DocProcDbContext db, Guid tenantId, Guid? channelId, DateTimeOffset now, CancellationToken ct)
    {
        if (channelId is { } requested)
        {
            return await db.IngestionChannels.SingleOrDefaultAsync(c => c.Id == requested && c.TenantId == tenantId, ct);
        }

        var existing = await db.IngestionChannels
            .Where(c => c.TenantId == tenantId && c.Type == ChannelType.ApiUpload)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return existing;
        }

        var created = new IngestionChannel
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = "API upload",
            Type = ChannelType.ApiUpload,
            Status = ChannelStatus.Active,
            CreatedAt = now,
        };
        db.IngestionChannels.Add(created);
        return created;
    }

    /// <summary>What makes two uploads "the same request" for an idempotency key: the channel and the files' names and content hashes, in order.</summary>
    private static string RequestHash(Guid? channelId, List<UploadedFile> files, List<string> sha256s)
    {
        var builder = new StringBuilder();
        builder.Append(channelId?.ToString("D") ?? string.Empty).Append('\n');
        for (var i = 0; i < files.Count; i++)
        {
            builder.Append(files[i].FileName).Append('\u001f').Append(sha256s[i]).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string SafeFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName?.Trim() ?? string.Empty);
        return string.IsNullOrEmpty(name) ? "upload" : name;
    }

    private sealed record UploadedFile(string FileName, string ContentType, long Length, Func<Stream> Open);
}
