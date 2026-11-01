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
        b.Property(s => s.DefaultCurrency).HasMaxLength(3).IsFixedLength();
        b.HasIndex(s => new { s.TenantId, s.NormalizedName });
        b.HasIndex(s => new { s.TenantId, s.VatId });
        b.HasOne(s => s.Tenant).WithMany().HasForeignKey(s => s.TenantId);
        b.HasOne(s => s.GlobalSupplier).WithMany().HasForeignKey(s => s.GlobalSupplierId).OnDelete(DeleteBehavior.SetNull);
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
        b.HasOne(p => p.Supplier).WithMany().HasForeignKey(p => p.SupplierId).OnDelete(DeleteBehavior.SetNull);
    }
}
