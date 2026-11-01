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

    public Tenant? Tenant { get; set; }
}
