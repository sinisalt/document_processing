namespace DocProc.Application.Abstractions;

/// <summary>
/// A mailbox feed. Three implementations per docs/09: Microsoft Graph (production), IMAP via MailKit (local GreenMail
/// and non-M365 providers) and a fixture source replaying .eml files.
/// </summary>
public interface IMailSource
{
    string ChannelType { get; }

    /// <summary>Messages newer than the cursor, oldest first; the cursor is opaque (Graph delta token, IMAP UID, file offset).</summary>
    IAsyncEnumerable<InboundMessage> FetchAsync(string? cursor, CancellationToken cancellationToken);

    /// <summary>Called after a message has been durably stored so it is never delivered twice.</summary>
    Task AcknowledgeAsync(InboundMessage message, CancellationToken cancellationToken);
}

public sealed record InboundMessage(
    string MessageId,
    string Cursor,
    DateTimeOffset ReceivedAt,
    string From,
    IReadOnlyList<string> To,
    string? Subject,
    ReadOnlyMemory<byte> RawEml,
    IReadOnlyList<InboundAttachment> Attachments);

public sealed record InboundAttachment(string FileName, string ContentType, long SizeBytes, ReadOnlyMemory<byte> Content, bool IsInline);
