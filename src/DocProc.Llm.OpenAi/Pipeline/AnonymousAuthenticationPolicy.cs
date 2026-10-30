using System.ClientModel.Primitives;

namespace DocProc.Llm.OpenAi.Pipeline;

/// <summary>
/// Sends no credential. Self-hosted OpenAI-compatible servers on a private network (Ollama, a vLLM behind a VPN) often
/// run without API keys; the SDK still needs an authentication policy to construct the client.
/// </summary>
public sealed class AnonymousAuthenticationPolicy : AuthenticationPolicy
{
    public static readonly AnonymousAuthenticationPolicy Instance = new();

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex) =>
        ProcessNext(message, pipeline, currentIndex);

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex) =>
        ProcessNextAsync(message, pipeline, currentIndex);
}
