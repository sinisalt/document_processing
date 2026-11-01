using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocProc.Infrastructure.Persistence.Configurations;

internal sealed class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> b)
    {
        b.ToTable("IdempotencyKeys");
        b.Property(k => k.Key).HasMaxLength(200);
        b.Property(k => k.RequestHash).HasMaxLength(64).IsFixedLength();
        b.Property(k => k.ResponseJson).HasColumnType("json");
        b.Property(k => k.Location).HasMaxLength(400);
        // One stored outcome per tenant and key; a concurrent second request with the same key fails the insert
        // instead of creating a second resource.
        b.HasIndex(k => new { k.TenantId, k.Key }).IsUnique();
        b.HasIndex(k => k.CreatedAt);
    }
}
