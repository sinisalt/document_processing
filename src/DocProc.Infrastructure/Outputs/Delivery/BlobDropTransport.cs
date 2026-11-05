using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Infrastructure.Outputs.Delivery;

/// <summary>
/// The file drop of docs/11 §13.3 (<c>blob</c> transport): the payload is written below
/// <c>{DropRoot}/{tenantId}/{config.path}/</c> under the file name the mapping version's <c>FileNameTemplate</c> (else
/// the destination's <c>fileNameTemplate</c>) gives, to a temporary name first and then renamed, so a reader of the
/// folder never sees half a file. A file of that name with the same bytes is the delivery already made (a retry after
/// the rename); one with other bytes is a permanent failure, never overwritten. The tenant folder comes from the
/// delivery, not the config, and the config's path is checked to stay relative, so no tenant writes into another's
/// folder.
/// </summary>
public sealed class BlobDropTransport(string root) : IDeliveryTransport
{
    public string Transport => OutputTransports.Blob;

    /// <summary>The absolute root of the drop.</summary>
    public string Root { get; } = Path.GetFullPath(root);

    public Task<DeliveryOutcome> SendAsync(DeliveryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var watch = Stopwatch.StartNew();
        var outcome = Send(context, cancellationToken);
        return Task.FromResult(outcome with { DurationMs = (int)Math.Min(int.MaxValue, watch.ElapsedMilliseconds) });
    }

    private DeliveryOutcome Send(DeliveryContext context, CancellationToken cancellationToken)
    {
        var path = context.Config["path"] is JsonValue p && p.TryGetValue<string>(out var text) ? text : string.Empty;
        var template = context.MappingFileNameTemplate ?? (context.Config["fileNameTemplate"] is JsonValue t && t.TryGetValue<string>(out var name) ? name : "{{meta.documentId}}-{{meta.version}}.json");
        string fileName;
        try
        {
            fileName = DeliveryTemplates.ExpandFileName(template, context.Values);
        }
        catch (DeliveryTemplateException exception)
        {
            return DeliveryOutcome.Permanent("template", exception.Message);
        }

        var tenantRoot = Path.GetFullPath(Path.Combine(Root, context.Delivery.TenantId.ToString("D")));
        var folder = Path.GetFullPath(Path.Combine(tenantRoot, path.Replace('/', Path.DirectorySeparatorChar)));
        var target = Path.GetFullPath(Path.Combine(folder, fileName));
        if (!folder.StartsWith(tenantRoot, StringComparison.Ordinal) || !target.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return DeliveryOutcome.Permanent("path", "The drop path leaves the tenant's folder.");
        }

        var display = Path.GetRelativePath(tenantRoot, target).Replace(Path.DirectorySeparatorChar, '/');
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(folder);
            if (File.Exists(target))
            {
                return SameContent(target, context.Payload)
                    ? DeliveryOutcome.Delivered() with { Note = $"{display} was already written with these bytes." }
                    : DeliveryOutcome.Permanent("exists", $"{display} exists with other content; it is never overwritten (give the file name a {{{{meta.version}}}} or {{{{meta.deliveryId}}}}).");
            }

            var temporary = Path.Combine(folder, $".{fileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporary, context.Payload.Span);
                File.Move(temporary, target, overwrite: false);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            return DeliveryOutcome.Delivered() with { Note = $"Written as {display}." };
        }
        catch (IOException) when (File.Exists(target) && SameContent(target, context.Payload))
        {
            // Another attempt renamed the same bytes into place first.
            return DeliveryOutcome.Delivered() with { Note = $"{display} was already written with these bytes." };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DeliveryOutcome.Retryable("io", $"Writing {display} failed: {exception.Message}");
        }
    }

    private static bool SameContent(string path, ReadOnlyMemory<byte> payload)
    {
        var existing = File.ReadAllBytes(path);
        return existing.Length == payload.Length && CryptographicOperations.FixedTimeEquals(existing, payload.Span);
    }
}
