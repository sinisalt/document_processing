using DocProc.Domain.Documents;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocProc.Infrastructure.Persistence.Configurations;

internal sealed class DocumentTypeDefinitionConfiguration : IEntityTypeConfiguration<DocumentTypeDefinition>
{
    public void Configure(EntityTypeBuilder<DocumentTypeDefinition> b)
    {
        b.ToTable("DocumentTypeDefinitions");
        b.Property(d => d.Key).HasMaxLength(DocumentTypeKey.MaxLength);
        b.Property(d => d.Name).AsJson();
        b.Property(d => d.Description).HasMaxLength(2000);
        b.Property(d => d.CreatedBy).HasMaxLength(200);
        b.Property(d => d.PublishedVersion).IsConcurrencyToken();
        // One type per key and tenant; platform templates (TenantId NULL) are unique by key on their own.
        b.HasIndex(d => new { d.TenantId, d.Key }).IsUnique();
        b.HasIndex(d => d.TenantId);
        b.HasOne(d => d.BaseDefinition).WithMany().HasForeignKey(d => d.BaseDefinitionId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class DocumentTypeVersionConfiguration : IEntityTypeConfiguration<DocumentTypeVersion>
{
    public void Configure(EntityTypeBuilder<DocumentTypeVersion> b)
    {
        b.ToTable("DocumentTypeVersions", t => t.IsTemporal());
        b.Property(v => v.DefinitionJson).HasColumnType("json");
        b.Property(v => v.CompiledJsonSchema).HasColumnType("json");
        b.Property(v => v.CompiledLlmSchema).HasColumnType("json");
        b.Property(v => v.FormSpecJson).HasColumnType("json");
        b.Property(v => v.DescriptorsJson).HasColumnType("json");
        b.Property(v => v.LlmSchemaName).HasMaxLength(100);
        b.Property(v => v.LlmSchemaVersion).HasMaxLength(64);
        b.Property(v => v.LlmSchemaHash).HasMaxLength(16).IsFixedLength();
        b.Property(v => v.ContentHash).HasMaxLength(64).IsFixedLength();
        b.Property(v => v.CreatedBy).HasMaxLength(200);
        b.Property(v => v.PublishedBy).HasMaxLength(200);
        b.Property(v => v.ChangeNote).HasMaxLength(2000);
        b.Property(v => v.Revision).IsConcurrencyToken();
        b.HasIndex(v => new { v.DefinitionId, v.Version }).IsUnique();
        b.HasIndex(v => v.TenantId);
        b.HasOne(v => v.Definition).WithMany(d => d.Versions).HasForeignKey(v => v.DefinitionId).OnDelete(DeleteBehavior.Cascade);
    }
}
