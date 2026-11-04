using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocProc.Infrastructure.Persistence.Configurations;

internal sealed class GlobalSupplierConfiguration : IEntityTypeConfiguration<GlobalSupplier>
{
    public void Configure(EntityTypeBuilder<GlobalSupplier> b)
    {
        b.ToTable("GlobalSuppliers");
        b.Property(s => s.Name).HasMaxLength(300);
        b.Property(s => s.CountryCode).HasMaxLength(2).IsFixedLength();
    }
}

internal sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> b)
    {
        b.ToTable("Suppliers");
        b.Property(s => s.Name).HasMaxLength(300);
        b.Property(s => s.NormalizedName).HasMaxLength(300);
        b.Property(s => s.VatId).HasMaxLength(32);
        b.Property(s => s.TaxId).HasMaxLength(32);
        b.Property(s => s.AddressesJson).HasColumnType("json");
        b.Property(s => s.AttributesJson).HasColumnType("json");
        b.Property(s => s.SettingsJson).HasColumnType("json");
        b.Property(s => s.DefaultCurrency).HasMaxLength(3).IsFixedLength();
        b.HasIndex(s => new { s.TenantId, s.NormalizedName });
        b.HasIndex(s => new { s.TenantId, s.VatId });
        b.HasOne(s => s.Tenant).WithMany().HasForeignKey(s => s.TenantId);
        b.HasOne(s => s.GlobalSupplier).WithMany().HasForeignKey(s => s.GlobalSupplierId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne(s => s.Group).WithMany().HasForeignKey(s => s.GroupId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class SupplierGroupConfiguration : IEntityTypeConfiguration<SupplierGroup>
{
    public void Configure(EntityTypeBuilder<SupplierGroup> b)
    {
        b.ToTable("SupplierGroups");
        b.Property(g => g.Name).HasMaxLength(SupplierGroup.MaxNameLength);
        b.Property(g => g.AttributesJson).HasColumnType("json");
        b.Property(g => g.SettingsJson).HasColumnType("json");
        b.HasIndex(g => new { g.TenantId, g.Name }).IsUnique();
        b.HasOne<Tenant>().WithMany().HasForeignKey(g => g.TenantId);
    }
}

internal sealed class ReceivingEntityConfiguration : IEntityTypeConfiguration<ReceivingEntity>
{
    public void Configure(EntityTypeBuilder<ReceivingEntity> b)
    {
        b.ToTable("ReceivingEntities");
        b.Property(e => e.Code).HasMaxLength(ReceivingEntity.MaxCodeLength);
        b.Property(e => e.Name).HasMaxLength(ReceivingEntity.MaxNameLength);
        b.Property(e => e.AddressesJson).HasColumnType("json");
        b.Property(e => e.AttributesJson).HasColumnType("json");
        b.HasIndex(e => new { e.TenantId, e.Code }).IsUnique();
        b.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId);
    }
}

internal sealed class SupplierEntityAttributesConfiguration : IEntityTypeConfiguration<SupplierEntityAttributes>
{
    public void Configure(EntityTypeBuilder<SupplierEntityAttributes> b)
    {
        b.ToTable("SupplierEntityAttributes");
        b.HasKey(a => new { a.SupplierId, a.ReceivingEntityId });
        b.Property(a => a.AttributesJson).HasColumnType("json");
        // SQL Server allows one cascade path: the supplier's deletion takes its entity attributes, the entity's is refused while any exist.
        b.HasOne(a => a.Supplier).WithMany().HasForeignKey(a => a.SupplierId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(a => a.ReceivingEntity).WithMany().HasForeignKey(a => a.ReceivingEntityId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class SupplierSourceRuleConfiguration : IEntityTypeConfiguration<SupplierSourceRule>
{
    public void Configure(EntityTypeBuilder<SupplierSourceRule> b)
    {
        b.ToTable("SupplierSourceRules");
        b.Property(r => r.Recipient).HasMaxLength(SupplierSourceRule.MaxAddressLength);
        b.Property(r => r.Sender).HasMaxLength(SupplierSourceRule.MaxAddressLength);
        b.Property(r => r.SenderDomain).HasMaxLength(SupplierSourceRule.MaxDomainLength);
        b.Property(r => r.FolderPrefix).HasMaxLength(SupplierSourceRule.MaxFolderPrefixLength);
        b.Property(r => r.SubjectRegex).HasMaxLength(SupplierSourceRule.MaxRegexLength);
        b.Property(r => r.FileNameRegex).HasMaxLength(SupplierSourceRule.MaxRegexLength);
        b.Property(r => r.PinTypeKey).HasMaxLength(64);
        b.Property(r => r.ConditionHash).HasMaxLength(64).IsFixedLength();
        b.Property(r => r.CreatedBy).HasMaxLength(200);

        // The resolver's lookups (docs/11 §6.5): candidates come by equality on the normalised condition values.
        b.HasIndex(r => new { r.TenantId, r.Recipient });
        b.HasIndex(r => new { r.TenantId, r.Sender });
        b.HasIndex(r => new { r.TenantId, r.SenderDomain });
        b.HasIndex(r => new { r.TenantId, r.ChannelId, r.FolderPrefix });
        b.HasIndex(r => new { r.TenantId, r.ConditionHash }).IsUnique();
        b.HasIndex(r => r.SupplierId);

        b.HasOne(r => r.Supplier).WithMany().HasForeignKey(r => r.SupplierId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<IngestionChannel>().WithMany().HasForeignKey(r => r.ChannelId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<ReceivingEntity>().WithMany().HasForeignKey(r => r.ReceivingEntityId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class ExtractorProfileConfiguration : IEntityTypeConfiguration<ExtractorProfile>
{
    public void Configure(EntityTypeBuilder<ExtractorProfile> b)
    {
        b.ToTable("ExtractorProfiles", t => t.IsTemporal());
        b.Property(p => p.DocumentType).HasMaxLength(32);
        b.Property(p => p.FingerprintJson).HasColumnType("json");
        b.Property(p => p.RulesJson).HasColumnType("json");
        b.Property(p => p.ApprovedBy).HasMaxLength(200);
        b.HasIndex(p => new { p.GlobalSupplierId, p.Status });
        b.HasIndex(p => new { p.SupplierId, p.Status });
        b.HasOne(p => p.GlobalSupplier).WithMany(g => g.Profiles).HasForeignKey(p => p.GlobalSupplierId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne(p => p.Supplier).WithMany().HasForeignKey(p => p.SupplierId).OnDelete(DeleteBehavior.NoAction);
    }
}
