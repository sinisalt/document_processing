using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocProc.Infrastructure.Persistence.Configurations;

internal sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> b)
    {
        b.ToTable("Documents", t => t.IsTemporal());
        b.Property(d => d.Version).IsConcurrencyToken();
        // A type key is a string (docs/11 §6.2); the column is the nvarchar(64) the enum was stored in, so no data changes.
        b.Property(d => d.Type).HasConversion(v => v.Value, v => DocumentTypeKey.FromStored(v)).HasMaxLength(DocumentTypeKey.MaxLength);
        b.ComplexProperty(d => d.NormalizedBlob, BlobReferenceMapping.Configure);
        b.ComplexProperty(d => d.TextLayerBlob, BlobReferenceMapping.Configure);
        b.ComplexProperty(d => d.EmbeddedXmlBlob, BlobReferenceMapping.Configure);
        b.Property(d => d.FailureReason).HasMaxLength(2000);
        b.Property(d => d.CanonicalJson).HasColumnType("json");
        b.Property(d => d.DocumentNumber).HasMaxLength(100);
        b.Property(d => d.DocumentNumberNormalized).HasMaxLength(100);
        b.Property(d => d.Currency).HasMaxLength(3).IsFixedLength();
        b.Property(d => d.TotalGross).HasPrecision(18, 4);
        b.Property(d => d.ApprovedBy).HasMaxLength(200);
        b.Property(d => d.AppliedConfigJson).HasColumnType("json");
        b.OwnsOne(d => d.Costs, c =>
        {
            c.ToJson();
            c.Property(x => x.EstimatedUsd).HasPrecision(18, 6);
        });

        b.HasIndex(d => new { d.TenantId, d.Status });
        b.HasIndex(d => new { d.TenantId, d.Type, d.IssueDate });
        // DUPLICATE_SUSPECTED: same supplier and normalised number (docs/02 §5).
        b.HasIndex(d => new { d.TenantId, d.SupplierId, d.DocumentNumberNormalized });
        b.HasIndex(d => d.IngestionItemId);

        b.HasOne(d => d.IngestionItem).WithMany(i => i.Documents).HasForeignKey(d => d.IngestionItemId);
        b.HasOne(d => d.Supplier).WithMany().HasForeignKey(d => d.SupplierId).OnDelete(DeleteBehavior.NoAction);
        // The rule is a trace, not a reference: deleting a rule keeps the documents it routed (their timeline names it).
        b.HasIndex(d => d.SupplierSourceRuleId);
        b.HasOne<ReceivingEntity>().WithMany().HasForeignKey(d => d.ReceivingEntityId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne(d => d.ExtractorProfile).WithMany().HasForeignKey(d => d.ExtractorProfileId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne(d => d.DocumentTypeVersion).WithMany().HasForeignKey(d => d.DocumentTypeVersionId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne(d => d.SupplierLayoutVersion).WithMany().HasForeignKey(d => d.SupplierLayoutVersionId).OnDelete(DeleteBehavior.NoAction);

        // Documents waiting for a Candidate layout (docs/11 §12.10): a hint, not a reference, so a retired layout's rows go freely.
        b.HasIndex(d => new { d.TenantId, d.PendingLayoutVersionId });
        b.HasOne<TypeIdentificationRuleVersion>().WithMany().HasForeignKey(d => d.IdentificationRuleVersionId).OnDelete(DeleteBehavior.NoAction);
        b.Property(d => d.PipelineHash).HasMaxLength(32);
    }
}

internal sealed class DocumentFieldIndexConfiguration : IEntityTypeConfiguration<DocumentFieldIndexEntry>
{
    public void Configure(EntityTypeBuilder<DocumentFieldIndexEntry> b)
    {
        b.ToTable("DocumentFieldIndex");
        b.HasKey(e => new { e.DocumentId, e.FieldKey, e.Ordinal });
        b.Property(e => e.FieldKey).HasMaxLength(DocumentFieldIndexEntry.MaxFieldKeyLength);
        b.Property(e => e.StringValue).HasMaxLength(DocumentFieldIndexEntry.MaxStringLength);
        b.Property(e => e.NumberValue).HasPrecision(28, 8);
        b.HasIndex(e => new { e.TenantId, e.FieldKey, e.StringValue });
        b.HasIndex(e => new { e.TenantId, e.FieldKey, e.DateValue });
        b.HasOne(e => e.Document).WithMany(d => d.FieldIndex).HasForeignKey(e => e.DocumentId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DocumentTableReportConfiguration : IEntityTypeConfiguration<DocumentTableReport>
{
    public void Configure(EntityTypeBuilder<DocumentTableReport> b)
    {
        b.ToTable("DocumentTableReports");
        b.HasKey(r => new { r.DocumentId, r.Path });
        b.Property(r => r.Path).HasMaxLength(DocumentTableReport.MaxPathLength);
        b.Property(r => r.StepId).HasMaxLength(DocumentTableReport.MaxStepIdLength);
        // Always an object, so the native json type takes it.
        b.Property(r => r.ReportJson).HasColumnType("json");
        b.HasOne(r => r.Document).WithMany(d => d.TableReports).HasForeignKey(r => r.DocumentId).OnDelete(DeleteBehavior.Cascade);
        // Corrections are deleted with their document like the report, so this link must not cascade a second path.
        b.HasOne(r => r.Correction).WithMany().HasForeignKey(r => r.CorrectionId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class DocumentFlagConfiguration : IEntityTypeConfiguration<DocumentFlagRecord>
{
    public void Configure(EntityTypeBuilder<DocumentFlagRecord> b)
    {
        b.ToTable("DocumentFlags");
        b.Property(f => f.Code).HasMaxLength(64);
        b.Property(f => f.Field).HasMaxLength(200);
        b.Property(f => f.Message).HasMaxLength(1000);
        b.Property(f => f.ResolvedBy).HasMaxLength(200);
        b.Property(f => f.Params!).AsJson();
        b.HasIndex(f => new { f.DocumentId, f.ResolvedAt });
        b.HasIndex(f => new { f.TenantId, f.Code });
        b.HasOne(f => f.Document).WithMany(d => d.Flags).HasForeignKey(f => f.DocumentId);
    }
}

internal sealed class DocumentEventConfiguration : IEntityTypeConfiguration<DocumentEvent>
{
    public void Configure(EntityTypeBuilder<DocumentEvent> b)
    {
        b.ToTable("DocumentEvents");
        b.Property(e => e.EventType).HasMaxLength(64);
        b.Property(e => e.Actor).HasMaxLength(200);
        b.Property(e => e.DetailsJson).HasColumnType("json");
        b.HasIndex(e => new { e.DocumentId, e.At });
        b.HasOne(e => e.Document).WithMany(d => d.Events).HasForeignKey(e => e.DocumentId);
    }
}

internal sealed class ReviewTaskConfiguration : IEntityTypeConfiguration<ReviewTask>
{
    public void Configure(EntityTypeBuilder<ReviewTask> b)
    {
        b.ToTable("ReviewTasks");
        b.Property(r => r.Version).IsConcurrencyToken();
        b.Property(r => r.Reason).HasMaxLength(1000);
        b.Property(r => r.AssignedTo).HasMaxLength(200);
        b.HasIndex(r => new { r.TenantId, r.Status, r.CreatedAt });
        b.HasIndex(r => r.DocumentId);
        b.HasOne(r => r.Document).WithMany(d => d.ReviewTasks).HasForeignKey(r => r.DocumentId);
    }
}

internal sealed class CorrectionConfiguration : IEntityTypeConfiguration<Correction>
{
    public void Configure(EntityTypeBuilder<Correction> b)
    {
        b.ToTable("Corrections");
        b.Property(c => c.FieldPath).HasMaxLength(200);
        // A field value is any JSON value, usually a bare string, number or null ("2026-11-15"), which SQL Server's json
        // type rejects (it takes an object or array only, error 13609), so these two are plain text: nvarchar(max) on
        // SQL Server, TEXT on SQLite.
        b.Property(c => c.OldValueJson);
        b.Property(c => c.NewValueJson);
        b.Property(c => c.Op).HasMaxLength(16).HasDefaultValue("set");
        // Always an object (or null), so the native json type takes it.
        b.Property(c => c.SourceJson).HasColumnType("json");
        b.Property(c => c.By).HasMaxLength(200);
        b.Property(c => c.Reason).HasMaxLength(500);
        b.HasIndex(c => new { c.DocumentId, c.At });
        b.HasOne(c => c.Document).WithMany(d => d.Corrections).HasForeignKey(c => c.DocumentId);
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("OutboxMessages");
        b.Property(m => m.EventType).HasMaxLength(64);
        b.Property(m => m.PayloadJson).HasColumnType("json");
        b.Property(m => m.FannedOutAt).IsConcurrencyToken();
        // The delivery job scans undelivered rows oldest first; the document index serves the timeline and idempotent publishing.
        b.HasIndex(m => new { m.DeliveredAt, m.CreatedAt });
        b.HasIndex(m => new { m.DocumentId, m.EventType });
        b.HasOne(m => m.Document).WithMany().HasForeignKey(m => m.DocumentId);
    }
}
