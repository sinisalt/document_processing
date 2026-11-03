using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocProc.Infrastructure.Persistence.Configurations;

// The output tables of docs/11 §6.7 and the lookup tables of §6.1 (P3). References between them do not cascade
// (SQL Server refuses several cascade paths to one table); a version's or an entry's row goes with its parent.

internal sealed class OutputDestinationConfiguration : IEntityTypeConfiguration<OutputDestination>
{
    public void Configure(EntityTypeBuilder<OutputDestination> b)
    {
        b.ToTable("OutputDestinations");
        b.Property(d => d.Name).HasMaxLength(200);
        b.Property(d => d.Transport).HasMaxLength(32);
        b.Property(d => d.ConfigJson).HasColumnType("json");
        b.Property(d => d.SecretRef).HasMaxLength(400);
        b.Property(d => d.PreviousSecretRef).HasMaxLength(400);
        b.Property(d => d.CallbackConfigJson).HasColumnType("json");
        b.Property(d => d.LastTestResult).HasMaxLength(2000);
        b.Property(d => d.CreatedBy).HasMaxLength(200);
        b.Property(d => d.Version).IsConcurrencyToken();
        b.HasIndex(d => new { d.TenantId, d.Name }).IsUnique();
        b.HasOne<Tenant>().WithMany().HasForeignKey(d => d.TenantId);
        b.Property(d => d.FeedSequence).HasDefaultValue(0L);
    }
}

internal sealed class OutputMappingConfiguration : IEntityTypeConfiguration<OutputMapping>
{
    public void Configure(EntityTypeBuilder<OutputMapping> b)
    {
        b.ToTable("OutputMappings");
        b.Property(m => m.Key).HasMaxLength(64);
        b.Property(m => m.Name).HasMaxLength(200);
        b.Property(m => m.Description).HasMaxLength(2000);
        b.Property(m => m.SourceTypeKey).HasMaxLength(DocumentTypeKey.MaxLength);
        b.Property(m => m.CreatedBy).HasMaxLength(200);
        b.Property(m => m.PublishedVersion).IsConcurrencyToken();
        b.HasIndex(m => new { m.TenantId, m.Key }).IsUnique();
        b.HasIndex(m => m.TenantId);
        b.HasOne<OutputMapping>().WithMany().HasForeignKey(m => m.BaseMappingId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class OutputMappingVersionConfiguration : IEntityTypeConfiguration<OutputMappingVersion>
{
    public void Configure(EntityTypeBuilder<OutputMappingVersion> b)
    {
        b.ToTable("OutputMappingVersions");
        b.Property(v => v.Engine).HasMaxLength(32);
        b.Property(v => v.ContentType).HasMaxLength(100);
        b.Property(v => v.FileNameTemplate).HasMaxLength(400);
        b.Property(v => v.TargetSchemaJson).HasColumnType("json");
        b.Property(v => v.ReferencedPathsJson).HasColumnType("json");
        b.Property(v => v.SampleDocumentIdsJson).HasColumnType("json");
        b.Property(v => v.ChangeNote).HasMaxLength(2000);
        b.Property(v => v.CreatedBy).HasMaxLength(200);
        b.Property(v => v.PublishedBy).HasMaxLength(200);
        b.Property(v => v.Revision).IsConcurrencyToken();
        b.HasIndex(v => new { v.MappingId, v.Version }).IsUnique();
        b.HasIndex(v => v.TenantId);
        b.HasOne(v => v.Mapping).WithMany(m => m.Versions).HasForeignKey(v => v.MappingId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OutputBindingConfiguration : IEntityTypeConfiguration<OutputBinding>
{
    public void Configure(EntityTypeBuilder<OutputBinding> b)
    {
        b.ToTable("OutputBindings");
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Events).HasConversion(
            v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
            v => System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new List<string>(),
            new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<string>>(
                (a, c) => (a ?? new List<string>()).SequenceEqual(c ?? new List<string>()),
                v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s)),
                v => v.ToList())).HasColumnType("json");
        b.Property(x => x.DocumentTypeKey).HasMaxLength(DocumentTypeKey.MaxLength);
        b.Property(x => x.Condition).HasMaxLength(4000);
        b.Property(x => x.RecipeJson).HasColumnType("json");
        b.Property(x => x.BatchJson).HasColumnType("json");
        b.Property(x => x.ExclusiveGroup).HasMaxLength(64);
        b.Property(x => x.CreatedBy).HasMaxLength(200);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.Enabled });
        b.HasOne(x => x.Mapping).WithMany().HasForeignKey(x => x.MappingId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<OutputMapping>().WithMany().HasForeignKey(x => x.UpdateMappingId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne(x => x.Destination).WithMany().HasForeignKey(x => x.DestinationId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<IngestionChannel>().WithMany().HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class OutboxDeliveryConfiguration : IEntityTypeConfiguration<OutboxDelivery>
{
    public void Configure(EntityTypeBuilder<OutboxDelivery> b)
    {
        b.ToTable("OutboxDeliveries");
        b.Property(d => d.IdempotencyKey).HasMaxLength(200);
        b.Property(d => d.Event).HasMaxLength(64).HasDefaultValue(OutputEvents.Approved);
        b.Property(d => d.RecipeStateJson).HasColumnType("json");
        b.Property(d => d.LastError).HasMaxLength(2000);
        b.Property(d => d.PayloadBlobKey).HasMaxLength(400);
        b.Property(d => d.PayloadSha256).HasMaxLength(64).IsFixedLength();
        b.Property(d => d.ContentType).HasMaxLength(100);
        b.Property(d => d.Version).IsConcurrencyToken();
        b.HasIndex(d => d.IdempotencyKey).IsUnique();
        b.HasIndex(d => new { d.Status, d.NextAttemptAt });
        b.HasIndex(d => d.DocumentId);
        b.HasIndex(d => new { d.DestinationId, d.FeedSequence });
        b.HasOne<OutboxDelivery>().WithMany().HasForeignKey(d => d.RedeliveryOfId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne(d => d.OutboxMessage).WithMany().HasForeignKey(d => d.OutboxMessageId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Document>().WithMany().HasForeignKey(d => d.DocumentId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne(d => d.Binding).WithMany().HasForeignKey(d => d.BindingId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne(d => d.Destination).WithMany().HasForeignKey(d => d.DestinationId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne(d => d.MappingVersion).WithMany().HasForeignKey(d => d.MappingVersionId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class DeliveryAttemptConfiguration : IEntityTypeConfiguration<DeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<DeliveryAttempt> b)
    {
        b.ToTable("DeliveryAttempts");
        b.Property(a => a.Step).HasMaxLength(64);
        b.Property(a => a.ErrorClass).HasMaxLength(64);
        b.Property(a => a.ResponseExcerpt).HasMaxLength(DeliveryAttempt.MaxExcerptLength);
        b.HasIndex(a => new { a.DeliveryId, a.At });
        b.HasOne(a => a.Delivery).WithMany(d => d.AttemptLog).HasForeignKey(a => a.DeliveryId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DocumentExternalRefConfiguration : IEntityTypeConfiguration<DocumentExternalRef>
{
    public void Configure(EntityTypeBuilder<DocumentExternalRef> b)
    {
        b.ToTable("DocumentExternalRefs");
        b.Property(r => r.ExternalId).HasMaxLength(400);
        b.Property(r => r.Message).HasMaxLength(2000);
        b.HasIndex(r => new { r.DocumentId, r.DestinationId }).IsUnique();
        b.HasOne<Document>().WithMany().HasForeignKey(r => r.DocumentId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<OutputDestination>().WithMany().HasForeignKey(r => r.DestinationId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class LookupTableConfiguration : IEntityTypeConfiguration<LookupTable>
{
    public void Configure(EntityTypeBuilder<LookupTable> b)
    {
        b.ToTable("LookupTables");
        b.Property(t => t.Key).HasMaxLength(64);
        b.Property(t => t.Description).HasMaxLength(2000);
        b.Property(t => t.Revision).IsConcurrencyToken();
        b.HasIndex(t => new { t.TenantId, t.Key }).IsUnique();
        b.HasIndex(t => t.TenantId);
    }
}

internal sealed class LookupEntryConfiguration : IEntityTypeConfiguration<LookupEntry>
{
    public void Configure(EntityTypeBuilder<LookupEntry> b)
    {
        b.ToTable("LookupEntries");
        b.Property(e => e.From).HasMaxLength(200);
        b.Property(e => e.To).HasMaxLength(400);
        b.Property(e => e.LabelsJson).HasColumnType("json");
        b.HasIndex(e => new { e.TableId, e.From });
        b.HasIndex(e => e.TenantId);
        b.HasOne(e => e.Table).WithMany(t => t.Entries).HasForeignKey(e => e.TableId).OnDelete(DeleteBehavior.Cascade);
    }
}
