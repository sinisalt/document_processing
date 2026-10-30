using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocProc.Infrastructure.Persistence.Configurations;

internal static class BlobReferenceMapping
{
    public static void Configure(ComplexPropertyBuilder<BlobReference> b)
    {
        b.Property(r => r.Container).HasMaxLength(63);
        b.Property(r => r.Key).HasMaxLength(400);
        b.Property(r => r.Sha256).HasMaxLength(64).IsFixedLength();
        b.Property(r => r.ContentType).HasMaxLength(127);
    }
}

internal sealed class IngestionConfiguration : IEntityTypeConfiguration<Ingestion>
{
    public void Configure(EntityTypeBuilder<Ingestion> b)
    {
        b.ToTable("Ingestions");
        b.Property(i => i.Version).IsConcurrencyToken();
        b.Property(i => i.SourceMessageId).HasMaxLength(998);
        b.Property(i => i.SourceFrom).HasMaxLength(320);
        b.Property(i => i.SourceSubject).HasMaxLength(1000);
        b.Property(i => i.SourceFileName).HasMaxLength(260);
        b.Property(i => i.LastError).HasMaxLength(2000);
        b.ComplexProperty(i => i.RawBlob, BlobReferenceMapping.Configure);
        b.HasIndex(i => new { i.TenantId, i.ReceivedAt }).IsDescending(false, true);
        b.HasIndex(i => new { i.TenantId, i.SourceMessageId });
        b.HasOne(i => i.Tenant).WithMany().HasForeignKey(i => i.TenantId);
        b.HasOne(i => i.Channel).WithMany().HasForeignKey(i => i.ChannelId);
    }
}

internal sealed class IngestionItemConfiguration : IEntityTypeConfiguration<IngestionItem>
{
    public void Configure(EntityTypeBuilder<IngestionItem> b)
    {
        b.ToTable("IngestionItems");
        b.Property(i => i.FileName).HasMaxLength(260);
        b.Property(i => i.MimeType).HasMaxLength(127);
        b.Property(i => i.Sha256).HasMaxLength(64).IsFixedLength();
        b.Property(i => i.DispositionReason).HasMaxLength(500);
        b.ComplexProperty(i => i.Blob, BlobReferenceMapping.Configure);
        // Exact duplicates: same tenant, same bytes (docs/03 stage 1).
        b.HasIndex(i => new { i.TenantId, i.Sha256 });
        b.HasOne(i => i.Ingestion).WithMany(i => i.Items).HasForeignKey(i => i.IngestionId);
        b.HasOne<IngestionItem>().WithMany().HasForeignKey(i => i.ParentItemId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<IngestionItem>().WithMany().HasForeignKey(i => i.DuplicateOfItemId).OnDelete(DeleteBehavior.NoAction);
    }
}
