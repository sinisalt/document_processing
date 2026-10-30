using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using DocProc.Application.Security;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Infrastructure.Persistence.Tenancy;
using DocProc.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
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
    public static readonly Guid SuspendedTenant = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test-key" };

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Authentication:Oidc:Authority", string.Empty);
        builder.UseSetting("Authentication:Oidc:Audience", Audience);
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

    private void Seed(DocProcDbContext db)
    {
        var now = Clock.GetUtcNow();
        db.Tenants.Add(new Tenant { Id = TenantA, Name = "Tenant A", Slug = "tenant-a", LegalName = "Gasthaus zur Post GmbH", DefaultCurrency = "EUR", CountryCode = "DE", TimeZone = "Europe/Berlin", CreatedAt = now, UpdatedAt = now });
        db.Tenants.Add(new Tenant { Id = SuspendedTenant, Name = "Suspended", Slug = "suspended", LegalName = "Closed Kitchen Ltd", Status = TenantStatus.Suspended, DefaultCurrency = "EUR", CountryCode = "DE", TimeZone = "Europe/Berlin", CreatedAt = now, UpdatedAt = now });
        db.SaveChanges();
    }

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
