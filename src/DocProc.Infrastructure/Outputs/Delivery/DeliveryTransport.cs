using System.Net;
using System.Text.Json.Nodes;
using DocProc.Infrastructure.Http;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Infrastructure.Outputs.Delivery;

/// <summary>How one attempt ended (docs/11 §13.6).</summary>
public enum DeliveryOutcomeKind
{
    /// <summary>The receiver has it: a success code, the file written, the entry in the feed.</summary>
    Delivered,

    /// <summary>Worth another attempt: network errors, timeouts, 408, 425, 429 and 5xx.</summary>
    Retryable,

    /// <summary>Another attempt would end the same way: other 4xx, a refused address, a missing secret, a template without a value.</summary>
    Permanent,
}

/// <summary>
/// The result of one attempt: the outcome, the status code, the error class and message, a response excerpt (at most
/// <see cref="DeliveryAttempt.MaxExcerptLength"/> characters, secrets masked), the receiver's <c>Retry-After</c>, the
/// external id its <c>responseMapping</c> captured and how long the attempt took.
/// </summary>
public sealed record DeliveryOutcome(DeliveryOutcomeKind Kind, int? StatusCode = null, string? ErrorClass = null, string? Error = null, string? ResponseExcerpt = null)
{
    public TimeSpan? RetryAfter { get; init; }

    public string? ExternalId { get; init; }

    public int DurationMs { get; init; }

    /// <summary>What happened besides the outcome (a responseMapping that found nothing), for the attempt's log.</summary>
    public string? Note { get; init; }

    public static DeliveryOutcome Delivered(int? statusCode = null, string? excerpt = null) => new(DeliveryOutcomeKind.Delivered, statusCode, "delivered", null, excerpt);

    public static DeliveryOutcome Retryable(string errorClass, string error, int? statusCode = null, string? excerpt = null) =>
        new(DeliveryOutcomeKind.Retryable, statusCode, "retryable:" + errorClass, error, excerpt);

    public static DeliveryOutcome Permanent(string errorClass, string error, int? statusCode = null, string? excerpt = null) =>
        new(DeliveryOutcomeKind.Permanent, statusCode, "permanent:" + errorClass, error, excerpt);
}

/// <summary>The document's original file, for a multipart part <c>{content: "original"}</c>.</summary>
public sealed record OriginalFile(ReadOnlyMemory<byte> Bytes, string FileName, string ContentType);

/// <summary>
/// One attempt's input: the delivery, its destination and the destination's config, the payload snapshot, the values
/// its templates read, the mapping version's file-name template, and the original file on demand.
/// </summary>
public sealed record DeliveryContext(
    OutboxDelivery Delivery,
    OutputDestination Destination,
    JsonObject Config,
    ReadOnlyMemory<byte> Payload,
    string ContentType,
    DeliveryTemplateValues Values,
    string? MappingFileNameTemplate,
    Func<CancellationToken, Task<OriginalFile?>> Original);

/// <summary>A transport of docs/11 §13.3, keyed by <see cref="Transport"/> (<c>http</c>, <c>blob</c>); <c>pull</c> publishes to the feed in the database instead.</summary>
public interface IDeliveryTransport
{
    string Transport { get; }

    Task<DeliveryOutcome> SendAsync(DeliveryContext context, CancellationToken cancellationToken);
}

/// <summary>
/// The outbound client of the delivery path: <see cref="TenantEndpointGuard"/>'s (https only, public addresses checked
/// after DNS resolution, no proxy, no redirects), with the hosts <see cref="DeliveryEndpointPolicy"/> exempts reachable
/// over http at any address (the demos' <c>mock-erp</c> in Development only).
/// </summary>
public sealed class DeliveryHttpClient : IDisposable
{
    public DeliveryHttpClient(DeliveryEndpointPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Policy = policy;
        Client = TenantEndpointGuard.CreateExemptingClient(resolve: null, exempt: policy.IsExempt);

        // Every read of the delivery path is bounded by its caller (ResponseHeadersRead and a bounded reader); this is
        // the backstop for one that buffers a whole answer.
        Client.MaxResponseContentBufferSize = 1024 * 1024;
    }

    public DeliveryEndpointPolicy Policy { get; }

    public HttpClient Client { get; }

    /// <summary>Why a delivery may not call <paramref name="uri"/>, or null when it may.</summary>
    public string? Refusal(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (Policy.IsExempt(uri) && uri.Scheme is "http" or "https")
        {
            return null;
        }

        return TenantEndpointGuard.Validate(uri) is null
            ? null
            : "The destination URL is not allowed: deliveries go over https to a public host (loopback, private, link-local and other reserved addresses are refused).";
    }

    public void Dispose() => Client.Dispose();
}

/// <summary>The classification of docs/11 §13.6 for an http status.</summary>
public static class DeliveryClassifier
{
    public static DeliveryOutcomeKind Classify(int statusCode, IReadOnlyCollection<int>? successCodes)
    {
        if (successCodes is { Count: > 0 } ? successCodes.Contains(statusCode) : statusCode is >= 200 and <= 299)
        {
            return DeliveryOutcomeKind.Delivered;
        }

        return statusCode is (int)HttpStatusCode.RequestTimeout or 425 or (int)HttpStatusCode.TooManyRequests or >= 500 and <= 599
            ? DeliveryOutcomeKind.Retryable
            : DeliveryOutcomeKind.Permanent;
    }
}
