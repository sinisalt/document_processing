namespace DocProc.Infrastructure.Persistence.Entities;

/// <summary>Where a master-data source reads its records (docs/11 §6.8): an http destination of the tenant, or uploads only.</summary>
public static class MasterDataTransports
{
    /// <summary>A GET of an http destination's <c>urlTemplate</c>, with its headers and authentication.</summary>
    public const string Http = "http";

    /// <summary>Records arrive only by upload (<c>POST /master-data-sources/{id}:run</c> with a body, or <c>POST /suppliers:import</c>).</summary>
    public const string Upload = "upload";

    public static readonly IReadOnlyList<string> All = [Http, Upload];
}

/// <summary>What a master-data source writes (docs/11 §6.8 <c>Target</c>); P5 builds the suppliers.</summary>
public static class MasterDataTargets
{
    public const string Suppliers = "suppliers";

    public static readonly IReadOnlyList<string> All = [Suppliers];
}

/// <summary>
/// A master-data source (docs/11 §6.8, §14): a tenant system whose records keep the tenant's suppliers in step. An http
/// source reads an http destination (its URL, headers, authentication and secret, through the delivery client's guards);
/// its <see cref="MappingExpression"/> (JSONata, run in the out-of-process renderer) turns the answer into supplier
/// records <c>{name, vatId, taxId, ibans, currency, attributes}</c>. Records match existing suppliers by
/// <see cref="MatchKeys"/> in order (<c>attributes.erpVendorId</c>, then <c>vatId</c>, then <c>iban</c> by default);
/// <see cref="ConflictPolicyJson"/> says per attribute whether the external value wins, the local one, or the difference
/// is flagged. <see cref="ScheduleCron"/> runs it by itself (<c>MasterDataSyncHostedService</c>).
/// </summary>
public sealed class MasterDataSource : ITenantScoped, IVersioned
{
    public const int MaxNameLength = 200;
    public const int MaxMappingLength = 8000;
    public const int MaxCronLength = 100;

    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public int Version { get; set; }

    public required string Name { get; set; }

    /// <summary><see cref="MasterDataTargets"/>.</summary>
    public string Target { get; set; } = MasterDataTargets.Suppliers;

    /// <summary><see cref="MasterDataTransports"/>.</summary>
    public string Transport { get; set; } = MasterDataTransports.Upload;

    /// <summary>The http destination an http source reads; null for an upload source.</summary>
    public Guid? DestinationId { get; set; }

    /// <summary>JSONata from the external records to supplier records; null when they already have that shape.</summary>
    public string? MappingExpression { get; set; }

    /// <summary>The keys records match suppliers by, in order: <c>attributes.erpVendorId</c> (any <c>attributes.&lt;key&gt;</c>), <c>vatId</c>, <c>iban</c>.</summary>
    public List<string> MatchKeys { get; set; } = [];

    /// <summary>The conflict policy per attribute as a JSON object (<c>{"name": "localWins", "attributes.erpVendorId": "externalWins", "*": "flag"}</c>).</summary>
    public string? ConflictPolicyJson { get; set; }

    /// <summary>A 5-field cron expression in UTC, optionally after <c>CRON_TZ=&lt;zone&gt; </c>; null runs it only on request.</summary>
    public string? ScheduleCron { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTimeOffset? NextRunAt { get; set; }

    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>The counts of the last run (<c>{status, created, updated, unchanged, conflicts, errors, runId}</c>).</summary>
    public string? LastResultJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>How a master-data run started.</summary>
public enum MasterDataTrigger
{
    /// <summary>Someone asked (<c>:run</c>, <c>:dry-run</c>).</summary>
    Manual,

    /// <summary>The source's schedule.</summary>
    Schedule,

    /// <summary>A file uploaded to <c>POST /suppliers:import</c>.</summary>
    Import,
}

public enum MasterDataRunStatus
{
    Succeeded,

    /// <summary>The records could not be read (the source did not answer, the mapping failed, the file did not parse); nothing changed.</summary>
    Failed,
}

/// <summary>
/// One run of a master-data import or sync (docs/11 §6.8 <c>MasterDataRun</c>): the counts and the report (per record the
/// action, the supplier, what changed and the conflicts), stored with the run instead of as a blob. A dry run changes
/// nothing; an applied run's changes committed with it.
/// </summary>
public sealed class MasterDataRun : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The source; null for an upload to <c>POST /suppliers:import</c>.</summary>
    public Guid? SourceId { get; set; }

    public MasterDataTrigger Trigger { get; set; }

    public bool DryRun { get; set; }

    public MasterDataRunStatus Status { get; set; }

    public int Created { get; set; }

    public int Updated { get; set; }

    public int Unchanged { get; set; }

    public int Conflicts { get; set; }

    public int Errors { get; set; }

    /// <summary>The report as a JSON object (<c>MasterDataReport</c>).</summary>
    public required string ReportJson { get; set; }

    public DateTimeOffset At { get; set; }

    public required string By { get; set; }
}
