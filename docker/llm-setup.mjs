// One-shot `llm-setup` service of docker-compose.yml (docs/09 "With Docker Compose", "LLM keys"). Makes the platform
// LLM provider configurations follow the keys in .env, through the API's own admin endpoints, signed in as the realm's
// superadmin, exactly as an operator would in the admin console (AI providers):
//
// - ANTHROPIC_API_KEY: the API and the worker read it as LLM_SECRET_LLM_PLATFORM_EU, the key of the seeded platform
//   Anthropic configuration (secret reference llm-platform-eu, DevSeed). Set, that configuration is enabled
//   (POST /api/v1/llm/providers/{id}:enable); empty, it has no key, so an active one is disabled again.
// - OPENAI_API_KEY with OPENAI_MODEL: the platform OpenAI configuration with this key (compared by the key's
//   fingerprint, `keyFingerprint` of GET /llm/providers) and this model for every stage is the active one. It is
//   created when none exists (POST /api/v1/llm/providers; the key goes to the shared secret store) and enabled when it
//   was disabled; every other active platform OpenAI configuration is disabled, so a key that changes back and forth
//   always ends with the key of .env in use. Empty, the OpenAI configurations are left alone: they keep the key stored
//   for them, and the log names the ones still active.
//
// The newest active platform configuration serves every tenant without its own (EfLlmPolicyStore); the last line of
// this script's log (`docker compose logs llm-setup`) names it, and so does the admin console. The API's start-up log
// does not: it is written before this script runs. Without a key nothing changes: e-invoice XML never needs an LLM,
// the demo PDFs are answered by the replay fixtures, and any other PDF ends Failed until a key is configured.
// Idempotent: a rerun with the same .env changes nothing. Inside the compose network the API is http://api:8080 and
// Keycloak http://keycloak:8080; the token's issuer is still http://localhost:8080/realms/docproc (KC_HOSTNAME), which
// is the one the API accepts. `plan` decides and does no I/O (docker/llm-setup.test.mjs, `node --test`).

import { createHash } from "node:crypto";
import { pathToFileURL } from "node:url";

/** DevSeed.PlatformAnthropicConfig: the seeded platform Anthropic configuration whose key is LLM_SECRET_LLM_PLATFORM_EU. */
export const seededAnthropicConfig = "00000000-0003-4000-8000-000000000001";
export const stages = ["Classify", "Split", "Extract", "Synthesize"];

/** The API's `keyFingerprint`: the first 16 hex digits of the SHA-256 of the key's UTF-8 bytes (LlmAdminEndpoints.Build). */
export function fingerprint(key) {
  return createHash("sha256").update(key, "utf8").digest("hex").slice(0, 16);
}

/** Active before inactive, then the most recently updated: the order in which the API picks the platform default. */
function preferred(a, b) {
  const active = Number(b.status === "Active") - Number(a.status === "Active");
  return active !== 0 ? active : (b.updatedAt ?? "").localeCompare(a.updatedAt ?? "");
}

/** The platform configuration that serves every tenant without its own, or undefined when none is active. */
export function inUse(providers) {
  return providers.filter((p) => p.status === "Active").sort(preferred)[0];
}

const key4 = (p) => (p.keyLast4 ? ` (key …${p.keyLast4})` : "");

/**
 * What to change so the platform configurations follow .env: `steps` ({action: "enable" | "disable" | "create", id?,
 * body?, why}) in the order to run them, and `notes` to log. The Anthropic steps come first, so with both keys set and
 * both changed in one run the OpenAI configuration is the newest one and in use, as before.
 */
export function plan(providers, { anthropicKey = "", openAiKey = "", openAiModel = "" } = {}) {
  const steps = [];
  const notes = [];
  if (openAiKey && !openAiModel) {
    throw new Error("OPENAI_API_KEY is set but OPENAI_MODEL is not: set the OpenAI model id to use for every stage in .env.");
  }

  const seeded = providers.find((p) => p.id === seededAnthropicConfig);
  if (anthropicKey) {
    if (!seeded) {
      notes.push(`the seeded Anthropic configuration ${seededAnthropicConfig} does not exist (is Persistence__SeedDevData off?); add a provider in the admin console instead.`);
    } else if (seeded.status === "Active") {
      notes.push(`Anthropic configuration ${seeded.id} is active; its key is ANTHROPIC_API_KEY (LLM_SECRET_LLM_PLATFORM_EU).`);
    } else {
      steps.push({ action: "enable", id: seeded.id, why: "Anthropic configuration; its key is ANTHROPIC_API_KEY (LLM_SECRET_LLM_PLATFORM_EU)" });
    }
  } else if (seeded?.status === "Active") {
    steps.push({ action: "disable", id: seeded.id, why: "Anthropic configuration: ANTHROPIC_API_KEY is empty, so LLM_SECRET_LLM_PLATFORM_EU is too and every call would fail with 'No key is stored'" });
  }

  const openAi = providers.filter((p) => p.kind === "OpenAi");
  if (openAiKey) {
    const fp = fingerprint(openAiKey);
    const target = openAi
      .filter((p) => p.keyFingerprint === fp && !p.endpoint && stages.every((stage) => p.models?.[stage] === openAiModel))
      .sort(preferred)[0];
    if (!target) {
      const models = Object.fromEntries(stages.map((stage) => [stage, openAiModel]));
      steps.push({ action: "create", body: { kind: "OpenAi", apiKey: openAiKey, models }, why: `OpenAI configuration (model ${openAiModel}) for OPENAI_API_KEY` });
    } else if (target.status !== "Active") {
      steps.push({ action: "enable", id: target.id, why: `OpenAI configuration${key4(target)} of OPENAI_API_KEY and OPENAI_MODEL (it was ${target.status})` });
    } else {
      notes.push(`OpenAI configuration ${target.id}${key4(target)} of OPENAI_API_KEY and OPENAI_MODEL is active.`);
    }

    for (const other of openAi.filter((p) => p !== target && p.status === "Active")) {
      steps.push({ action: "disable", id: other.id, why: `previous OpenAI configuration${key4(other)}: OPENAI_API_KEY or OPENAI_MODEL in .env is another one` });
    }
  } else {
    if (openAiModel) notes.push("OPENAI_MODEL is set without OPENAI_API_KEY; it is ignored.");
    for (const active of openAi.filter((p) => p.status === "Active")) {
      notes.push(`OPENAI_API_KEY is empty; OpenAI configuration ${active.id}${key4(active)} stays active with the key stored for it. Disable or delete it in the admin console (AI providers) if it should not be used.`);
    }
  }

  return { steps, notes };
}

const env = process.env;
const settings = {
  apiUrl: env.API_URL ?? "http://api:8080",
  tokenUrl: env.TOKEN_URL ?? "http://keycloak:8080/realms/docproc/protocol/openid-connect/token",
  clientId: env.CLIENT_ID ?? "docproc-admin",
  username: env.ADMIN_USERNAME ?? "superadmin",
  password: env.ADMIN_PASSWORD ?? "superadmin",
};

const log = (message) => console.log(`llm-setup: ${message}`);

async function retry(what, action, attempts = 30) {
  for (let attempt = 1; ; attempt++) {
    try {
      return await action();
    } catch (error) {
      if (attempt >= attempts) throw new Error(`${what}: ${error.message}`);
      await new Promise((resolve) => setTimeout(resolve, 2_000));
    }
  }
}

async function token() {
  const { clientId, username, password } = settings;
  const body = new URLSearchParams({ client_id: clientId, grant_type: "password", username, password, scope: "openid" });
  const response = await fetch(settings.tokenUrl, { method: "POST", body });
  if (!response.ok) throw new Error(`token request answered ${response.status} ${await response.text()}`);
  return (await response.json()).access_token;
}

async function api(accessToken, method, path, body) {
  const response = await fetch(`${settings.apiUrl}/api/v1${path}`, {
    method,
    headers: { authorization: `Bearer ${accessToken}`, ...(body === undefined ? {} : { "content-type": "application/json" }) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  if (!response.ok) throw new Error(`${method} ${path} answered ${response.status} ${text}`);
  return text ? JSON.parse(text) : undefined;
}

async function main() {
  const keys = {
    anthropicKey: (env.ANTHROPIC_API_KEY ?? "").trim(),
    openAiKey: (env.OPENAI_API_KEY ?? "").trim(),
    openAiModel: (env.OPENAI_MODEL ?? "").trim(),
  };
  // Checked before signing in, so a missing model fails at once.
  plan([], keys);

  const accessToken = await retry("signing in as the platform admin", token);
  const providers = await retry("listing the platform LLM providers", () => api(accessToken, "GET", "/llm/providers"));
  const { steps, notes } = plan(providers, keys);
  for (const note of notes) log(note);
  for (const step of steps) {
    if (step.action === "create") {
      const created = await api(accessToken, "POST", "/llm/providers", step.body);
      log(`created the ${step.why}: ${created.id}${key4(created)}.`);
    } else {
      await api(accessToken, "POST", `/llm/providers/${step.id}:${step.action}`);
      log(`${step.action}d ${step.id}, the ${step.why}.`);
    }
  }

  if (!keys.anthropicKey && !keys.openAiKey && steps.length === 0) {
    log("no ANTHROPIC_API_KEY or OPENAI_API_KEY set; nothing to configure. E-invoice XML is processed without an LLM.");
  }

  const current = inUse(steps.length === 0 ? providers : await api(accessToken, "GET", "/llm/providers"));
  log(current
    ? `in use for tenants without their own provider: ${current.kind} ${current.id}${key4(current)} (the newest active platform configuration).`
    : "no platform configuration is active: LLM stages fail until one is enabled in the admin console (AI providers).");
}

// A failure is reported but does not fail the service: the admin console waits for this one-shot to complete, and a
// mistyped key must not keep the console (or `docker compose up --wait`) from starting. Fix .env and run
// `docker compose up -d llm-setup` again.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    console.error(`llm-setup: FAILED: ${error.message}`);
    console.error("llm-setup: fix the keys in .env and run `docker compose up -d llm-setup` again, or configure the provider in the admin console (AI providers).");
  });
}
