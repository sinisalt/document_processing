using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Application.Outputs;
using DocProc.Infrastructure.Outputs.Delivery;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocProc.Infrastructure.Outputs.Acknowledgements;

/// <summary>
/// The status poll of docs/11 §13.8: for every <see cref="DocumentExternalRef"/> that is still <c>Pending</c> and due, the
/// destination's <c>statusPoll.urlTemplate</c> (with <c>{externalId}</c> and <c>{documentId}</c>) is fetched with the
/// destination's own headers and authentication (<see cref="DestinationReader"/>: public addresses re-checked at connect,
/// https, no redirects), at most <see cref="Timeout"/> per call and <see cref="MaxResponseBytes"/> of answer; the answer
/// is mapped by <c>statusPoll.mapping</c> (JSONata in the out-of-process renderer) to <c>{status, message,
/// externalId?}</c> and applied by <see cref="ExternalAcknowledgements"/>. A Pending answer or a failure (network,
/// status, mapping) asks again after the interval, up to <c>maxPolls</c>; then the reference stops being polled. A claim
/// on the row (its <c>NextPollAt</c> moved on first) keeps two hosts from asking twice.
/// </summary>
public sealed class ExternalStatusPoller(
    ISystemDbContextFactory contexts,
    IOutputRenderer renderer,
    DestinationReader reader,
    TimeProvider clock,
    IOptions<DeliveryOptions> options,
    ILogger<ExternalStatusPoller> logger)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public const int MaxResponseBytes = 64 * 1024;

    /// <summary>One round: asks about the due references; returns how many answers were applied.</summary>
    public async Task<int> PollDueAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var db = contexts.CreatePlatformScoped();
        var due = await db.DocumentExternalRefs.AsNoTracking()
            .Where(r => r.Status == ExternalRefStatus.Pending && r.NextPollAt != null && r.NextPollAt <= now)
            .OrderBy(r => r.NextPollAt)
            .Take(Math.Max(1, options.Value.StatusPollBatchSize))
            .Select(r => new { r.Id, r.DestinationId, r.NextPollAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var applied = 0;
        foreach (var row in due)
        {
            var destination = await db.OutputDestinations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == row.DestinationId, cancellationToken).ConfigureAwait(false);
            var config = AcknowledgementConfig.Of(destination?.CallbackConfigJson);
            if (destination is null || config.StatusPoll is not { } poll || destination.Status != OutputDestinationStatus.Active)
            {
                await db.DocumentExternalRefs.Where(r => r.Id == row.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.NextPollAt, (DateTimeOffset?)null), cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Claim: move the row's next poll on before asking, so a second host skips it.
            var next = now.AddMinutes(poll.IntervalMinutes);
            var claimed = await db.DocumentExternalRefs
                .Where(r => r.Id == row.Id && r.NextPollAt == row.NextPollAt && r.Status == ExternalRefStatus.Pending)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.NextPollAt, next).SetProperty(r => r.Polls, r => r.Polls + 1), cancellationToken).ConfigureAwait(false);
            if (claimed == 0)
            {
                continue;
            }

            var reference = await db.DocumentExternalRefs.SingleAsync(r => r.Id == row.Id, cancellationToken).ConfigureAwait(false);
            var (report, problem) = await AskAsync(destination, poll, reference, cancellationToken).ConfigureAwait(false);
            if (report is null)
            {
                reference.Message = problem is null ? reference.Message : Shorten(problem, 1000);
                if (reference.Polls >= poll.MaxPolls)
                {
                    reference.NextPollAt = null;
                    reference.Message = Shorten($"Status polling stopped after {reference.Polls} polls. {problem}", 1000);
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Status poll of document {DocumentId} at {Destination}: {Problem}", reference.DocumentId, destination.Name, problem);
                }

                continue;
            }

            // The answer is about this reference whatever ids it names.
            var outcome = await ExternalAcknowledgements.ApplyAsync(db, destination, report with { DocumentId = reference.DocumentId, ExternalId = report.ExternalId ?? reference.ExternalId }, AcknowledgementSources.StatusPoll, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            if (outcome == AcknowledgementOutcome.StillPending && reference.Polls >= poll.MaxPolls)
            {
                reference.NextPollAt = null;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (outcome == AcknowledgementOutcome.Applied)
            {
                applied++;
            }
        }

        return applied;
    }

    private async Task<(AcknowledgementReport? Report, string? Problem)> AskAsync(OutputDestination destination, StatusPollConfig poll, DocumentExternalRef reference, CancellationToken cancellationToken)
    {
        if (!AcknowledgementConfig.PollTemplateIsValid(poll.UrlTemplate) || !Uri.TryCreate(ExternalAcknowledgements.PollUrl(poll, reference), UriKind.Absolute, out var uri))
        {
            return (null, "The status poll URL template does not give an absolute URL.");
        }

        var answer = await reader.GetAsync(destination, uri, Timeout, MaxResponseBytes, new Dictionary<string, string> { ["X-DocProc-Document-Id"] = reference.DocumentId.ToString() }, cancellationToken).ConfigureAwait(false);
        if (answer.Body is not { } body)
        {
            return (null, answer.Problem);
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(Encoding.UTF8.GetString(body.Span));
        }
        catch (JsonException)
        {
            return (null, "The status answer is not JSON.");
        }

        if (poll.Mapping is { Length: > 0 } mapping)
        {
            var rendered = await renderer.RenderAsync(new RenderRequest(mapping, parsed, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime)), cancellationToken).ConfigureAwait(false);
            if (rendered.Error is { } error)
            {
                return (null, $"The status mapping failed ({error.Kind}): {error.Message}");
            }

            parsed = rendered.Output;
        }

        if (parsed is JsonObject obj && obj["documentId"] is null && obj["externalId"] is null)
        {
            obj["externalId"] = reference.ExternalId;
        }

        var report = AcknowledgementReport.Read(parsed, out var problem);
        return (report, problem);
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max];
}
