namespace DocProc.Llm.Extraction;

/// <summary>Values shared by the stage callers of <see cref="Gateway.ILlmGateway"/>.</summary>
public static class LlmRequestDefaults
{
    /// <summary>
    /// Placeholder for <see cref="LlmRequest.Model"/>: the gateway replaces it with the model resolved for the tenant
    /// and stage before hashing or calling a provider, so stage code never names a model.
    /// </summary>
    public const string ModelResolvedByGateway = "resolved-by-gateway";
}
