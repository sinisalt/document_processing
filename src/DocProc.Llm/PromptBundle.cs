using System.Collections.Frozen;

namespace DocProc.Llm;

public sealed record PromptExample(string Input, string Output);

/// <summary>
/// Provider-neutral prompt. The stable part (system instructions, schema description, examples) comes first so both
/// explicit cache breakpoints (Anthropic) and automatic prefix caching (OpenAI) apply; the task text varies per document.
/// </summary>
public sealed record PromptBundle
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public required string System { get; init; }

    public IReadOnlyList<PromptExample> Examples { get; init; } = [];

    /// <summary>Per-document text: tenant context, supplier memory, instructions specific to this call.</summary>
    public required string Task { get; init; }

    /// <summary>Optional vendor-specific wording, used only when an eval shows a vendor needs it.</summary>
    public IReadOnlyDictionary<LlmProviderKind, string> SystemOverrides { get; init; } = FrozenDictionary<LlmProviderKind, string>.Empty;

    public string SystemFor(LlmProviderKind kind) =>
        SystemOverrides.TryGetValue(kind, out var s) ? s : System;

    /// <summary>Stable prefix identity: anything that changes here invalidates provider-side caches.</summary>
    public string CacheKey => $"{Id}@{Version}";
}
