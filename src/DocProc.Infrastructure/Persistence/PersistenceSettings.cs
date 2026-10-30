using Microsoft.Extensions.Configuration;

namespace DocProc.Infrastructure.Persistence;

/// <summary>Which database engine the hosts talk to.</summary>
public enum PersistenceProvider
{
    /// <summary>SQL Server 2025 / Azure SQL with migrations, row-level security and temporal tables (docs/01 "Data").</summary>
    SqlServer,

    /// <summary>
    /// A local SQLite file for the dependency-free development mode (docs/09 "Running without Docker"). The schema is
    /// created from the model, not from the migrations; there is no row-level security, no temporal history and no
    /// server-side ordering by DateTimeOffset, so it is refused in production.
    /// </summary>
    Sqlite,
}

/// <summary>
/// The <c>Persistence</c> configuration section. <see cref="Provider"/> defaults to SQL Server when
/// <c>ConnectionStrings:DocProc</c> is set and to nothing at all otherwise (a host can still start without a database).
/// </summary>
public sealed class PersistenceSettings
{
    public const string SectionName = "Persistence";
    public const string DefaultSqlitePath = "./data/docproc.db";

    public PersistenceProvider? Provider { get; set; }

    /// <summary>SQLite database file; relative paths resolve against the working directory and the folder is created.</summary>
    public string SqlitePath { get; set; } = DefaultSqlitePath;

    /// <summary>Inserts the demo tenants, suppliers and LLM configuration (<see cref="DevSeed"/>). Implied by <see cref="PersistenceProvider.Sqlite"/>.</summary>
    public bool SeedDevData { get; set; }

    public static PersistenceSettings From(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<PersistenceSettings>() ?? new PersistenceSettings();

    /// <summary>The provider that applies: the configured one, else SQL Server when a connection string exists, else null.</summary>
    public PersistenceProvider? Resolve(string? connectionString) =>
        Provider ?? (string.IsNullOrWhiteSpace(connectionString) ? null : PersistenceProvider.SqlServer);
}
