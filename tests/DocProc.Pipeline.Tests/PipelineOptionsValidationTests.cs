using DocProc.Infrastructure.Pdf;
using Microsoft.Extensions.Options;

namespace DocProc.Pipeline.Tests;

/// <summary>
/// The <c>Pipeline</c> section is validated at start-up (docs/09 "Pipeline"): a setting the runner or the stages could
/// not work with fails the host with a message naming the key, instead of failing every document later.
/// </summary>
public class PipelineOptionsValidationTests
{
    [Fact]
    public void The_defaults_are_valid()
    {
        Validate(new PipelineOptions()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void A_render_dpi_above_the_pdf_maximum_is_refused_with_both_keys_named()
    {
        var result = Validate(new PipelineOptions { RenderDpi = 700 }, new PdfProcessingOptions { MaximumRenderDpi = 600 });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Pipeline:RenderDpi is 700, above DocumentProcessing:Pdf:MaximumRenderDpi (600)");
    }

    [Fact]
    public void A_render_dpi_at_the_pdf_maximum_is_accepted()
    {
        Validate(new PipelineOptions { RenderDpi = 300 }, new PdfProcessingOptions { MaximumRenderDpi = 300 }).Succeeded.ShouldBeTrue();
    }

    public static TheoryData<string, Action<PipelineOptions>> Invalid => new()
    {
        { "Pipeline:MaxParallelism is 0", o => o.MaxParallelism = 0 },
        { "Pipeline:MaxAttempts is 0", o => o.MaxAttempts = 0 },
        { "Pipeline:RetryBaseDelay is -00:00:01", o => o.RetryBaseDelay = TimeSpan.FromSeconds(-1) },
        { "Pipeline:MaxRetryDelay is -00:00:01", o => o.MaxRetryDelay = TimeSpan.FromSeconds(-1) },
        { "Pipeline:ShutdownGracePeriod is -00:00:01", o => o.ShutdownGracePeriod = TimeSpan.FromSeconds(-1) },
        { "Pipeline:RenderDpi is 0", o => o.RenderDpi = 0 },
        { "Pipeline:LlmClassificationThreshold is 1.5", o => o.LlmClassificationThreshold = 1.5 },
        { "Pipeline:LlmClassificationThreshold is -0.1", o => o.LlmClassificationThreshold = -0.1 },
        { "Pipeline:LlmClassificationThreshold is NaN", o => o.LlmClassificationThreshold = double.NaN },
        { "Pipeline:MaxArchiveDepth is -1", o => o.MaxArchiveDepth = -1 },
        { "Pipeline:MaxArchiveEntryBytes is 0", o => o.MaxArchiveEntryBytes = 0 },
        { "Pipeline:MaxArchiveEntries is 0", o => o.MaxArchiveEntries = 0 },
        { "Pipeline:MaxUnpackedBytesPerIngestion is -5", o => o.MaxUnpackedBytesPerIngestion = -5 },
        { "Pipeline:MaxUnpackedItemsPerIngestion is 0", o => o.MaxUnpackedItemsPerIngestion = 0 },
        { "Pipeline:MaxArchiveCompressionRatio is -1", o => o.MaxArchiveCompressionRatio = -1 },
        { "Pipeline:MinimumImageBytes is -1", o => o.MinimumImageBytes = -1 },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void A_setting_the_pipeline_cannot_work_with_is_refused_with_its_key_named(string expected, Action<PipelineOptions> change)
    {
        var options = new PipelineOptions();
        change(options);

        var result = Validate(options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldHaveSingleItem().ShouldStartWith(expected);
    }

    [Fact]
    public void The_limits_that_allow_zero_accept_it()
    {
        // 0 turns the compression check off, 0 archive depth expands only the attachment itself, and no image is too small.
        Validate(new PipelineOptions { MaxArchiveCompressionRatio = 0, MaxArchiveDepth = 0, MinimumImageBytes = 0, RetryBaseDelay = TimeSpan.Zero, LlmClassificationThreshold = 0 })
            .Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task A_host_with_a_render_dpi_above_the_pdf_maximum_fails_at_start_up()
    {
        var failure = await Should.ThrowAsync<OptionsValidationException>(() => PipelineTestHost.StartAsync(settings =>
        {
            settings["Pipeline:RenderDpi"] = "800";
            settings["DocumentProcessing:Pdf:MaximumRenderDpi"] = "600";
        }));

        failure.OptionsType.ShouldBe(typeof(PipelineOptions));
        failure.Message.ShouldContain("Pipeline:RenderDpi is 800, above DocumentProcessing:Pdf:MaximumRenderDpi (600)");
    }

    [Fact]
    public async Task A_host_with_no_worker_fails_at_start_up()
    {
        var failure = await Should.ThrowAsync<OptionsValidationException>(() => PipelineTestHost.StartAsync(settings => settings["Pipeline:MaxParallelism"] = "0"));

        failure.Message.ShouldContain("Pipeline:MaxParallelism is 0; it must be at least 1");
    }

    private static ValidateOptionsResult Validate(PipelineOptions options, PdfProcessingOptions? pdf = null) =>
        new PipelineOptionsValidator(Options.Create(pdf ?? new PdfProcessingOptions())).Validate(null, options);
}
