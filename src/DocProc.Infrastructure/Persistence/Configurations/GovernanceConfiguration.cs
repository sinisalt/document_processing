using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocProc.Infrastructure.Persistence.Configurations;

internal sealed class GoldenDocumentConfiguration : IEntityTypeConfiguration<GoldenDocument>
{
    public void Configure(EntityTypeBuilder<GoldenDocument> b)
    {
        b.ToTable("GoldenDocuments");
        b.Property(g => g.TypeKey).HasMaxLength(64);
        b.Property(g => g.ExpectedValuesJson).HasColumnType("json");
        b.Property(g => g.ExpectedOutputsJson).HasColumnType("json");
        b.Property(g => g.Note).HasMaxLength(GoldenDocument.MaxNoteLength);
        b.Property(g => g.AddedBy).HasMaxLength(200);

        // A document is golden once; the replay set reads them by supplier and type.
        b.HasIndex(g => new { g.TenantId, g.DocumentId }).IsUnique();
        b.HasIndex(g => new { g.TenantId, g.SupplierId, g.TypeKey });
        b.HasOne(g => g.Document).WithMany().HasForeignKey(g => g.DocumentId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Tenant>().WithMany().HasForeignKey(g => g.TenantId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class RegressionRunConfiguration : IEntityTypeConfiguration<RegressionRun>
{
    public void Configure(EntityTypeBuilder<RegressionRun> b)
    {
        b.ToTable("RegressionRuns");
        b.Property(r => r.Trigger).HasMaxLength(32);
        b.Property(r => r.CandidateConfigJson).HasColumnType("json");
        b.Property(r => r.ResultJson).HasColumnType("json");
        b.Property(r => r.CreatedBy).HasMaxLength(200);
        b.HasIndex(r => new { r.TenantId, r.CreatedAt });
        b.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.ToTable("Notifications");
        b.Property(n => n.Audience).HasMaxLength(64);
        b.Property(n => n.Kind).HasMaxLength(64);
        b.Property(n => n.Title).HasMaxLength(Notification.MaxTitleLength);
        b.Property(n => n.Message).HasMaxLength(Notification.MaxMessageLength);
        b.Property(n => n.EntityType).HasMaxLength(64);
        b.Property(n => n.DedupeKey).HasMaxLength(200);
        b.Property(n => n.PayloadJson).HasColumnType("json");
        b.Property(n => n.ResolvedBy).HasMaxLength(200);
        b.HasIndex(n => new { n.TenantId, n.DedupeKey }).IsUnique();
        b.HasIndex(n => new { n.TenantId, n.Audience, n.ResolvedAt });
        b.HasOne<Tenant>().WithMany().HasForeignKey(n => n.TenantId).OnDelete(DeleteBehavior.NoAction);
    }
}
