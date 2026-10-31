using System.Globalization;
using DocProc.Infrastructure.Pdf;
using Microsoft.Extensions.Options;

namespace DocProc.Pipeline;

/// <summary>
/// Fails host start-up on a <c>Pipeline</c> section the runner or the stages could not work with, instead of failing
/// every document later: a render resolution above <c>DocumentProcessing:Pdf:MaximumRenderDpi</c> (every PDF render
/// would throw), no worker or no attempt at all, limits of zero or less, a threshold outside 0 to 1, negative delays.
/// </summary>
internal sealed class PipelineOptionsValidator(IOptions<PdfProcessingOptions> pdf) : IValidateOptions<PipelineOptions>
{
    public ValidateOptionsResult Validate(string? name, PipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        void AtLeast(string key, long value, long minimum, string why = "")
        {
            if (value < minimum)
            {
                failures.Add($"{PipelineOptions.SectionName}:{key} is {value}; it must be at least {minimum}{why}.");
            }
        }

        void NotNegative(string key, TimeSpan value)
        {
            if (value < TimeSpan.Zero)
            {
                failures.Add($"{PipelineOptions.SectionName}:{key} is {value}; it must not be negative.");
            }
        }

        AtLeast(nameof(PipelineOptions.MaxParallelism), options.MaxParallelism, 1, ": the runner needs at least one worker");
        AtLeast(nameof(PipelineOptions.MaxAttempts), options.MaxAttempts, 1, ": the first attempt counts");
        NotNegative(nameof(PipelineOptions.RetryBaseDelay), options.RetryBaseDelay);
        NotNegative(nameof(PipelineOptions.MaxRetryDelay), options.MaxRetryDelay);
        NotNegative(nameof(PipelineOptions.ShutdownGracePeriod), options.ShutdownGracePeriod);

        var maximumDpi = pdf.Value.MaximumRenderDpi;
        if (options.RenderDpi < 1)
        {
            failures.Add($"{PipelineOptions.SectionName}:{nameof(PipelineOptions.RenderDpi)} is {options.RenderDpi}; it must be at least 1.");
        }
        else if (options.RenderDpi > maximumDpi)
        {
            failures.Add($"{PipelineOptions.SectionName}:{nameof(PipelineOptions.RenderDpi)} is {options.RenderDpi}, above {PdfProcessingOptions.SectionName}:{nameof(PdfProcessingOptions.MaximumRenderDpi)} ({maximumDpi}): every PDF render would fail. Lower the first or raise the second.");
        }

        if (double.IsNaN(options.LlmClassificationThreshold) || options.LlmClassificationThreshold is < 0 or > 1)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture, $"{PipelineOptions.SectionName}:{nameof(PipelineOptions.LlmClassificationThreshold)} is {options.LlmClassificationThreshold}; it must be between 0 and 1."));
        }

        AtLeast(nameof(PipelineOptions.MaxArchiveDepth), options.MaxArchiveDepth, 0);
        AtLeast(nameof(PipelineOptions.MaxArchiveEntryBytes), options.MaxArchiveEntryBytes, 1);
        AtLeast(nameof(PipelineOptions.MaxArchiveEntries), options.MaxArchiveEntries, 1);
        AtLeast(nameof(PipelineOptions.MaxUnpackedBytesPerIngestion), options.MaxUnpackedBytesPerIngestion, 1);
        AtLeast(nameof(PipelineOptions.MaxUnpackedItemsPerIngestion), options.MaxUnpackedItemsPerIngestion, 1);
        AtLeast(nameof(PipelineOptions.MaxArchiveCompressionRatio), options.MaxArchiveCompressionRatio, 0, " (0 turns the check off)");
        AtLeast(nameof(PipelineOptions.MinimumImageBytes), options.MinimumImageBytes, 0);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
