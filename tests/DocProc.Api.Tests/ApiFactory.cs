using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocProc.Application.Abstractions;
using DocProc.Application.Pipeline;
using DocProc.Application.Security;
using DocProc.Domain.Common;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using DocProc.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DocProc.Api.Tests;

/// <summary>
/// Hosts the API in-process with no identity provider and an in-memory SQLite database: the bearer handler is given
/// a test signing key and issuer instead of discovering them, tokens are minted locally with the same claims
/// Keycloak emits, and the entity model is created in SQLite (SQL Server-only features such as row-level security
/// and temporal tables are exercised by the integration tests instead).
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "https://test-issuer.local/realms/docproc";
    public const string Audience = "docproc-api";

    public static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    public static readonly Guid SuspendedTenant = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test-key" };

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Replaces the pipeline queue: the API only enqueues, and tests assert on what it enqueued.</summary>
    public CapturingPipelineQueue Queue { get; } = new();

    /// <summary>How the API writes JSON (web defaults, enums as strings), for reading responses into the endpoint records.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Root of the file-system blob store, a temp directory so the test host never writes under the API's content root.</summary>
    public string BlobRoot { get; } = Path.Combine(Path.GetTempPath(), "docproc-tests", Guid.NewGuid().ToString("N"), "blobs");

    /// <summary>Root of the file secret store, next to the blobs so both go when the factory is disposed.</summary>
    public string SecretRoot => Path.Combine(Path.GetDirectoryName(BlobRoot)!, "secrets");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Authentication:Oidc:Authority", string.Empty);
        builder.UseSetting("Authentication:Oidc:Audience", Audience);
        builder.UseSetting("Blobs:RootPath", BlobRoot);
        builder.UseSetting("Secrets:Path", SecretRoot);
        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters.ValidIssuer = Issuer;
                options.TokenValidationParameters.IssuerSigningKey = SigningKey;
            });

            _connection.Open();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddDbContext<DocProcDbContext>(o => o.UseSqlite(_connection));
            services.AddSingleton<ISystemDbContextFactory>(new InMemorySqliteDbContextFactory(_connection));
            services.AddScoped<IApiKeyValidator, ApiKeyValidator>();
            services.RemoveAll<IPipelineQueue>();
            services.AddSingleton<IPipelineQueue>(Queue);

            using var db = new InMemorySqliteDbContextFactory(_connection).CreatePlatformScoped();
            db.Database.EnsureCreated();
            Seed(db);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
            var directory = Path.GetDirectoryName(BlobRoot)!;
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>Inserts a key directly, bypassing the endpoint, for cases the endpoint would refuse (suspended tenant).</summary>
    public string InsertKey(Guid tenantId, string name, params ApiKeyScope[] scopes)
    {
        _ = Server; // the database is created when the host starts
        var generated = ApiKeys.Generate();
        using var db = new InMemorySqliteDbContextFactory(_connection).CreatePlatformScoped();
        db.ApiKeys.Add(new ApiKey { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = name, Prefix = generated.Prefix, Hash = generated.Hash, Scopes = [.. scopes], CreatedAt = Clock.GetUtcNow() });
        db.SaveChanges();
        return generated.Key;
    }

    public ApiKey? FindKey(string key)
    {
        _ = Server;
        using var db = new InMemorySqliteDbContextFactory(_connection).CreatePlatformScoped();
        var hash = ApiKeys.Hash(key);
        return db.ApiKeys.AsNoTracking().SingleOrDefault(k => k.Hash == hash);
    }

    /// <summary>Runs a seed or fix-up against a platform-scoped context and saves it; rows must carry their TenantId.</summary>
    public void Run(Action<DocProcDbContext> action)
    {
        _ = Server;
        using var db = new InMemorySqliteDbContextFactory(_connection).CreatePlatformScoped();
        action(db);
        db.SaveChanges();
    }

    /// <summary>Reads through a platform-scoped, no-tracking context: what is really in the database, whatever the API returned.</summary>
    public T Query<T>(Func<DocProcDbContext, T> query)
    {
        _ = Server;
        using var db = new InMemorySqliteDbContextFactory(_connection).CreatePlatformScoped();
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        return query(db);
    }

    private void Seed(DocProcDbContext db)
    {
        var now = Clock.GetUtcNow();
        db.Tenants.Add(new Tenant { Id = TenantA, Name = "Tenant A", Slug = "tenant-a", LegalName = "Gasthaus zur Post GmbH", DefaultCurrency = "EUR", CountryCode = "DE", TimeZone = "Europe/Berlin", CreatedAt = now, UpdatedAt = now });
        db.Tenants.Add(new Tenant { Id = TenantB, Name = "Tenant B", Slug = "tenant-b", LegalName = "Pizzeria Napoli d.o.o.", DefaultCurrency = "EUR", CountryCode = "HR", TimeZone = "Europe/Zagreb", CreatedAt = now, UpdatedAt = now });
        db.Tenants.Add(new Tenant { Id = SuspendedTenant, Name = "Suspended", Slug = "suspended", LegalName = "Closed Kitchen Ltd", Status = TenantStatus.Suspended, DefaultCurrency = "EUR", CountryCode = "DE", TimeZone = "Europe/Berlin", CreatedAt = now, UpdatedAt = now });
        db.SaveChanges();
    }

    /// <summary>
    /// Inserts a document the way the pipeline would have left it: an ingestion with one item whose bytes are in the
    /// raw store, the canonical invoice JSON, denormalised columns, flags, timeline events, optionally a review task
    /// and a rendered first page in the derived store. Everything the document endpoints read.
    /// </summary>
    public async Task<SeededDocument> SeedDocumentAsync(DocumentSeed seed)
    {
        _ = Server;
        var blobs = Services.GetRequiredService<IContainerBlobStore>();
        var now = seed.CreatedAt ?? Clock.GetUtcNow();
        var tenantId = seed.TenantId;

        using var db = new InMemorySqliteDbContextFactory(_connection).CreatePlatformScoped();

        var channel = db.IngestionChannels.FirstOrDefault(c => c.TenantId == tenantId && c.Type == ChannelType.ApiUpload);
        if (channel is null)
        {
            channel = new IngestionChannel { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = "API upload", Type = ChannelType.ApiUpload, CreatedAt = now };
            db.IngestionChannels.Add(channel);
        }

        Supplier? supplier = null;
        if (seed.SupplierName is { } supplierName)
        {
            supplier = db.Suppliers.FirstOrDefault(s => s.TenantId == tenantId && s.Name == supplierName);
            if (supplier is null)
            {
                supplier = new Supplier { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = supplierName, NormalizedName = Identifiers.NormalizeName(supplierName), CreatedAt = now, UpdatedAt = now };
                db.Suppliers.Add(supplier);
            }
        }

        var originalBytes = seed.OriginalBytes ?? Encoding.ASCII.GetBytes("%PDF-1.4\n% seeded original " + Guid.NewGuid().ToString("N") + "\n%%EOF\n");
        var raw = await blobs.PutAsync(BlobContainers.Raw, tenantId, new MemoryStream(originalBytes), seed.ContentType, CancellationToken.None);
        var ingestion = new Ingestion
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ChannelId = channel.Id,
            ReceivedAt = now,
            SourceFrom = seed.SourceFrom,
            SourceFileName = seed.FileName,
            RawBlob = ToReference(raw),
            Status = IngestionStatus.Processed,
            UpdatedAt = now,
        };
        var item = new IngestionItem
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            IngestionId = ingestion.Id,
            FileName = seed.FileName,
            MimeType = seed.ContentType,
            SizeBytes = raw.SizeBytes,
            Sha256 = raw.Sha256,
            Blob = ToReference(raw),
            Kind = ItemKind.Pdf,
            CreatedAt = now,
        };
        ingestion.Items.Add(item);
        db.Ingestions.Add(ingestion);

        var invoice = seed.Invoice;
        var document = new Document
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            IngestionItemId = item.Id,
            PageCount = 1,
            Type = seed.Type,
            TypeConfidence = 0.97,
            TypeSource = TypeSource.Rule,
            SupplierId = supplier?.Id,
            ExtractionMethod = invoice is null ? null : ExtractionMethod.Llm,
            Status = seed.Status,
            FailureReason = seed.Status == DocumentStatus.Failed ? "seeded failure" : null,
            CanonicalJson = invoice is null ? null : CanonicalDocumentSerializer.Serialize(invoice),
            CanonicalSchemaVersion = invoice is null ? null : CanonicalDocumentSerializer.SchemaVersion,
            DocumentNumber = invoice?.InvoiceNumber.Value?.Verbatim,
            DocumentNumberNormalized = invoice?.InvoiceNumber.Value?.Normalized,
            IssueDate = invoice is { IssueDate.HasValue: true } ? invoice.IssueDate.Value : null,
            Currency = invoice?.Currency.Value,
            TotalGross = invoice is { TotalGross.HasValue: true } ? invoice.TotalGross.Value.Amount : null,
            Costs = new DocumentCosts { LlmInputTokens = 1200, LlmOutputTokens = 300, EstimatedUsd = 0.012m, LlmProvider = "anthropic", LlmModel = "claude-opus-5-5", KeyOwner = KeyOwner.Platform },
            CreatedAt = now,
            UpdatedAt = now,
            ApprovedAt = seed.Status is DocumentStatus.Approved or DocumentStatus.AutoApproved ? now : null,
            ApprovedBy = seed.Status == DocumentStatus.Approved ? "seed" : null,
        };

        if (seed.PagePng is { } png)
        {
            var derived = await blobs.PutAsync(BlobContainers.Derived, tenantId, new MemoryStream(png), "image/png", CancellationToken.None);
            document.PageImageKeys.Add(derived.Key);
        }

        if (seed.TextLayerJson is { } text)
        {
            var layer = await blobs.PutAsync(BlobContainers.Derived, tenantId, new MemoryStream(Encoding.UTF8.GetBytes(text)), "application/json", CancellationToken.None);
            document.TextLayerBlob = ToReference(layer);
        }

        foreach (var flag in seed.Flags)
        {
            document.Flags.Add(DocumentFlagRecord.FromDomain(tenantId, document.Id, flag, now));
        }

        document.Events.Add(new DocumentEvent { TenantId = tenantId, DocumentId = document.Id, EventType = DocumentEventTypes.Received, ToStatus = DocumentStatus.Received, Actor = "pipeline", At = now });
        document.Events.Add(new DocumentEvent { TenantId = tenantId, DocumentId = document.Id, EventType = DocumentEventTypes.StageCompleted, FromStatus = DocumentStatus.Received, ToStatus = seed.Status, Actor = "pipeline", DetailsJson = "{\"stage\":\"seed\"}", At = now });

        ReviewTask? task = null;
        if (seed.ReviewTask)
        {
            task = new ReviewTask { Id = Guid.CreateVersion7(), TenantId = tenantId, DocumentId = document.Id, Reason = seed.Flags.Count > 0 ? string.Join(", ", seed.Flags.Select(f => f.Code)) : "review", AssignedTo = seed.AssignedTo, Status = seed.AssignedTo is null ? ReviewTaskStatus.Open : ReviewTaskStatus.InProgress, CreatedAt = now };
            document.ReviewTasks.Add(task);
        }

        db.Documents.Add(document);
        db.SaveChanges();
        return new SeededDocument(document.Id, ingestion.Id, item.Id, task?.Id, document.CanonicalJson, originalBytes);
    }

    private static BlobReference ToReference(BlobRef blob) => new()
    {
        Container = blob.Container,
        Key = blob.Key,
        Sha256 = blob.Sha256,
        SizeBytes = blob.SizeBytes,
        ContentType = blob.ContentType,
    };

    private sealed class InMemorySqliteDbContextFactory(SqliteConnection connection) : ISystemDbContextFactory
    {
        public DocProcDbContext CreatePlatformScoped()
        {
            var options = new DbContextOptionsBuilder<DocProcDbContext>().UseSqlite(connection).Options;
            return new DocProcDbContext(options, TenantContext.Platform());
        }
    }

    public HttpClient ClientFor(TokenSpec? token, string? tenantHeader = null)
    {
        var client = CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Mint(token));
        }

        if (tenantHeader is not null)
        {
            client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantHeader);
        }

        return client;
    }

    public HttpClient ClientForApiKey(string key, bool viaAuthorizationHeader = false, string? tenantHeader = null)
    {
        var client = CreateClient();
        if (viaAuthorizationHeader)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("ApiKey", key);
        }
        else
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
        }

        if (tenantHeader is not null)
        {
            client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantHeader);
        }

        return client;
    }

    public static string Mint(TokenSpec spec)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = spec.Subject,
            ["preferred_username"] = spec.Subject,
            ["email"] = $"{spec.Subject}@example.test",
            ["roles"] = spec.Roles,
        };
        if (spec.TenantId is not null)
        {
            claims["tenant_id"] = spec.TenantId;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = spec.Issuer ?? Issuer,
            Audience = spec.Audience ?? Audience,
            Claims = claims,
            IssuedAt = DateTime.UtcNow.AddMinutes(-1),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = spec.Expires ?? DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
            Subject = new ClaimsIdentity(),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}

public sealed record TokenSpec(string Subject, string[] Roles, string? TenantId = null, string? Issuer = null, string? Audience = null, DateTime? Expires = null);
