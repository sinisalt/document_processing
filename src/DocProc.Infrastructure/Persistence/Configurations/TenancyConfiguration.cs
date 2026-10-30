using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocProc.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.ToTable("Tenants");
        b.Property(t => t.Name).HasMaxLength(200);
        b.Property(t => t.Slug).HasMaxLength(64);
        b.HasIndex(t => t.Slug).IsUnique();
        b.Property(t => t.LegalName).HasMaxLength(300);
        b.Property(t => t.VatId).HasMaxLength(32);
        b.Property(t => t.DefaultCurrency).HasMaxLength(3).IsFixedLength();
        b.Property(t => t.CountryCode).HasMaxLength(2).IsFixedLength();
        b.Property(t => t.TimeZone).HasMaxLength(64);
        b.OwnsOne(t => t.Settings, s => s.ToJson());
    }
}

internal sealed class TenantMemberConfiguration : IEntityTypeConfiguration<TenantMember>
{
    public void Configure(EntityTypeBuilder<TenantMember> b)
    {
        b.ToTable("TenantMembers");
        b.HasKey(m => new { m.TenantId, m.UserId });
        b.Property(m => m.UserId).HasMaxLength(200);
        b.Property(m => m.Email).HasMaxLength(320);
        b.Property(m => m.DisplayName).HasMaxLength(200);
        b.HasOne(m => m.Tenant).WithMany(t => t.Members).HasForeignKey(m => m.TenantId);
    }
}

internal sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> b)
    {
        b.ToTable("ApiKeys");
        b.Property(k => k.Name).HasMaxLength(100);
        b.Property(k => k.Prefix).HasMaxLength(16);
        b.Property(k => k.Hash).HasMaxLength(64).IsFixedLength();
        b.HasIndex(k => k.Hash).IsUnique();
        b.HasOne(k => k.Tenant).WithMany().HasForeignKey(k => k.TenantId);
    }
}

internal sealed class IngestionChannelConfiguration : IEntityTypeConfiguration<IngestionChannel>
{
    public void Configure(EntityTypeBuilder<IngestionChannel> b)
    {
        b.ToTable("IngestionChannels");
        b.Property(c => c.Name).HasMaxLength(100);
        b.Property(c => c.ConfigJson).HasColumnType("json");
        b.Property(c => c.SecretRef).HasMaxLength(200);
        b.Property(c => c.Cursor).HasMaxLength(2000);
        b.Property(c => c.LastError).HasMaxLength(2000);
        b.HasOne(c => c.Tenant).WithMany(t => t.Channels).HasForeignKey(c => c.TenantId);
    }
}
