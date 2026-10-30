using System;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GlobalSuppliers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    VatIds = table.Column<string>(type: "json", nullable: false),
                    EmailDomains = table.Column<string>(type: "json", nullable: false),
                    CountryCode = table.Column<string>(type: "nchar(2)", fixedLength: true, maxLength: 2, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlobalSuppliers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LlmUsage",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Stage = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ConfigId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KeyOwner = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PromptVersion = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SchemaVersion = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    InputTokens = table.Column<int>(type: "int", nullable: false),
                    OutputTokens = table.Column<int>(type: "int", nullable: false),
                    CacheReadTokens = table.Column<int>(type: "int", nullable: false),
                    CacheWriteTokens = table.Column<int>(type: "int", nullable: false),
                    ReasoningTokens = table.Column<int>(type: "int", nullable: false),
                    EstimatedCostUsd = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    LatencyMs = table.Column<int>(type: "int", nullable: false),
                    StopReason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProviderRequestId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RequestHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlmUsage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LegalName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    VatId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    NameAliases = table.Column<string>(type: "json", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DefaultCurrency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    CountryCode = table.Column<string>(type: "nchar(2)", fixedLength: true, maxLength: 2, nullable: false),
                    TimeZone = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Settings = table.Column<string>(type: "json", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Prefix = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Hash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Scopes = table.Column<string>(type: "json", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiKeys_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IngestionChannels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ConfigJson = table.Column<string>(type: "json", nullable: false),
                    SecretRef = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Cursor = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LastPolledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestionChannels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestionChannels_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LlmProviderConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Endpoint = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ApiVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    AuthType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SecretRef = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    KeyLast4 = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: true),
                    KeyFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Models = table.Column<string>(type: "json", nullable: false),
                    MonthlySpendCapUsd = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    MaxConcurrency = table.Column<int>(type: "int", nullable: false),
                    MaxRequestsPerMinute = table.Column<int>(type: "int", nullable: false),
                    DataResidency = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AllowBatch = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ValidatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlmProviderConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LlmProviderConfigs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Suppliers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    VatId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    TaxId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Ibans = table.Column<string>(type: "json", nullable: false),
                    EmailDomains = table.Column<string>(type: "json", nullable: false),
                    Aliases = table.Column<string>(type: "json", nullable: false),
                    AddressesJson = table.Column<string>(type: "json", nullable: true),
                    DefaultCurrency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: true),
                    AlwaysReview = table.Column<bool>(type: "bit", nullable: false),
                    GlobalSupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suppliers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Suppliers_GlobalSuppliers_GlobalSupplierId",
                        column: x => x.GlobalSupplierId,
                        principalTable: "GlobalSuppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Suppliers_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TenantMembers",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Role = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantMembers", x => new { x.TenantId, x.UserId });
                    table.ForeignKey(
                        name: "FK_TenantMembers_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Ingestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SourceMessageId = table.Column<string>(type: "nvarchar(998)", maxLength: 998, nullable: true),
                    SourceFrom = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    SourceSubject = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SourceFileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RawBlob_Container = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    RawBlob_ContentType = table.Column<string>(type: "nvarchar(127)", maxLength: 127, nullable: false),
                    RawBlob_Key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    RawBlob_Sha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    RawBlob_SizeBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ingestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Ingestions_IngestionChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "IngestionChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Ingestions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TenantLlmPolicies",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DefaultConfigId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StageOverrides = table.Column<string>(type: "json", nullable: false),
                    FallbackToPlatformKey = table.Column<bool>(type: "bit", nullable: false),
                    AllowPlatformKeyAtAll = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantLlmPolicies", x => x.TenantId);
                    table.ForeignKey(
                        name: "FK_TenantLlmPolicies_LlmProviderConfigs_DefaultConfigId",
                        column: x => x.DefaultConfigId,
                        principalTable: "LlmProviderConfigs",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TenantLlmPolicies_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExtractorProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GlobalSupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    DocumentType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FingerprintJson = table.Column<string>(type: "json", nullable: false),
                    RulesJson = table.Column<string>(type: "json", nullable: false),
                    RulesSchemaVersion = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    Successes = table.Column<int>(type: "int", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastFailureAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SuccessRate30d = table.Column<double>(type: "float", nullable: true),
                    CreatedFromDocumentIds = table.Column<string>(type: "json", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ApprovedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PeriodEnd = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    PeriodStart = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtractorProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExtractorProfiles_GlobalSuppliers_GlobalSupplierId",
                        column: x => x.GlobalSupplierId,
                        principalTable: "GlobalSuppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ExtractorProfiles_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "ExtractorProfilesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", null)
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "PeriodEnd")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "PeriodStart");

            migrationBuilder.CreateTable(
                name: "IngestionItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IngestionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    MimeType = table.Column<string>(type: "nvarchar(127)", maxLength: 127, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Disposition = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DispositionReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DuplicateOfItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Blob_Container = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    Blob_ContentType = table.Column<string>(type: "nvarchar(127)", maxLength: 127, nullable: false),
                    Blob_Key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Blob_Sha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Blob_SizeBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestionItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestionItems_IngestionItems_DuplicateOfItemId",
                        column: x => x.DuplicateOfItemId,
                        principalTable: "IngestionItems",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_IngestionItems_IngestionItems_ParentItemId",
                        column: x => x.ParentItemId,
                        principalTable: "IngestionItems",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_IngestionItems_Ingestions_IngestionId",
                        column: x => x.IngestionId,
                        principalTable: "Ingestions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IngestionItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PageStart = table.Column<int>(type: "int", nullable: false),
                    PageEnd = table.Column<int>(type: "int", nullable: false),
                    PageCount = table.Column<int>(type: "int", nullable: false),
                    PageImageKeys = table.Column<string>(type: "json", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TypeConfidence = table.Column<double>(type: "float", nullable: true),
                    TypeSource = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupplierMatchConfidence = table.Column<double>(type: "float", nullable: true),
                    ExtractorProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExtractionMethod = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CanonicalJson = table.Column<string>(type: "json", nullable: true),
                    CanonicalSchemaVersion = table.Column<int>(type: "int", nullable: true),
                    DocumentNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DocumentNumberNormalized = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IssueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Currency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: true),
                    TotalGross = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ApprovedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PeriodEnd = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    PeriodStart = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    NormalizedBlob_Container = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: true),
                    NormalizedBlob_ContentType = table.Column<string>(type: "nvarchar(127)", maxLength: 127, nullable: true),
                    NormalizedBlob_Key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    NormalizedBlob_Sha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    NormalizedBlob_SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    TextLayerBlob_Container = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: true),
                    TextLayerBlob_ContentType = table.Column<string>(type: "nvarchar(127)", maxLength: 127, nullable: true),
                    TextLayerBlob_Key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    TextLayerBlob_Sha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    TextLayerBlob_SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Costs = table.Column<string>(type: "json", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Documents_ExtractorProfiles_ExtractorProfileId",
                        column: x => x.ExtractorProfileId,
                        principalTable: "ExtractorProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Documents_IngestionItems_IngestionItemId",
                        column: x => x.IngestionItemId,
                        principalTable: "IngestionItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Documents_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "DocumentsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", null)
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "PeriodEnd")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "PeriodStart");

            migrationBuilder.CreateTable(
                name: "Corrections",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FieldPath = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OldValueJson = table.Column<string>(type: "json", nullable: true),
                    NewValueJson = table.Column<string>(type: "json", nullable: true),
                    By = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Corrections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Corrections_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DocumentEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FromStatus = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ToStatus = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DetailsJson = table.Column<string>(type: "json", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentEvents_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DocumentFlags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Field = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolvedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentFlags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentFlags_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReviewTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    AssignedTo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SlaDueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReviewTasks_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_Hash",
                table: "ApiKeys",
                column: "Hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_TenantId",
                table: "ApiKeys",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Corrections_DocumentId_At",
                table: "Corrections",
                columns: new[] { "DocumentId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_Corrections_TenantId",
                table: "Corrections",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentEvents_DocumentId_At",
                table: "DocumentEvents",
                columns: new[] { "DocumentId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentEvents_TenantId",
                table: "DocumentEvents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentFlags_DocumentId_ResolvedAt",
                table: "DocumentFlags",
                columns: new[] { "DocumentId", "ResolvedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentFlags_TenantId",
                table: "DocumentFlags",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentFlags_TenantId_Code",
                table: "DocumentFlags",
                columns: new[] { "TenantId", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_ExtractorProfileId",
                table: "Documents",
                column: "ExtractorProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_IngestionItemId",
                table: "Documents",
                column: "IngestionItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_SupplierId",
                table: "Documents",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_TenantId",
                table: "Documents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_TenantId_Status",
                table: "Documents",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_TenantId_SupplierId_DocumentNumberNormalized",
                table: "Documents",
                columns: new[] { "TenantId", "SupplierId", "DocumentNumberNormalized" });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_TenantId_Type_IssueDate",
                table: "Documents",
                columns: new[] { "TenantId", "Type", "IssueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ExtractorProfiles_GlobalSupplierId_Status",
                table: "ExtractorProfiles",
                columns: new[] { "GlobalSupplierId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ExtractorProfiles_SupplierId_Status",
                table: "ExtractorProfiles",
                columns: new[] { "SupplierId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_IngestionChannels_TenantId",
                table: "IngestionChannels",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionItems_DuplicateOfItemId",
                table: "IngestionItems",
                column: "DuplicateOfItemId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionItems_IngestionId",
                table: "IngestionItems",
                column: "IngestionId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionItems_ParentItemId",
                table: "IngestionItems",
                column: "ParentItemId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionItems_TenantId",
                table: "IngestionItems",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionItems_TenantId_Sha256",
                table: "IngestionItems",
                columns: new[] { "TenantId", "Sha256" });

            migrationBuilder.CreateIndex(
                name: "IX_Ingestions_ChannelId",
                table: "Ingestions",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_Ingestions_TenantId",
                table: "Ingestions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Ingestions_TenantId_ReceivedAt",
                table: "Ingestions",
                columns: new[] { "TenantId", "ReceivedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_Ingestions_TenantId_SourceMessageId",
                table: "Ingestions",
                columns: new[] { "TenantId", "SourceMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_LlmProviderConfigs_TenantId_Status",
                table: "LlmProviderConfigs",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LlmUsage_ConfigId_At",
                table: "LlmUsage",
                columns: new[] { "ConfigId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_LlmUsage_DocumentId",
                table: "LlmUsage",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_LlmUsage_RequestHash",
                table: "LlmUsage",
                column: "RequestHash");

            migrationBuilder.CreateIndex(
                name: "IX_LlmUsage_TenantId",
                table: "LlmUsage",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LlmUsage_TenantId_At",
                table: "LlmUsage",
                columns: new[] { "TenantId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewTasks_DocumentId",
                table: "ReviewTasks",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ReviewTasks_TenantId",
                table: "ReviewTasks",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ReviewTasks_TenantId_Status_CreatedAt",
                table: "ReviewTasks",
                columns: new[] { "TenantId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_GlobalSupplierId",
                table: "Suppliers",
                column: "GlobalSupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_TenantId",
                table: "Suppliers",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_TenantId_NormalizedName",
                table: "Suppliers",
                columns: new[] { "TenantId", "NormalizedName" });

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_TenantId_VatId",
                table: "Suppliers",
                columns: new[] { "TenantId", "VatId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLlmPolicies_DefaultConfigId",
                table: "TenantLlmPolicies",
                column: "DefaultConfigId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantLlmPolicies_TenantId",
                table: "TenantLlmPolicies",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantMembers_TenantId",
                table: "TenantMembers",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Slug",
                table: "Tenants",
                column: "Slug",
                unique: true);

            // Second tenancy layer (docs/01): row-level security keyed on SESSION_CONTEXT.
            migrationBuilder.Sql(RowLevelSecurity.CreateSql());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DropSql());

            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropTable(
                name: "Corrections");

            migrationBuilder.DropTable(
                name: "DocumentEvents");

            migrationBuilder.DropTable(
                name: "DocumentFlags");

            migrationBuilder.DropTable(
                name: "LlmUsage");

            migrationBuilder.DropTable(
                name: "ReviewTasks");

            migrationBuilder.DropTable(
                name: "TenantLlmPolicies");

            migrationBuilder.DropTable(
                name: "TenantMembers");

            migrationBuilder.DropTable(
                name: "Documents")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "DocumentsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", null)
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "PeriodEnd")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "PeriodStart");

            migrationBuilder.DropTable(
                name: "LlmProviderConfigs");

            migrationBuilder.DropTable(
                name: "ExtractorProfiles")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "ExtractorProfilesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", null)
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "PeriodEnd")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "PeriodStart");

            migrationBuilder.DropTable(
                name: "IngestionItems");

            migrationBuilder.DropTable(
                name: "Suppliers");

            migrationBuilder.DropTable(
                name: "Ingestions");

            migrationBuilder.DropTable(
                name: "GlobalSuppliers");

            migrationBuilder.DropTable(
                name: "IngestionChannels");

            migrationBuilder.DropTable(
                name: "Tenants");
        }
    }
}
