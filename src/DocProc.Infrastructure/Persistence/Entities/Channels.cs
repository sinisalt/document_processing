namespace DocProc.Infrastructure.Persistence.Entities;

public enum ChannelType
{
    Mailbox,
    WatchedFolder,
    ApiUpload,
    PortalUpload,
}

public enum ChannelStatus
{
    Active,
    Paused,
    Error,
}

/// <summary>One way documents reach a tenant (docs/02 §1). Credentials are never here: <see cref="SecretRef"/> points into Key Vault.</summary>
public sealed class IngestionChannel : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public ChannelType Type { get; set; }

    /// <summary>Type-specific settings as JSON: mailbox address and provider, folder path, API key id.</summary>
    public string ConfigJson { get; set; } = "{}";

    public string? SecretRef { get; set; }
    public ChannelStatus Status { get; set; } = ChannelStatus.Active;

    /// <summary>Opaque resume point: Graph delta token, IMAP UID, folder scan marker.</summary>
    public string? Cursor { get; set; }

    public DateTimeOffset? LastPolledAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The normalised source a polling channel reads (docs/11 §6.3 <c>SourceKey</c>): <c>imap:{host}:{port}/{user}/{folder}</c>
    /// for a mailbox, <c>folder:{tenant}/{path}</c> for a watched folder; unique per channel type across tenants, so
    /// two tenants cannot poll the same mailbox. Null for upload channels and for channels without a valid config.
    /// </summary>
    public string? SourceKey { get; set; }

    /// <summary>The supplier of a document no source rule claims (docs/11 §7.2 step 4), as <c>DefaultWhenNoContentMatch</c>.</summary>
    public Guid? DefaultSupplierId { get; set; }

    /// <summary>The type of a document no rule pins, when the channel delivers one type only.</summary>
    public string? DefaultDocumentTypeKey { get; set; }

    /// <summary>The receiving entity of a document no rule names (one mailbox per company, docs/11 §6.4).</summary>
    public Guid? DefaultReceivingEntityId { get; set; }

    public Tenant? Tenant { get; set; }
}
