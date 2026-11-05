using System;
using DocProc.Infrastructure.Outputs;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// P3 output tables of docs/11 §6.7 and the lookup tables of §6.1: <c>OutputDestinations</c>, <c>OutputMappings</c>
    /// and <c>OutputMappingVersions</c>, <c>OutputBindings</c>, <c>OutboxDeliveries</c>, <c>DeliveryAttempts</c>,
    /// <c>DocumentExternalRefs</c>, <c>LookupTables</c> and <c>LookupEntries</c>, plus <c>Suppliers.AttributesJson</c>
    /// (§6.4, what mappings read as <c>supplier.attributes</c>). The mappings and lookup tables hold platform rows
    /// (TenantId NULL) and go under the platform-row predicates, the rest under the strict one, in the re-created
    /// policy <see cref="RowLevelSecurity.PolicyV7"/>. Seeds the platform output templates (<c>std.event</c>,
    /// <c>generic-ap-json</c>, §13.9) from the copy frozen with this migration.
    /// </summary>
    public partial class OutputsAndLookups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttributesJson",
                table: "Suppliers",
                type: "json",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LookupTables",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CaseSensitive = table.Column<bool>(type: "bit", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LookupTables", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutputDestinations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Transport = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ConfigJson = table.Column<string>(type: "json", nullable: false),
                    SecretRef = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    PreviousSecretRef = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    SecretSetAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CallbackConfigJson = table.Column<string>(type: "json", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LastTestAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastTestResult = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutputDestinations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutputDestinations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OutputMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SourceTypeKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Origin = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BaseMappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BaseVersion = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PublishedVersion = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutputMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutputMappings_OutputMappings_BaseMappingId",
                        column: x => x.BaseMappingId,
                        principalTable: "OutputMappings",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "LookupEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TableId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    From = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    To = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    LabelsJson = table.Column<string>(type: "json", nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LookupEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LookupEntries_LookupTables_TableId",
                        column: x => x.TableId,
                        principalTable: "LookupTables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DocumentExternalRefs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DestinationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentExternalRefs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentExternalRefs_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DocumentExternalRefs_OutputDestinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "OutputDestinations",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OutputBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Events = table.Column<string>(type: "json", nullable: false),
                    DocumentTypeKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChannelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReceivingEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Condition = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    MappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PinnedMappingVersion = table.Column<int>(type: "int", nullable: true),
                    DestinationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeJson = table.Column<string>(type: "json", nullable: true),
                    Mode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BatchJson = table.Column<string>(type: "json", nullable: true),
                    ExclusiveGroup = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    OnReapproval = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UpdateMappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutputBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutputBindings_IngestionChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "IngestionChannels",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OutputBindings_OutputDestinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "OutputDestinations",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OutputBindings_OutputMappings_MappingId",
                        column: x => x.MappingId,
                        principalTable: "OutputMappings",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OutputBindings_OutputMappings_UpdateMappingId",
                        column: x => x.UpdateMappingId,
                        principalTable: "OutputMappings",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OutputBindings_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OutputMappingVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Engine = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Expression = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FileNameTemplate = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    TargetSchemaJson = table.Column<string>(type: "json", nullable: true),
                    ReferencedPathsJson = table.Column<string>(type: "json", nullable: false),
                    SampleDocumentIdsJson = table.Column<string>(type: "json", nullable: false),
                    State = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    ChangeNote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PublishedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutputMappingVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutputMappingVersions_OutputMappings_MappingId",
                        column: x => x.MappingId,
                        principalTable: "OutputMappings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OutboxDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OutboxMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentVersion = table.Column<int>(type: "int", nullable: false),
                    BindingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DestinationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MappingVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RecipeStateJson = table.Column<string>(type: "json", nullable: true),
                    LastStatusCode = table.Column<int>(type: "int", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PayloadBlobKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    PayloadSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutboxDeliveries_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OutboxDeliveries_OutboxMessages_OutboxMessageId",
                        column: x => x.OutboxMessageId,
                        principalTable: "OutboxMessages",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OutboxDeliveries_OutputBindings_BindingId",
                        column: x => x.BindingId,
                        principalTable: "OutputBindings",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OutboxDeliveries_OutputDestinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "OutputDestinations",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OutboxDeliveries_OutputMappingVersions_MappingVersionId",
                        column: x => x.MappingVersionId,
                        principalTable: "OutputMappingVersions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "DeliveryAttempts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Step = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    StatusCode = table.Column<int>(type: "int", nullable: true),
                    ErrorClass = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ResponseExcerpt = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeliveryAttempts_OutboxDeliveries_DeliveryId",
                        column: x => x.DeliveryId,
                        principalTable: "OutboxDeliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryAttempts_DeliveryId_At",
                table: "DeliveryAttempts",
                columns: new[] { "DeliveryId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryAttempts_TenantId",
                table: "DeliveryAttempts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentExternalRefs_DestinationId",
                table: "DocumentExternalRefs",
                column: "DestinationId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentExternalRefs_DocumentId_DestinationId",
                table: "DocumentExternalRefs",
                columns: new[] { "DocumentId", "DestinationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentExternalRefs_TenantId",
                table: "DocumentExternalRefs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LookupEntries_TableId_From",
                table: "LookupEntries",
                columns: new[] { "TableId", "From" });

            migrationBuilder.CreateIndex(
                name: "IX_LookupEntries_TenantId",
                table: "LookupEntries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LookupTables_TenantId",
                table: "LookupTables",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_LookupTables_TenantId_Key",
                table: "LookupTables",
                columns: new[] { "TenantId", "Key" },
                unique: true,
                filter: "[TenantId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_BindingId",
                table: "OutboxDeliveries",
                column: "BindingId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_DestinationId",
                table: "OutboxDeliveries",
                column: "DestinationId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_DocumentId",
                table: "OutboxDeliveries",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_IdempotencyKey",
                table: "OutboxDeliveries",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_MappingVersionId",
                table: "OutboxDeliveries",
                column: "MappingVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_OutboxMessageId",
                table: "OutboxDeliveries",
                column: "OutboxMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_Status_NextAttemptAt",
                table: "OutboxDeliveries",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_TenantId",
                table: "OutboxDeliveries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_OutputBindings_ChannelId",
                table: "OutputBindings",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_OutputBindings_DestinationId",
                table: "OutputBindings",
                column: "DestinationId");

            migrationBuilder.CreateIndex(
                name: "IX_OutputBindings_MappingId",
                table: "OutputBindings",
                column: "MappingId");

            migrationBuilder.CreateIndex(
                name: "IX_OutputBindings_SupplierId",
                table: "OutputBindings",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_OutputBindings_TenantId",
                table: "OutputBindings",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_OutputBindings_TenantId_Enabled",
                table: "OutputBindings",
                columns: new[] { "TenantId", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_OutputBindings_TenantId_Name",
                table: "OutputBindings",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutputBindings_UpdateMappingId",
                table: "OutputBindings",
                column: "UpdateMappingId");

            migrationBuilder.CreateIndex(
                name: "IX_OutputDestinations_TenantId",
                table: "OutputDestinations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_OutputDestinations_TenantId_Name",
                table: "OutputDestinations",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutputMappingVersions_MappingId_Version",
                table: "OutputMappingVersions",
                columns: new[] { "MappingId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutputMappingVersions_TenantId",
                table: "OutputMappingVersions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_OutputMappings_BaseMappingId",
                table: "OutputMappings",
                column: "BaseMappingId");

            migrationBuilder.CreateIndex(
                name: "IX_OutputMappings_TenantId",
                table: "OutputMappings",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_OutputMappings_TenantId_Key",
                table: "OutputMappings",
                columns: new[] { "TenantId", "Key" },
                unique: true,
                filter: "[TenantId] IS NOT NULL");

            // The seed runs with the policy down and in platform scope (every data statement through EXEC, so the
            // idempotent script compiles); then the policy is re-created with the new tables.
            migrationBuilder.Sql(RowLevelSecurity.DropSql());
            foreach (var statement in OutputTemplates.SeedStatements())
            {
                migrationBuilder.Sql(RowLevelSecurity.InPlatformScope(statement));
            }

            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV7));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The schema-bound policy names the new tables, so it is dropped first and the earlier policy created after.
            migrationBuilder.Sql(RowLevelSecurity.DropSql());

            migrationBuilder.DropTable(
                name: "DeliveryAttempts");

            migrationBuilder.DropTable(
                name: "DocumentExternalRefs");

            migrationBuilder.DropTable(
                name: "LookupEntries");

            migrationBuilder.DropTable(
                name: "OutboxDeliveries");

            migrationBuilder.DropTable(
                name: "LookupTables");

            migrationBuilder.DropTable(
                name: "OutputBindings");

            migrationBuilder.DropTable(
                name: "OutputMappingVersions");

            migrationBuilder.DropTable(
                name: "OutputDestinations");

            migrationBuilder.DropTable(
                name: "OutputMappings");

            migrationBuilder.DropColumn(
                name: "AttributesJson",
                table: "Suppliers");

            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV6));
        }
    }
}
