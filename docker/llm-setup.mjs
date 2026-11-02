// One-shot `llm-setup` service of docker-compose.yml (docs/09 "With Docker Compose", "LLM keys"). Turns the LLM keys
// of .env into active platform provider configurations through the API's own admin endpoints, signed in as the
// realm's superadmin, exactly as an operator would in the admin console (LLM providers):
//
// - ANTHROPIC_API_KEY: the API and the worker read it as LLM_SECRET_LLM_PLATFORM_EU, the key of the seeded platform
//   Anthropic configuration (secret reference llm-platform-eu, DevSeed), so this script only enables that
//   configuration (POST /api/v1/llm/providers/{id}:enable).
// - OPENAI_API_KEY with OPENAI_MODEL: creates a platform OpenAI configuration with that model for every stage
//   (POST /api/v1/llm/providers; the key goes to the shared secret store), once per key.
//
// The newest active platform configuration serves every tenant without its own; the start-up log of the API names it.
// Without a key the script does nothing: e-invoice XML never needs an LLM, the demo PDFs are answered by the replay
// fixtures, and any other PDF ends Failed until a key is configured. Idempotent: a rerun changes nothing.
// Inside the compose network the API is http://api:8080 and Keycloak http://keycloak:8080; the token's issuer is
// still http://localhost:8080/realms/docproc (KC_HOSTNAME), which is the one the API accepts.

const env = process.env;
const apiUrl = env.API_URL ?? "http://api:8080";
const tokenUrl = env.TOKEN_URL ?? "http://keycloak:8080/realms/docproc/protocol/openid-connect/token";
const clientId = env.CLIENT_ID ?? "docproc-admin";
const username = env.ADMIN_USERNAME ?? "superadmin";
const password = env.ADMIN_PASSWORD ?? "superadmin";
const anthropicKey = (env.ANTHROPIC_API_KEY ?? "").trim();
const openAiKey = (env.OPENAI_API_KEY ?? "").trim();
const openAiModel = (env.OPENAI_MODEL ?? "").trim();

/** DevSeed.PlatformAnthropicConfig: the seeded platform Anthropic configuration whose key is LLM_SECRET_LLM_PLATFORM_EU. */
const seededAnthropicConfig = "00000000-0003-4000-8000-000000000001";
const stages = ["Classify", "Split", "Extract", "Synthesize"];

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
  const body = new URLSearchParams({ client_id: clientId, grant_type: "password", username, password, scope: "openid" });
  const response = await fetch(tokenUrl, { method: "POST", body });
  if (!response.ok) throw new Error(`token request answered ${response.status} ${await response.text()}`);
  return (await response.json()).access_token;
}

async function api(accessToken, method, path, body) {
  const response = await fetch(`${apiUrl}/api/v1${path}`, {
    method,
    headers: { authorization: `Bearer ${accessToken}`, ...(body === undefined ? {} : { "content-type": "application/json" }) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  if (!response.ok) throw new Error(`${method} ${path} answered ${response.status} ${text}`);
  return text ? JSON.parse(text) : undefined;
}

async function main() {
  if (!anthropicKey && !openAiKey) {
    log("no ANTHROPIC_API_KEY or OPENAI_API_KEY set; nothing to configure. E-invoice XML is processed without an LLM.");
    return;
  }

  if (openAiKey && !openAiModel) {
    throw new Error("OPENAI_API_KEY is set but OPENAI_MODEL is not: set the OpenAI model id to use for every stage in .env.");
  }

  const accessToken = await retry("signing in as the platform admin", token);
  const providers = await retry("listing the platform LLM providers", () => api(accessToken, "GET", "/llm/providers"));

  if (anthropicKey) {
    const seeded = providers.find((p) => p.id === seededAnthropicConfig);
    if (!seeded) {
      log(`the seeded Anthropic configuration ${seededAnthropicConfig} does not exist (is Persistence__SeedDevData off?); add a provider in the admin console instead.`);
    } else if (seeded.status === "Active") {
      log(`Anthropic configuration ${seeded.id} is already active; its key is ANTHROPIC_API_KEY (LLM_SECRET_LLM_PLATFORM_EU).`);
    } else {
      await api(accessToken, "POST", `/llm/providers/${seeded.id}:enable`);
      log(`enabled the Anthropic configuration ${seeded.id}; its key is ANTHROPIC_API_KEY (LLM_SECRET_LLM_PLATFORM_EU).`);
    }
  }

  if (openAiKey) {
    const openAi = providers.filter((p) => p.kind === "OpenAi");
    const same = openAi.find((p) => p.keyLast4 === openAiKey.slice(-4) && p.models?.Extract === openAiModel);
    if (same) {
      log(`OpenAI configuration ${same.id} for this key and model exists (${same.status}); nothing to do.`);
    } else {
      const models = Object.fromEntries(stages.map((stage) => [stage, openAiModel]));
      const created = await api(accessToken, "POST", "/llm/providers", { kind: "OpenAi", apiKey: openAiKey, models });
      log(`created the OpenAI configuration ${created.id} (model ${openAiModel}, key …${created.keyLast4}).`);
      for (const previous of openAi.filter((p) => p.status === "Active")) {
        await api(accessToken, "POST", `/llm/providers/${previous.id}:disable`);
        log(`disabled the previous OpenAI configuration ${previous.id} (key …${previous.keyLast4}).`);
      }
    }
  }

  const active = (await api(accessToken, "GET", "/llm/providers")).filter((p) => p.status === "Active");
  active.sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
  if (active.length > 0) log(`in use for tenants without their own provider: ${active[0].kind} ${active[0].id} (the newest active platform configuration).`);
}

// A failure is reported but does not fail the service: the admin console waits for this one-shot to complete, and a
// mistyped key must not keep the console (or `docker compose up --wait`) from starting. Fix .env and run
// `docker compose up -d llm-setup` again.
main().catch((error) => {
  console.error(`llm-setup: FAILED: ${error.message}`);
  console.error("llm-setup: fix the keys in .env and run `docker compose up -d llm-setup` again, or configure the provider in the admin console (AI providers).");
});
