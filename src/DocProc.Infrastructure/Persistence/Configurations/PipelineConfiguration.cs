using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocProc.Infrastructure.Persistence.Configurations;

internal sealed class PipelineSnapshotConfiguration : IEntityTypeConfiguration<PipelineSnapshot>
{
    public void Configure(EntityTypeBuilder<PipelineSnapshot> b)
    {
        b.ToTable("PipelineSnapshots");
        b.HasKey(p => new { p.TenantId, p.Hash });
        b.Property(p => p.Hash).HasMaxLength(32);
        b.Property(p => p.StepsJson).HasColumnType("json");
        b.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class LookupResultConfiguration : IEntityTypeConfiguration<LookupResult>
{
    public void Configure(EntityTypeBuilder<LookupResult> b)
    {
        b.ToTable("LookupResults");
        b.Property(r => r.StepId).HasMaxLength(64);
        b.Property(r => r.RequestHash).HasMaxLength(64).IsFixedLength();
        b.Property(r => r.AssignmentsJson).HasColumnType("json");
        b.Property(r => r.Error).HasMaxLength(1000);
        b.HasIndex(r => new { r.TenantId, r.DocumentId, r.StepId, r.RequestHash }).IsUnique();
        b.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Document>().WithMany().HasForeignKey(r => r.DocumentId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class LlmBudgetConfiguration : IEntityTypeConfiguration<LlmBudgetRecord>
{
    public void Configure(EntityTypeBuilder<LlmBudgetRecord> b)
    {
        b.ToTable("LlmBudgets");
        b.Property(r => r.LimitUsd).HasPrecision(18, 4);
        b.Property(r => r.UpdatedBy).HasMaxLength(200);
        b.HasIndex(r => new { r.TenantId, r.Period });
        b.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.NoAction);
    }
}
