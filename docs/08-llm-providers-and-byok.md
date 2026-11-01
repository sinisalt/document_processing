# LLM providers and bring-your-own-key (BYOK)

Requirements: the platform must work with more than one LLM provider (Anthropic-style and
OpenAI-style endpoints), and a tenant must be able to supply their own API key or Azure
resource so that usage is billed to them and their data stays within their own provider
agreement.

## Provider abstraction

```
                         ┌──────────────────────────────────────────────┐
  pipeline stages ──────►│ ILlmGateway                                   │
  (classify, split,      │  - resolves which provider config applies     │
   extract, synthesize)  │    (tenant stage override → tenant default    │
                         │     → platform default)                       │
                         │  - fetches the credential, applies rate and   │
                         │    spend limits, logs usage and cost          │
                         └──────────────┬───────────────────────────────┘
                                        │
                    ┌───────────────────┼─────────────────────────┐
                    ▼                   ▼                         ▼
          ┌──────────────────┐ ┌──────────────────┐   ┌────────────────────────┐
          │ AnthropicProvider│ │ OpenAiCompatible │   │ (later) Bedrock /      │
          │  - Anthropic API │ │ Provider         │   │ Vertex / self-hosted   │
          │  - Claude on     │ │  - OpenAI API    │   │ OpenAI-compatible      │
          │    Microsoft     │ │  - Azure OpenAI /│   │ (vLLM, Ollama) for     │
          │    Foundry       │ │    Foundry models│   │ on-prem customers      │
          └──────────────────┘ └──────────────────┘   └────────────────────────┘
```

`ILlmProvider` is the only thing the pipeline knows about. It exposes a small set of
operations and a capability descriptor; the stage code adapts to the capabilities rather
than to the vendor.

```csharp
interface ILlmProvider
{
    LlmCapabilities Capabilities { get; }   // NativePdfInput, JsonSchemaOutput, PromptCaching,
                                            // Batch, MaxPagesPerRequest, MaxInputTokens, Vision
    Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken ct);
    Task<BatchHandle> SubmitBatchAsync(IReadOnlyList<LlmRequest> requests, CancellationToken ct); // optional
    Task<BatchResult> GetBatchAsync(BatchHandle handle, CancellationToken ct);                    // optional
}

record LlmRequest(
    string Model,                       // model id or Azure deployment name
    PromptBundle Prompt,                // system + examples (stable, cacheable) + task text
    DocumentInput? Document,            // PDF bytes and/or page images and/or text layer
    JsonSchema? OutputSchema,           // canonical schema, version-tagged
    LlmOptions Options);                // effort/reasoning level, max output tokens, timeout

record LlmResult(string Json, LlmUsage Usage, string Model, string StopReason, string ProviderRequestId);
```

Implementation notes:

- **Base it on `Microsoft.Extensions.AI`** (`IChatClient`, `GetResponseAsync<T>` with
  `useJsonSchemaResponseFormat`) for the shared path. Both the official Anthropic SDK and the
  official OpenAI / Azure OpenAI SDKs expose `IChatClient`, which gives us middleware for
  logging, telemetry and caching for free. Where the abstraction does not expose a feature we
  need (native PDF document blocks, explicit cache breakpoints, batch endpoints, refusal
  fallbacks, effort levels), the adapter uses the vendor SDK directly. The pipeline never does.
- **Document input strategy per capability.** If the provider accepts native PDF input (the
  Anthropic API does, up to 600 pages; OpenAI chat completions accept PDF as base64 file
  parts), send the PDF. Otherwise send page PNGs plus the positioned text layer rendered as
  text. The same `DocumentInput` carries both, and the adapter picks. This also makes the
  profile-synthesis and splitting stages provider-neutral.
- **Schema portability.** Structured-output rules differ: OpenAI strict mode requires every
  property to be listed in `required` and `additionalProperties: false`; Anthropic rejects
  `minimum`/`maximum`, length constraints and recursive schemas. The canonical JSON schema is
  therefore written to the **intersection**: all properties required, optional values
  expressed as nullable types, no numeric or length constraints (validation does that in
  code), no recursion. A schema test in CI checks each provider accepts the schema.
- **Prompt portability.** Prompts are provider-neutral `PromptBundle`s with an optional
  per-provider override file; the stable part (instructions, schema description, examples)
  comes first so both Anthropic's explicit cache breakpoints and OpenAI's automatic prefix
  caching apply. The eval harness runs every prompt version against every enabled
  provider/model combination, so a prompt change cannot silently regress one vendor.
- **Reasoning/effort** is a normalised option (`None | Low | Medium | High`) mapped by each
  adapter to its vendor setting; unsupported values degrade to the nearest.
- **Errors** are normalised to `LlmError { Kind: Auth | RateLimited | Quota | ContentRefused |
  InvalidRequest | Transient; RetryAfter }` so the worker's retry policy and the tenant-facing
  flags do not depend on the vendor.
- **Claude on Microsoft Foundry** is a first-class option for the Anthropic adapter because the
  platform runs in Azure: same models and API, billed through Azure (and against Azure
  commitments), with an Azure-hosted route for data-zone control. The `Anthropic.Foundry`
  package provides the client. A tenant can therefore BYOK with either an Anthropic API key
  or their own Foundry resource.

## Provider configuration model

```
LlmProviderConfig
  id, scope (Platform | Tenant), tenantId (null for platform)
  kind (Anthropic | AnthropicFoundry | OpenAi | AzureOpenAi | OpenAiCompatible)
  endpoint (base URL or Azure resource endpoint), apiVersion (Azure)
  auth: { type: ApiKey | ManagedIdentity | EntraClientCredentials,
          secretRef: Key Vault secret name (never the key itself), keyLast4, keyFingerprint }
  models: { classify: "...", split: "...", extract: "...", synthesize: "..." }   // model ids or deployment names
  limits: { monthlySpendCapUsd, maxConcurrency, maxRequestsPerMinute }
  dataResidency tag (EU | US | tenant-owned), allowBatch (bool)
  status (Active | Disabled | Invalid), validatedAt, lastError
  createdBy, updatedAt (changes are audit-logged)

TenantLlmPolicy
  tenantId, defaultConfigId, stageOverrides { stage → configId }
  fallbackToPlatformKey (bool, default false for BYOK tenants)
  allowPlatformKeyAtAll (bool; some customers require "our key or nothing")
```

Resolution order for a call: tenant stage override → tenant default → platform default for
that stage. A tenant without any config simply uses the platform's keys and is billed by the
platform; a BYOK tenant uses their own, and the platform never incurs the cost.

### Endpoint restrictions

The worker sends a tenant's key and document content to the configuration's `endpoint` from
inside the platform network, and the provider's status and error come back to the tenant as
the document's failure reason. A tenant-chosen endpoint is therefore restricted
(`TenantEndpointGuard`), or BYOK would let a tenant admin probe and POST to internal services:

- **At creation** (`POST /llm-providers`): `https` only, no user info in the URL, and the host
  must be a public name or address. Refused with 400 on `endpoint`: loopback, private
  (RFC 1918, CGNAT 100.64/10, IPv6 ULA fc00::/7), link-local (169.254/16 with the cloud
  metadata address, fe80::/10), Azure's platform virtual IP `168.63.129.16` (public address
  space, but the platform's DNS, health probes and WireServer, reachable from inside every VNet
  the Container Apps environment runs in), unspecified, multicast, documentation and other reserved ranges,
  for IPv4 and IPv6 alike, including IPv4-mapped/-compatible IPv6, NAT64, 6to4 and Teredo
  forms and decimal, octal or hexadecimal spellings (`2130706433`, `0177.0.0.1`, `0x7f.1`,
  `127.1`); `localhost`, `*.localhost`, `*.local`, `*.internal`, `*.localdomain`, `*.home.arpa`
  and single-label names (which resolve through the platform's search domains). The creation
  check does not resolve DNS.
- **At connect time**: tenant-scope configurations talk through one `HttpClient` whose
  `SocketsHttpHandler.ConnectCallback` resolves the host, refuses the connection when any of
  its addresses is blocked, and connects to the addresses it checked rather than resolving
  again, so a name that later resolves to an internal address (DNS rebinding) is still refused.
  The client sends https only, uses no proxy (a proxy would connect after the check) and does
  not follow redirects. A row created before the check existed is refused when the adapter
  builds its client.
- A refusal sends nothing to the host and fails with `Unavailable` (the tenant's documents
  wait with `LLM_PROVIDER_UNAVAILABLE`, no retry loop) and one fixed message that names neither
  the resolved address nor anything a host said, so the guard is no oracle for the internal
  network.

**Platform configurations** (`POST /llm/providers`, SuperAdmin only) are not restricted: they
may use plain `http` and private or loopback addresses for self-hosted models (vLLM, Ollama)
inside the platform network, and use the SDKs' default transport. Point them only at hosts
the platform operates.

## Key handling

1. A tenant admin enters the key (or selects "use my Azure resource with managed identity /
   Entra app registration") in the portal over TLS.
2. The API writes the secret to **Azure Key Vault** under a per-tenant secret name and stores
   only the reference, the last four characters and a fingerprint in SQL Server. In local
   development a Key Vault emulator or .NET user-secrets stands in behind the same interface.
3. The API validates the credential immediately with a minimal call (list models or a
   one-token completion) and records `validatedAt` or `lastError`.
4. The worker fetches the secret per job through its managed identity, caches it in memory
   for a short TTL keyed by config id, and never writes it to logs, telemetry, exceptions or the
   database. Secrets are redacted by a logging filter as a second line of defence.
5. Rotation writes a new Key Vault secret version; revocation disables the config; both are
   audit-logged with who and when.
6. Platform keys live in the same vault under platform scope and follow the same path, so
   there is one code path, not a privileged one.

## Cost attribution and limits

- Every LLM call writes a usage row: tenant, document, stage, config id, **key owner
  (Platform | Tenant)**, provider, model, input/output/cached/reasoning tokens, estimated cost,
  latency, stop reason, provider request id.
- Cost is estimated from a per-provider **price table** kept in configuration (we cannot see
  a tenant's invoice). The portal shows tenants their estimated spend per day/supplier/stage;
  the admin console shows platform-key spend per tenant for invoicing.
- Spend caps are enforced by the gateway: when a config reaches its monthly cap or its
  provider returns quota/auth errors, the affected documents get the flag
  `LLM_PROVIDER_UNAVAILABLE`, the tenant's LLM stages pause (documents queue, nothing is lost),
  the tenant admin is notified, and, only if the tenant policy allows, calls fall back to the
  platform key.
- Rate limiting is per config so one tenant's burst cannot consume another tenant's quota,
  and so BYOK tenants are limited by their own provider tier, not ours.

## Isolation

- The credential, the rate limiter and the in-memory secret cache are keyed by config id,
  which is tenant-scoped; nothing provider-related is shared between tenants except the
  adapter code.
- A tenant's BYOK provider can be in their own Azure subscription and region, which keeps
  document content inside their data-processing agreement. This is the recommended set-up
  for customers with strict residency requirements and is a selling point of BYOK.
- Platform default keys are configured per data-residency tag (EU, US) and tenants are
  assigned to one, so a tenant without BYOK still gets a provider endpoint in their region.

## What stays provider-specific (and where)

| Concern | Lives in | Note |
|---|---|---|
| Native PDF block, citations, cache_control, refusal fallbacks, effort | `AnthropicProvider` | see `06-llm-integration.md` for the Anthropic defaults |
| File parts for PDFs, strict JSON schema, reasoning effort, automatic prefix caching, Batch API | `OpenAiCompatibleProvider` | Azure OpenAI uses deployment names instead of model ids and an API version |
| Price tables | configuration | reviewed when providers change prices |
| Default models per stage | platform config, per provider | chosen by the eval harness, not hard-coded |
| Prompts | `PromptBundle` + optional per-provider override | override only when an eval shows a vendor needs different wording |

## UI

- **Customer portal → Settings → AI providers**: add a provider (kind, endpoint, key or Azure
  identity), test connection, choose a model per stage from the provider's model list, set a
  monthly cap, decide whether the platform key may be used as fallback, see usage and
  estimated cost.
- **Admin console → Platform → AI providers**: platform default configs per region, price
  tables, per-tenant overview (BYOK or platform, spend, error rate), kill switch per provider.

## Roadmap placement

The abstraction is in Phase 0/1 from the start (it costs little when the first adapter is
written and a lot to retrofit). Phase 1 ships the Anthropic adapter and the OpenAI-compatible
adapter against Azure OpenAI, both with platform keys. BYOK configuration, Key Vault flow and
the portal screens ship in Phase 2; cost attribution and caps in Phase 3 with the rest of the
measurement work.
