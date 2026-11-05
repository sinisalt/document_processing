using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocProc.Infrastructure.Persistence.Configurations;

// The master-data sources and their runs of docs/11 §6.8 (P5).

internal sealed class MasterDataSourceConfiguration : IEntityTypeConfiguration<MasterDataSource>
{
    public void Configure(EntityTypeBuilder<MasterDataSource> b)
    {
        b.ToTable("MasterDataSources");
        b.Property(s => s.Version).IsConcurrencyToken();
        b.Property(s => s.Name).HasMaxLength(MasterDataSource.MaxNameLength);
        b.Property(s => s.Target).HasMaxLength(64);
        b.Property(s => s.Transport).HasMaxLength(32);
        b.Property(s => s.MappingExpression).HasMaxLength(MasterDataSource.MaxMappingLength);
        b.Property(s => s.ConflictPolicyJson).HasColumnType("json");
        b.Property(s => s.ScheduleCron).HasMaxLength(MasterDataSource.MaxCronLength);
        b.Property(s => s.LastResultJson).HasColumnType("json");
        b.Property(s => s.CreatedBy).HasMaxLength(200);
        b.HasIndex(s => new { s.TenantId, s.Name }).IsUnique();
        // The scheduler picks the enabled sources that are due.
        b.HasIndex(s => new { s.Enabled, s.NextRunAt });
        b.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<OutputDestination>().WithMany().HasForeignKey(s => s.DestinationId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class MasterDataRunConfiguration : IEntityTypeConfiguration<MasterDataRun>
{
    public void Configure(EntityTypeBuilder<MasterDataRun> b)
    {
        b.ToTable("MasterDataRuns");
        b.Property(r => r.ReportJson).HasColumnType("json");
        b.Property(r => r.By).HasMaxLength(200);
        b.HasIndex(r => new { r.TenantId, r.SourceId, r.At });
        b.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<MasterDataSource>().WithMany().HasForeignKey(r => r.SourceId).OnDelete(DeleteBehavior.Cascade);
    }
}
