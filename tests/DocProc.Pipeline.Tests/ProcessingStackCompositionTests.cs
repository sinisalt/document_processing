using DocProc.Llm.OpenAi;
using DocProc.Llm.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Pipeline.Tests;

/// <summary>What <c>AddDocProcProcessingStack</c> wires beyond the handlers the other tests drive.</summary>
public class ProcessingStackCompositionTests
{
    [Fact]
    public async Task The_openai_adapter_times_its_requests_on_the_host_clock()
    {
        await using var host = await PipelineTestHost.StartAsync();

        var factory = host.Services.GetServices<ILlmProviderFactory>().OfType<OpenAiCompatibleProviderFactory>().ToList().ShouldHaveSingleItem();
        factory.Clock.ShouldBeSameAs(host.Clock, "the per-request timeout waits on the host's TimeProvider, not on the system clock");
    }
}
