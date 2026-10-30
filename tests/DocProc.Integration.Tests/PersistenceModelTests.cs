using DocProc.Domain.Common;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Integration.Tests;

/// <summary>Model-level checks that need the SQL Server provider but no database.</summary>
public class PersistenceModelTests
{
    private static DocProcDbContext NewContext(ITenantContext tenant)
    {
        var options = PersistenceOptions.ConfigureSqlServer(
            new DbContextOptionsBuilder<DocProcDbContext>(),
            "Server=unused;Database=unused;Integrated Security=false;TrustServerCertificate=true",
            migrationsOnly: true);
        return new DocProcDbContext(options.Options, tenant);
    }

    [Fact]
    public void Model_snapshot_is_up_to_date_with_the_entity_model()
    {
        using var context = NewContext(TenantContext.Platform());

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "The entity model changed without a migration. Run: dotnet ef migrations add <Name> --project src/DocProc.Infrastructure --startup-project src/DocProc.Infrastructure --output-dir Persistence/Migrations");
    }

    [Fact]
    public void Every_tenant_scoped_entity_has_a_query_filter_and_a_tenant_index()
    {
        using var context = NewContext(TenantContext.Platform());

        var scoped = context.Model.GetEntityTypes()
            .Where(e => typeof(Infrastructure.Persistence.Entities.ITenantScoped).IsAssignableFrom(e.ClrType) && !e.IsOwned())
            .ToList();

        scoped.ShouldNotBeEmpty();
        foreach (var entity in scoped)
        {
            entity.GetDeclaredQueryFilters().ShouldNotBeEmpty($"{entity.ClrType.Name} has no tenant query filter");
            entity.GetIndexes().ShouldContain(i => i.Properties.Any(p => p.Name == "TenantId"), $"{entity.ClrType.Name} has no index on TenantId");
        }
    }

    [Fact]
    public void Row_level_security_policy_covers_every_tenant_scoped_table()
    {
        using var context = NewContext(TenantContext.Platform());

        var tenantTables = context.Model.GetEntityTypes()
            .Where(e => typeof(Infrastructure.Persistence.Entities.ITenantScoped).IsAssignableFrom(e.ClrType) && !e.IsOwned())
            .Select(e => e.GetTableName()!)
            .Append("Tenants")
            .ToHashSet(StringComparer.Ordinal);

        var protectedTables = RowLevelSecurity.ProtectedTables.Select(t => t.Table).ToHashSet(StringComparer.Ordinal);

        protectedTables.ShouldBe(tenantTables, ignoreOrder: true);

        var sql = RowLevelSecurity.CreateSql(RowLevelSecurity.ProtectedTables);
        sql.ShouldContain("EXEC(N'CREATE FUNCTION");
        sql.ShouldContain("EXEC(N'CREATE SECURITY POLICY");
        sql.ShouldContain("SESSION_CONTEXT(N''TenantId'')");
        foreach (var table in tenantTables)
        {
            sql.ShouldContain($"ON [dbo].[{table}]");
        }

        // The initial migration builds its policy at run time from the frozen list, so that list must never grow.
        RowLevelSecurity.InitialProtectedTables.Count.ShouldBe(14);
        RowLevelSecurity.CreateSql().ShouldNotContain("OutboxMessages");
    }

    [Fact]
    public void Enums_are_stored_as_strings()
    {
        using var context = NewContext(TenantContext.Platform());

        var status = context.Model.FindEntityType(typeof(Infrastructure.Persistence.Entities.Document))!.FindProperty("Status")!;

        status.GetProviderClrType().ShouldBe(typeof(string));
        status.GetMaxLength().ShouldBe(64);
    }

    [Fact]
    public void Canonical_invoice_round_trips_through_json_with_provenance_intact()
    {
        var source = new FieldSource(SourceMethod.Llm, Page: 1, BBox: new BoundingBox(10, 20, 110, 40), RawText: "RE-2026-0042");
        var invoice = new Invoice
        {
            Kind = FieldValue.Extracted(InvoiceKind.Invoice, source, 0.99),
            InvoiceNumber = FieldValue.Extracted(DocumentIdentifier.Create("RE-2026-0042"), source, 0.98)
                .Correct(DocumentIdentifier.Create("RE-2026-0043"), "reviewer@example.com", new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero)),
            IssueDate = FieldValue.Extracted(new DateOnly(2026, 10, 27)),
            Currency = FieldValue.Inferred("EUR"),
            Supplier = FieldValue.Extracted(new Party("METRO Deutschland GmbH", VatId: "DE811177936")),
            SubtotalNet = FieldValue.Extracted(new Money(150m, "EUR")),
            TaxBreakdown = [new TaxBreakdownLine(19m, new Money(150m, "EUR"), new Money(28.5m, "EUR"), TaxCategory.Standard)],
            TotalTax = FieldValue.Extracted(new Money(28.5m, "EUR")),
            TotalGross = FieldValue<Money>.Unreadable(source),
            Lines = [new InvoiceLine { LineNumber = 1, Quantity = FieldValue.Extracted(10m), Unit = FieldValue.Extracted(UnitOfMeasure.Kilogram), UnitRaw = "kg" }],
        };

        var json = CanonicalDocumentSerializer.Serialize(invoice);
        var restored = CanonicalDocumentSerializer.DeserializeInvoice(json);

        restored.InvoiceNumber.Value!.Normalized.ShouldBe("RE-2026-0043");
        restored.InvoiceNumber.Status.ShouldBe(FieldStatus.Corrected);
        restored.InvoiceNumber.History.ShouldHaveSingleItem().Value!.Verbatim.ShouldBe("RE-2026-0042");
        restored.InvoiceNumber.History[0].Source!.BBox.ShouldBe(new BoundingBox(10, 20, 110, 40));
        restored.Kind.Confidence.ShouldBe(0.99);
        restored.Currency.Status.ShouldBe(FieldStatus.Inferred);
        restored.TotalGross.Status.ShouldBe(FieldStatus.Unreadable);
        restored.TotalGross.HasValue.ShouldBeFalse();
        restored.TaxBreakdown.ShouldHaveSingleItem().Category.ShouldBe(TaxCategory.Standard);
        restored.Lines.ShouldHaveSingleItem().Unit.Value.ShouldBe(UnitOfMeasure.Kilogram);
        json.ShouldContain("\"kilogram\"", Case.Insensitive);
        CanonicalDocumentSerializer.Serialize(restored).ShouldBe(json);
    }
}
