using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocProc.Application.Abstractions;
using DocProc.Application.Outputs;
using DocProc.Definitions.Pipelines;
using DocProc.Infrastructure.Http;
using DocProc.Infrastructure.Outputs.Delivery;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocProc.Infrastructure.Outputs.Lookups;

/// <summary>One call of an <c>http-lookup</c> step: the document it runs for, the step, and the values view it reads.</summary>
/// <param name="TenantId">The document's tenant; the destination must be one of its own.</param>
/// <param name="DocumentId">The document.</param>
/// <param name="DocumentVersion">Its version (<c>{{meta.version}}</c>).</param>
/// <param name="TypeKey">Its type (<c>{{meta.typeKey}}</c>).</param>
/// <param name="Step">The resolved step with its parameters.</param>
/// <param name="Values">The values view of the document as Extract has it so far.</param>
/// <param name="Today">Today in the tenant's time zone (<c>$today()</c>).</param>
public sealed record LookupCall(Guid TenantId, Guid DocumentId, int DocumentVersion, string TypeKey, ResolvedStep Step, JsonObject Values, DateOnly Today);

/// <summary>What one <c>http-lookup</c> call gave.</summary>
/// <param name="Succeeded">The destination answered and the mapping gave assignments (possibly none).</param>
/// <param name="Assignments">The field assignments, path to value (a JSON null leaves the field as it is).</param>
/// <param name="Cached">The answer came from an earlier call with the same request (<c>LookupResults</c>).</param>
/// <param name="Reason">For a failure: <c>destination</c>, <c>request</c>, <c>blocked</c>, <c>secret</c>, <c>timeout</c>, <c>network</c>, <c>status</c>, <c>tooLarge</c>, <c>mapping</c> or <c>circuitOpen</c>.</param>
/// <param name="Message">For a failure: what went wrong, without anything the destination said.</param>
/// <param name="HttpStatus">The http status of the last attempt, when one answered.</param>
/// <param name="Attempts">How many requests were sent.</param>
public sealed record LookupOutcome(bool Succeeded, IReadOnlyDictionary<string, JsonNode?> Assignments, bool Cached, string? Reason, string? Message, int? HttpStatus, int Attempts)
{
    public static LookupOutcome Failed(string reason, string message, int? status = null, int attempts = 0) =>
        new(false, new Dictionary<string, JsonNode?>(StringComparer.Ordinal), false, reason, message, status, attempts);
}

/// <summary>
/// The <c>http-lookup</c> step of docs/11 §10.2: calls one of the tenant's <c>http</c> destinations during Extract, to
/// enrich or verify values (an ERP vendor id, a PO owner), and maps the answer to field assignments.
/// <list type="bullet">
/// <item><description>The request: the <c>request</c> JSONata expression runs in the sandboxed renderer over the values
/// view; its result fills the destination's <c>urlTemplate</c> placeholders (<c>{{output.*}}</c>, percent-encoded, the
/// origin kept) and is the JSON body of a POST. The destination's headers and auth (its secret from
/// <see cref="ISecretStore"/>) are sent as a delivery sends them.</description></item>
/// <item><description>The connection: <see cref="DeliveryHttpClient"/>, whose connect step re-checks every address
/// (<see cref="TenantEndpointGuard"/>: https to public addresses only, no redirects, no proxy); a timeout per attempt
/// of at most 5 s, an answer of at most 256 KB, at most 2 retries after a retryable failure (a timeout, a network error,
/// 408, 425, 429, 5xx), and a circuit breaker per destination (5 failures in a row keep it closed for 30 s).</description></item>
/// <item><description>The answer: the <c>response</c> expression maps <c>{response, status, values}</c> to an object of
/// field paths and values (at most <see cref="MaxAssignments"/>), which the stage writes as <c>Inferred</c>.</description></item>
/// <item><description>Idempotent per document version: the result is stored under the step and the hash of the request
/// sent (<see cref="LookupResult"/>), and a retry or a reprocess that sends the same request reuses it.</description></item>
/// <item><description>A failure is an outcome, never an exception: the stage raises <c>LOOKUP_FAILED</c>.</description></item>
/// </list>
/// </summary>
public sealed partial class HttpLookupService(
    DocProcDbContext db,
    ISystemDbContextFactory contexts,
    IOutputRenderer renderer,
    DeliveryHttpClient http,
    ISecretStore secrets,
    OAuthTokenCache tokens,
    TimeProvider clock,
    ILogger<HttpLookupService> logger)
{
    /// <summary>The largest answer read (docs/11 §10.2: 256 KB).</summary>
    public const int MaxResponseBytes = 256 * 1024;

    /// <summary>The most field assignments one answer gives.</summary>
    public const int MaxAssignments = 50;

    /// <summary>The largest value of one assignment, in characters of JSON.</summary>
    public const int MaxAssignmentLength = 4096;

    /// <summary>Failures in a row after which a destination's breaker opens.</summary>
    public const int BreakerThreshold = 5;

    /// <summary>How long an open breaker refuses calls.</summary>
    public static readonly TimeSpan BreakerOpenFor = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<Guid, (int Failures, DateTimeOffset OpenUntil)> Breakers = new();

    public async Task<LookupOutcome> RunAsync(LookupCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var step = call.Step;
        if (step.Text("destinationId") is not { } destinationText || !Guid.TryParse(destinationText, out var destinationId))
        {
            return LookupOutcome.Failed("destination", "The step names no destination (destinationId).");
        }

        var destination = await db.OutputDestinations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == destinationId && d.TenantId == call.TenantId, cancellationToken).ConfigureAwait(false);
        if (destination is null || destination.Transport != OutputTransports.Http || destination.Status != OutputDestinationStatus.Active)
        {
            return LookupOutcome.Failed("destination", "The step's destination is not an active http destination of the tenant.");
        }

        JsonObject config;
        try
        {
            config = JsonNode.Parse(destination.ConfigJson) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return LookupOutcome.Failed("destination", "The destination's configuration does not read.");
        }

        // The request: the expression's result fills the URL's placeholders and is a POST's body.
        JsonNode? request = null;
        if (step.Text("request") is { } requestExpression)
        {
            var rendered = await renderer.RenderAsync(new RenderRequest(requestExpression, call.Values.DeepClone(), call.Today), cancellationToken).ConfigureAwait(false);
            if (rendered.Error is { } error)
            {
                return LookupOutcome.Failed("request", $"The request expression failed ({error.Kind}): {error.Message}");
            }

            request = rendered.Output;
        }

        var method = step.Text("method") ?? "GET";
        var template = config["urlTemplate"] is JsonValue t && t.TryGetValue<string>(out var text) ? text : string.Empty;
        if (DestinationConfig.HasPlaceholderInOrigin(template))
        {
            return LookupOutcome.Failed("request", "The destination's URL template has a placeholder in its scheme or host.");
        }

        Uri uri;
        try
        {
            if (!Uri.TryCreate(DeliveryTemplates.ExpandUrl(template, new DeliveryTemplateValues(call.DocumentId, call.DocumentVersion, "lookup", call.TypeKey, Guid.Empty, request)), UriKind.Absolute, out var parsed)
                || !DestinationConfig.KeepsTemplateOrigin(template, parsed) || !DestinationConfig.KeepsTemplatePath(template, parsed))
            {
                return LookupOutcome.Failed("request", "The URL template does not give an absolute URL of its own origin and path.");
            }

            uri = parsed;
        }
        catch (DeliveryTemplateException exception)
        {
            return LookupOutcome.Failed("request", exception.Message);
        }

        if (http.Refusal(uri) is not null)
        {
            return LookupOutcome.Failed("blocked", "The destination URL is not allowed: lookups go over https to a public host.");
        }

        var body = method == "POST" ? Encoding.UTF8.GetBytes((request ?? new JsonObject()).ToJsonString()) : [];
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{method}\n{uri.AbsoluteUri}\n{Encoding.UTF8.GetString(body)}")));
        var cached = await CachedAsync(call, requestHash, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        if (Breakers.TryGetValue(destination.Id, out var breaker) && breaker.OpenUntil > clock.GetUtcNow())
        {
            return LookupOutcome.Failed("circuitOpen", "The destination failed repeatedly; lookups to it pause for 30 s.");
        }

        var watch = Stopwatch.StartNew();
        var (outcome, responseBody) = await SendAsync(destination, config, method, uri, body, requestHash, step, cancellationToken).ConfigureAwait(false);
        if (outcome.Succeeded)
        {
            outcome = await MapAsync(call, step, outcome, responseBody, cancellationToken).ConfigureAwait(false);
        }

        Breakers.AddOrUpdate(
            destination.Id,
            _ => outcome.Succeeded || outcome.Reason == "mapping" ? (0, DateTimeOffset.MinValue) : (1, DateTimeOffset.MinValue),
            (_, current) => outcome.Succeeded || outcome.Reason == "mapping"
                ? (0, DateTimeOffset.MinValue)
                : current.Failures + 1 >= BreakerThreshold ? (0, clock.GetUtcNow() + BreakerOpenFor) : (current.Failures + 1, current.OpenUntil));
        await StoreAsync(call, destination.Id, requestHash, outcome, (int)watch.ElapsedMilliseconds, cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>Closes every breaker (tests).</summary>
    public static void ResetBreakers() => Breakers.Clear();

    private async Task<LookupOutcome?> CachedAsync(LookupCall call, string requestHash, CancellationToken cancellationToken)
    {
        var row = await db.LookupResults.AsNoTracking()
            .Where(r => r.TenantId == call.TenantId && r.DocumentId == call.DocumentId && r.StepId == call.Step.Id && r.RequestHash == requestHash && r.Status == LookupCallStatus.Succeeded)
            .Select(r => new { r.AssignmentsJson, r.HttpStatus })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var assignments = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(row.AssignmentsJson) && JsonNode.Parse(row.AssignmentsJson) is JsonObject stored)
        {
            foreach (var (path, value) in stored)
            {
                assignments[path] = value?.DeepClone();
            }
        }

        return new LookupOutcome(true, assignments, true, null, null, row.HttpStatus, 0);
    }

    private async Task<(LookupOutcome Outcome, byte[] Body)> SendAsync(OutputDestination destination, JsonObject config, string method, Uri uri, byte[] body, string requestHash, ResolvedStep step, CancellationToken cancellationToken)
    {
        var auth = config["auth"] as JsonObject;
        var authType = DestinationConfig.AuthTypeOf(config);
        string? secret = null;
        if (HttpAuthTypes.NeedsSecret(authType))
        {
            secret = destination.SecretRef is { } reference ? await secrets.GetAsync(reference, cancellationToken).ConfigureAwait(false) : null;
            if (string.IsNullOrEmpty(secret) || DestinationConfig.SecretProblem(authType, secret) is not null)
            {
                return (LookupOutcome.Failed("secret", $"The destination's auth '{authType}' needs its secret, and none usable is set."), []);
            }
        }

        if (authType == HttpAuthTypes.ApiKeyHeader && auth?["headerName"] is JsonValue headerNode && headerNode.TryGetValue<string>(out var headerName) && DestinationConfig.IsReservedHeader(headerName))
        {
            return (LookupOutcome.Failed("destination", "The destination's auth headerName is a header the transport sets."), []);
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Clamp((int)(step.Number("timeoutMs") ?? StepKindCatalogue.MaxLookupTimeoutMs), 100, StepKindCatalogue.MaxLookupTimeoutMs));
        var retries = Math.Clamp((int)(step.Number("retries") ?? 1), 0, StepKindCatalogue.MaxLookupRetries);
        var successCodes = config["successCodes"] is JsonArray codes ? codes.Select(c => c?.GetValue<int>() ?? 0).ToList() : null;
        var last = LookupOutcome.Failed("network", "The destination was not called.");
        for (var attempt = 1; attempt <= retries + 1; attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt - 1)), cancellationToken).ConfigureAwait(false);
            }

            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timer.CancelAfter(timeout);
            string? token = null;
            if (authType == HttpAuthTypes.OAuth2ClientCredentials)
            {
                var obtained = await tokens.GetAsync(destination, auth!, secret!, timer.Token, cancellationToken).ConfigureAwait(false);
                if (obtained.Failure is { } failure)
                {
                    last = LookupOutcome.Failed("token", $"The token endpoint failed ({failure.ErrorClass}).", attempts: attempt);
                    if (failure.Kind == DeliveryOutcomeKind.Retryable)
                    {
                        continue;
                    }

                    return (last, []);
                }

                token = obtained.Token;
            }

            using var message = new HttpRequestMessage(new HttpMethod(method), uri);
            try
            {
                if (method == "POST")
                {
                    message.Content = new ByteArrayContent(body);
                    message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                }

                if (config["headers"] is JsonObject headers)
                {
                    foreach (var (name, value) in headers)
                    {
                        if (value is JsonValue v && v.TryGetValue<string>(out var headerValue) && !message.Headers.TryAddWithoutValidation(name, headerValue))
                        {
                            message.Content?.Headers.TryAddWithoutValidation(name, headerValue);
                        }
                    }
                }

                message.Headers.Accept.ParseAdd("application/json");
                message.Headers.TryAddWithoutValidation("Idempotency-Key", requestHash);
                message.Headers.TryAddWithoutValidation("X-DocProc-Lookup-Step", step.Id);
                HttpDeliveryTransport.Authorize(message, authType, auth, secret, token, body, clock);
            }
            catch (FormatException exception)
            {
                return (LookupOutcome.Failed("request", $"The request cannot be built from the destination's settings: {exception.Message}"), []);
            }

            HttpResponseMessage response;
            try
            {
                response = await http.Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timer.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                last = LookupOutcome.Failed("timeout", $"The destination did not answer within {timeout.TotalMilliseconds:0} ms.", attempts: attempt);
                continue;
            }
            catch (HttpRequestException exception) when (TenantEndpointGuard.FindBlocked(exception) is not null)
            {
                return (LookupOutcome.Failed("blocked", "The destination URL is not allowed: lookups go over https to a public host.", attempts: attempt), []);
            }
            catch (HttpRequestException exception)
            {
                last = LookupOutcome.Failed("network", $"The destination could not be reached ({exception.HttpRequestError}).", attempts: attempt);
                continue;
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                byte[] read;
                try
                {
                    read = await HttpDeliveryTransport.ReadBoundedAsync(response.Content, timer.Token, MaxResponseBytes + 1).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    last = LookupOutcome.Failed("timeout", "The destination's answer did not arrive in time.", status, attempt);
                    continue;
                }

                if (status == 401 && token is not null)
                {
                    tokens.Forget(destination);
                }

                var kind = DeliveryClassifier.Classify(status, successCodes);
                if (kind == DeliveryOutcomeKind.Delivered)
                {
                    return read.Length > MaxResponseBytes
                        ? (LookupOutcome.Failed("tooLarge", $"The destination's answer is larger than {MaxResponseBytes / 1024} KB.", status, attempt), [])
                        : (new LookupOutcome(true, new Dictionary<string, JsonNode?>(StringComparer.Ordinal), false, null, null, status, attempt), read);
                }

                last = LookupOutcome.Failed("status", $"The destination answered {status}.", status, attempt);
                if (kind != DeliveryOutcomeKind.Retryable)
                {
                    return (last, []);
                }
            }
        }

        return (last, []);
    }

    private async Task<LookupOutcome> MapAsync(LookupCall call, ResolvedStep step, LookupOutcome sent, byte[] responseBody, CancellationToken cancellationToken)
    {
        JsonNode? response;
        try
        {
            response = responseBody.Length == 0 ? null : JsonNode.Parse(responseBody);
        }
        catch (JsonException)
        {
            response = null; // Not JSON: the mapping sees a null response and the status.
        }

        var input = new JsonObject { ["response"] = response, ["status"] = sent.HttpStatus, ["values"] = call.Values.DeepClone() };
        var expression = step.Text("response") ?? string.Empty;
        var rendered = await renderer.RenderAsync(new RenderRequest(expression, input, call.Today), cancellationToken).ConfigureAwait(false);
        if (rendered.Error is { } error)
        {
            return LookupOutcome.Failed("mapping", $"The response mapping failed ({error.Kind}): {error.Message}", sent.HttpStatus, sent.Attempts);
        }

        var assignments = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (rendered.Undefined || rendered.Output is null)
        {
            return sent with { Assignments = assignments };
        }

        if (rendered.Output is not JsonObject mapped)
        {
            return LookupOutcome.Failed("mapping", "The response mapping gives an object of field paths and values.", sent.HttpStatus, sent.Attempts);
        }

        if (mapped.Count > MaxAssignments)
        {
            return LookupOutcome.Failed("mapping", $"The response mapping gives at most {MaxAssignments} fields.", sent.HttpStatus, sent.Attempts);
        }

        foreach (var (path, value) in mapped)
        {
            if (!PathPattern().IsMatch(path))
            {
                return LookupOutcome.Failed("mapping", $"'{Shorten(path)}' is not a field path (a key, or a key and a member: supplier.vatId, ext.storeNo).", sent.HttpStatus, sent.Attempts);
            }

            if (value is not null && value.ToJsonString().Length > MaxAssignmentLength)
            {
                return LookupOutcome.Failed("mapping", $"The value for '{path}' is longer than {MaxAssignmentLength} characters.", sent.HttpStatus, sent.Attempts);
            }

            assignments[path] = value?.DeepClone();
        }

        return sent with { Assignments = assignments };
    }

    private async Task StoreAsync(LookupCall call, Guid destinationId, string requestHash, LookupOutcome outcome, int durationMs, CancellationToken cancellationToken)
    {
        try
        {
            // Its own context: the result is kept whatever becomes of the stage's unit of work, so a retry of the stage
            // after a crash reuses the answer instead of calling the destination again.
            await using var store = contexts.CreatePlatformScoped();
            var row = await store.LookupResults.FirstOrDefaultAsync(r => r.TenantId == call.TenantId && r.DocumentId == call.DocumentId && r.StepId == call.Step.Id && r.RequestHash == requestHash, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                row = new LookupResult { Id = Guid.CreateVersion7(), TenantId = call.TenantId, DocumentId = call.DocumentId, StepId = call.Step.Id, RequestHash = requestHash, CreatedAt = clock.GetUtcNow() };
                store.LookupResults.Add(row);
            }

            row.DestinationId = destinationId;
            row.Status = outcome.Succeeded ? LookupCallStatus.Succeeded : LookupCallStatus.Failed;
            row.HttpStatus = outcome.HttpStatus;
            row.AssignmentsJson = outcome.Succeeded ? new JsonObject(outcome.Assignments.Select(a => KeyValuePair.Create(a.Key, a.Value?.DeepClone()))).ToJsonString() : null;
            row.Error = outcome.Succeeded ? null : Shorten($"{outcome.Reason}: {outcome.Message}", 1000);
            row.Attempts = outcome.Attempts;
            row.DurationMs = durationMs;
            await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            // A twin attempt stored the same request first; the stage goes on with its own outcome.
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(exception, "Lookup result of document {DocumentId}, step {StepId} not stored: another attempt stored it.", call.DocumentId, call.Step.Id);
            }
        }
    }

    private static string Shorten(string text, int max = 100) => text.Length <= max ? text : text[..max];

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}(\\.[A-Za-z][A-Za-z0-9_]{0,63}){0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();
}
