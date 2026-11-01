using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocProc.Infrastructure.Persistence.Configurations;

internal sealed class LlmProviderConfigConfiguration : IEntityTypeConfiguration<LlmProviderConfigRecord>
{
    public void Configure(EntityTypeBuilder<LlmProviderConfigRecord> b)
    {
        b.ToTable("LlmProviderConfigs");
        b.Property(c => c.Endpoint).HasMaxLength(500);
        b.Property(c => c.ApiVersion).HasMaxLength(32);
        b.Property(c => c.SecretRef).HasMaxLength(200);
        b.Property(c => c.KeyLast4).HasMaxLength(4);
        b.Property(c => c.KeyFingerprint).HasMaxLength(64);
        b.Property(c => c.MonthlySpendCapUsd).HasPrecision(18, 2);
        b.Property(c => c.LastError).HasMaxLength(2000);
        b.Property(c => c.CreatedBy).HasMaxLength(200);
        b.Property(c => c.Models).AsJson();
        b.HasIndex(c => new { c.TenantId, c.Status });
        b.HasOne(c => c.Tenant).WithMany().HasForeignKey(c => c.TenantId);
    }
}

internal sealed class TenantLlmPolicyConfiguration : IEntityTypeConfiguration<TenantLlmPolicyRecord>
{
    public void Configure(EntityTypeBuilder<TenantLlmPolicyRecord> b)
    {
        b.ToTable("TenantLlmPolicies");
        b.HasKey(p => p.TenantId);
        b.Property(p => p.StageOverrides).AsJson();
        b.HasOne(p => p.Tenant).WithOne().HasForeignKey<TenantLlmPolicyRecord>(p => p.TenantId);
        b.HasOne(p => p.DefaultConfig).WithMany().HasForeignKey(p => p.DefaultConfigId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class LlmUsageConfiguration : IEntityTypeConfiguration<LlmUsageEntry>
{
    public void Configure(EntityTypeBuilder<LlmUsageEntry> b)
    {
        b.ToTable("LlmUsage");
        b.Property(u => u.Model).HasMaxLength(100);
        b.Property(u => u.PromptVersion).HasMaxLength(100);
        b.Property(u => u.SchemaVersion).HasMaxLength(100);
        b.Property(u => u.EstimatedCostUsd).HasPrecision(18, 6);
        b.Property(u => u.StopReason).HasMaxLength(64);
        b.Property(u => u.ProviderRequestId).HasMaxLength(100);
        b.Property(u => u.RequestHash).HasMaxLength(64).IsFixedLength();
        b.HasIndex(u => new { u.TenantId, u.At });
        b.HasIndex(u => new { u.ConfigId, u.At });
        b.HasIndex(u => u.DocumentId);
        b.HasIndex(u => u.RequestHash);
    }
}
