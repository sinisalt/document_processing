using System;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocProc.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// P4 part A (docs/11 §6.3 to §6.5): the source facts of an ingestion (recipients, mailbox, folder path, sender
    /// authentication, forwarder, hints), the per-item route, the channel's source key and defaults, the supplier's
    /// group and settings, the routed rule and receiving entity of a document, and the new tables
    /// <c>SupplierGroups</c>, <c>ReceivingEntities</c>, <c>SupplierEntityAttributes</c> and <c>SupplierSourceRules</c>
    /// under the strict predicate of the re-created policy <see cref="RowLevelSecurity.PolicyV8"/>. A polled message or
    /// file is unique per channel (<c>IX_Ingestions_ChannelId_SourceMessageId</c>, which replaces the channel index).
    /// No data moves: every new column is nullable or has its default, and no existing row sets SourceMessageId.
    /// </summary>
    public partial class SourceRoutingAndSupplierConfig : Migration
    {
        // Frozen: the stored enum names of P4 that the Down maps back (renaming the members later must not change them).
        private const string SupplierTypeSource = "Supplier";
        private const string PinnedTypeSource = "Rule";
        private const string SourceMethods = "N'SourceRule', N'ChannelDefault', N'Hint'";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Ingestions_ChannelId",
                table: "Ingestions");

            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "Suppliers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SettingsJson",
                table: "Suppliers",
                type: "json",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentTypeHint",
                table: "Ingestions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ForwardedBy",
                table: "Ingestions",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SenderAuthJson",
                table: "Ingestions",
                type: "json",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceFolderPath",
                table: "Ingestions",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceMailbox",
                table: "Ingestions",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceRecipientsJson",
                table: "Ingestions",
                type: "json",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupplierIdHint",
                table: "Ingestions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RouteJson",
                table: "IngestionItems",
                type: "json",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultDocumentTypeKey",
                table: "IngestionChannels",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DefaultReceivingEntityId",
                table: "IngestionChannels",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DefaultSupplierId",
                table: "IngestionChannels",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceKey",
                table: "IngestionChannels",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReceivingEntityId",
                table: "Documents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupplierSourceRuleId",
                table: "Documents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReceivingEntities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    VatIds = table.Column<string>(type: "json", nullable: false),
                    AddressesJson = table.Column<string>(type: "json", nullable: true),
                    AttributesJson = table.Column<string>(type: "json", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceivingEntities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReceivingEntities_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupplierGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AttributesJson = table.Column<string>(type: "json", nullable: true),
                    SettingsJson = table.Column<string>(type: "json", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierGroups_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupplierEntityAttributes",
                columns: table => new
                {
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceivingEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttributesJson = table.Column<string>(type: "json", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierEntityAttributes", x => new { x.SupplierId, x.ReceivingEntityId });
                    table.ForeignKey(
                        name: "FK_SupplierEntityAttributes_ReceivingEntities_ReceivingEntityId",
                        column: x => x.ReceivingEntityId,
                        principalTable: "ReceivingEntities",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SupplierEntityAttributes_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SupplierEntityAttributes_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SupplierSourceRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Recipient = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Sender = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    SenderDomain = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: true),
                    FolderPrefix = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    SubjectRegex = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    FileNameRegex = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Mode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RequireSenderAuth = table.Column<bool>(type: "bit", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    PinTypeKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ReceivingEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedFromDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConditionHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierSourceRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierSourceRules_IngestionChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "IngestionChannels",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SupplierSourceRules_ReceivingEntities_ReceivingEntityId",
                        column: x => x.ReceivingEntityId,
                        principalTable: "ReceivingEntities",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SupplierSourceRules_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SupplierSourceRules_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_GroupId",
                table: "Suppliers",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Ingestions_ChannelId_SourceMessageId",
                table: "Ingestions",
                columns: new[] { "ChannelId", "SourceMessageId" },
                unique: true,
                filter: "[SourceMessageId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionChannels_DefaultReceivingEntityId",
                table: "IngestionChannels",
                column: "DefaultReceivingEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionChannels_DefaultSupplierId",
                table: "IngestionChannels",
                column: "DefaultSupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionChannels_Type_SourceKey",
                table: "IngestionChannels",
                columns: new[] { "Type", "SourceKey" },
                unique: true,
                filter: "[SourceKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_ReceivingEntityId",
                table: "Documents",
                column: "ReceivingEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_SupplierSourceRuleId",
                table: "Documents",
                column: "SupplierSourceRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivingEntities_TenantId",
                table: "ReceivingEntities",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivingEntities_TenantId_Code",
                table: "ReceivingEntities",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierEntityAttributes_ReceivingEntityId",
                table: "SupplierEntityAttributes",
                column: "ReceivingEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierEntityAttributes_TenantId",
                table: "SupplierEntityAttributes",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierGroups_TenantId",
                table: "SupplierGroups",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierGroups_TenantId_Name",
                table: "SupplierGroups",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSourceRules_ChannelId",
                table: "SupplierSourceRules",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSourceRules_ReceivingEntityId",
                table: "SupplierSourceRules",
                column: "ReceivingEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSourceRules_SupplierId",
                table: "SupplierSourceRules",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSourceRules_TenantId",
                table: "SupplierSourceRules",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSourceRules_TenantId_ChannelId_FolderPrefix",
                table: "SupplierSourceRules",
                columns: new[] { "TenantId", "ChannelId", "FolderPrefix" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSourceRules_TenantId_ConditionHash",
                table: "SupplierSourceRules",
                columns: new[] { "TenantId", "ConditionHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSourceRules_TenantId_Recipient",
                table: "SupplierSourceRules",
                columns: new[] { "TenantId", "Recipient" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSourceRules_TenantId_Sender",
                table: "SupplierSourceRules",
                columns: new[] { "TenantId", "Sender" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierSourceRules_TenantId_SenderDomain",
                table: "SupplierSourceRules",
                columns: new[] { "TenantId", "SenderDomain" });

            migrationBuilder.AddForeignKey(
                name: "FK_Documents_ReceivingEntities_ReceivingEntityId",
                table: "Documents",
                column: "ReceivingEntityId",
                principalTable: "ReceivingEntities",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_IngestionChannels_ReceivingEntities_DefaultReceivingEntityId",
                table: "IngestionChannels",
                column: "DefaultReceivingEntityId",
                principalTable: "ReceivingEntities",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_IngestionChannels_Suppliers_DefaultSupplierId",
                table: "IngestionChannels",
                column: "DefaultSupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Suppliers_SupplierGroups_GroupId",
                table: "Suppliers",
                column: "GroupId",
                principalTable: "SupplierGroups",
                principalColumn: "Id");

            // The schema-bound policy is re-created over the new tables.
            migrationBuilder.Sql(RowLevelSecurity.DropSql());
            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV8));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The schema-bound policy names the new tables, so it is dropped first and the earlier policy created after.
            migrationBuilder.Sql(RowLevelSecurity.DropSql());

            // The build before P4 reads neither the Supplier type source nor the source match methods: a pinned type
            // becomes a rule's and a routed supplier's method unknown (the supplier stays), in the table and in its
            // history, which only the system writes, so system versioning is off for that one statement.
            migrationBuilder.Sql(RowLevelSecurity.InPlatformScope(
                $"UPDATE [Documents] SET [TypeSource] = N'{PinnedTypeSource}' WHERE [TypeSource] = N'{SupplierTypeSource}'; " +
                $"UPDATE [Documents] SET [SupplierMatchMethod] = NULL WHERE [SupplierMatchMethod] IN ({SourceMethods});"));
            migrationBuilder.Sql("ALTER TABLE [Documents] SET (SYSTEM_VERSIONING = OFF);");
            migrationBuilder.Sql(RowLevelSecurity.InPlatformScope(
                $"UPDATE [DocumentsHistory] SET [TypeSource] = N'{PinnedTypeSource}' WHERE [TypeSource] = N'{SupplierTypeSource}'; " +
                $"UPDATE [DocumentsHistory] SET [SupplierMatchMethod] = NULL WHERE [SupplierMatchMethod] IN ({SourceMethods});"));
            migrationBuilder.Sql("ALTER TABLE [Documents] SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [dbo].[DocumentsHistory]));");

            migrationBuilder.DropForeignKey(
                name: "FK_Documents_ReceivingEntities_ReceivingEntityId",
                table: "Documents");

            migrationBuilder.DropForeignKey(
                name: "FK_IngestionChannels_ReceivingEntities_DefaultReceivingEntityId",
                table: "IngestionChannels");

            migrationBuilder.DropForeignKey(
                name: "FK_IngestionChannels_Suppliers_DefaultSupplierId",
                table: "IngestionChannels");

            migrationBuilder.DropForeignKey(
                name: "FK_Suppliers_SupplierGroups_GroupId",
                table: "Suppliers");

            migrationBuilder.DropTable(
                name: "SupplierEntityAttributes");

            migrationBuilder.DropTable(
                name: "SupplierGroups");

            migrationBuilder.DropTable(
                name: "SupplierSourceRules");

            migrationBuilder.DropTable(
                name: "ReceivingEntities");

            migrationBuilder.DropIndex(
                name: "IX_Suppliers_GroupId",
                table: "Suppliers");

            migrationBuilder.DropIndex(
                name: "IX_Ingestions_ChannelId_SourceMessageId",
                table: "Ingestions");

            migrationBuilder.DropIndex(
                name: "IX_IngestionChannels_DefaultReceivingEntityId",
                table: "IngestionChannels");

            migrationBuilder.DropIndex(
                name: "IX_IngestionChannels_DefaultSupplierId",
                table: "IngestionChannels");

            migrationBuilder.DropIndex(
                name: "IX_IngestionChannels_Type_SourceKey",
                table: "IngestionChannels");

            migrationBuilder.DropIndex(
                name: "IX_Documents_ReceivingEntityId",
                table: "Documents");

            migrationBuilder.DropIndex(
                name: "IX_Documents_SupplierSourceRuleId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "SettingsJson",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "DocumentTypeHint",
                table: "Ingestions");

            migrationBuilder.DropColumn(
                name: "ForwardedBy",
                table: "Ingestions");

            migrationBuilder.DropColumn(
                name: "SenderAuthJson",
                table: "Ingestions");

            migrationBuilder.DropColumn(
                name: "SourceFolderPath",
                table: "Ingestions");

            migrationBuilder.DropColumn(
                name: "SourceMailbox",
                table: "Ingestions");

            migrationBuilder.DropColumn(
                name: "SourceRecipientsJson",
                table: "Ingestions");

            migrationBuilder.DropColumn(
                name: "SupplierIdHint",
                table: "Ingestions");

            migrationBuilder.DropColumn(
                name: "RouteJson",
                table: "IngestionItems");

            migrationBuilder.DropColumn(
                name: "DefaultDocumentTypeKey",
                table: "IngestionChannels");

            migrationBuilder.DropColumn(
                name: "DefaultReceivingEntityId",
                table: "IngestionChannels");

            migrationBuilder.DropColumn(
                name: "DefaultSupplierId",
                table: "IngestionChannels");

            migrationBuilder.DropColumn(
                name: "SourceKey",
                table: "IngestionChannels");

            migrationBuilder.DropColumn(
                name: "ReceivingEntityId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "SupplierSourceRuleId",
                table: "Documents");

            migrationBuilder.CreateIndex(
                name: "IX_Ingestions_ChannelId",
                table: "Ingestions",
                column: "ChannelId");

            migrationBuilder.Sql(RowLevelSecurity.CreateSql(RowLevelSecurity.PolicyV7));
        }
    }
}
