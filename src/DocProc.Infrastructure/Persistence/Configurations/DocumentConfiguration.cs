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
        b.HasOne(d => d.Supplier).WithMany().HasForeignKey(d => d.SupplierId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne(d => d.ExtractorProfile).WithMany().HasForeignKey(d => d.ExtractorProfileId).OnDelete(DeleteBehavior.SetNull);
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
        b.Property(c => c.OldValueJson).HasColumnType("json");
        b.Property(c => c.NewValueJson).HasColumnType("json");
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
        // The delivery job scans undelivered rows oldest first; the document index serves the timeline and idempotent publishing.
        b.HasIndex(m => new { m.DeliveredAt, m.CreatedAt });
        b.HasIndex(m => new { m.DocumentId, m.EventType });
        b.HasOne(m => m.Document).WithMany().HasForeignKey(m => m.DocumentId);
    }
}
