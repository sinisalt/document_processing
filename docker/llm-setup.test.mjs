// Unit tests of docker/llm-setup.mjs's decisions (no API, no network): `node --test docker/llm-setup.test.mjs`
// (CI's compose job runs it). The admin API is simulated by `apply`, which does what POST /llm/providers and
// :enable / :disable do to the list GET /llm/providers returns.

import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { test } from "node:test";
import { fingerprint, inUse, plan, seededAnthropicConfig, stages } from "./llm-setup.mjs";

let clock = 0;
const now = () => new Date(Date.UTC(2026, 10, 2, 12, 0, clock++)).toISOString();
const allStages = (model) => Object.fromEntries(stages.map((stage) => [stage, model]));

function seed() {
  clock = 0;
  return [
    { id: seededAnthropicConfig, kind: "Anthropic", status: "Disabled", keyLast4: null, keyFingerprint: null, models: allStages("seeded-model"), updatedAt: now() },
    { id: "replay", kind: "Replay", status: "Active", keyLast4: null, keyFingerprint: null, models: allStages("seeded-model"), updatedAt: now() },
  ];
}

function apply(providers, steps) {
  let next = 0;
  for (const step of steps) {
    if (step.action === "create") {
      const key = step.body.apiKey;
      providers.push({
        id: `openai-${providers.length}-${next++}`,
        kind: step.body.kind,
        status: "Active",
        endpoint: null,
        keyLast4: key.slice(-4),
        keyFingerprint: createHash("sha256").update(key).digest("hex").slice(0, 16),
        models: { ...step.body.models },
        updatedAt: now(),
      });
    } else {
      const provider = providers.find((p) => p.id === step.id);
      provider.status = step.action === "enable" ? "Active" : "Disabled";
      provider.updatedAt = now();
    }
  }
  return providers;
}

/** One `docker compose up` with these .env keys: plan, then apply. */
const run = (providers, keys) => apply(providers, plan(providers, keys).steps);

test("the fingerprint is the API's: the first 16 hex digits of the key's SHA-256", () => {
  // `printf %s sk-test-oooo | sha256sum | cut -c1-16`; LlmAdminEndpointsTests holds the API to the same rule.
  assert.equal(fingerprint("sk-test-oooo"), "b5a14d192006a16b");
  assert.equal(fingerprint("sk-tést-ü"), createHash("sha256").update(Buffer.from("sk-tést-ü", "utf8")).digest("hex").slice(0, 16));
  assert.notEqual(fingerprint("sk-A-same"), fingerprint("sk-B-same"));
});

test("without keys a fresh stack is left as seeded", () => {
  const { steps, notes } = plan(seed(), {});
  assert.deepEqual(steps, []);
  assert.deepEqual(notes, []);
});

test("an Anthropic key enables the seeded configuration once", () => {
  const providers = seed();
  assert.deepEqual(plan(providers, { anthropicKey: "sk-ant-1234" }).steps.map((s) => [s.action, s.id]), [["enable", seededAnthropicConfig]]);
  run(providers, { anthropicKey: "sk-ant-1234" });
  assert.equal(inUse(providers).id, seededAnthropicConfig);
  assert.deepEqual(plan(providers, { anthropicKey: "sk-ant-1234" }).steps, []);
});

test("removing the Anthropic key disables the seeded configuration it no longer has a key for", () => {
  const providers = run(seed(), { anthropicKey: "sk-ant-1234" });

  const { steps } = plan(providers, {});
  assert.deepEqual(steps.map((s) => [s.action, s.id]), [["disable", seededAnthropicConfig]]);
  run(providers, {});
  assert.equal(inUse(providers).id, "replay");
  assert.deepEqual(plan(providers, {}).steps, []);
});

test("an OpenAI key creates a configuration with the model for every stage", () => {
  const { steps } = plan(seed(), { openAiKey: "sk-test-oooo", openAiModel: "model-x" });
  assert.equal(steps.length, 1);
  assert.equal(steps[0].action, "create");
  assert.deepEqual(steps[0].body, { kind: "OpenAi", apiKey: "sk-test-oooo", models: allStages("model-x") });
});

test("switching the OpenAI key back and forth always leaves the key of .env in use", () => {
  const providers = seed();
  const oooo = { openAiKey: "sk-test-oooo", openAiModel: "model-x" };
  const pppp = { openAiKey: "sk-test-pppp", openAiModel: "model-x" };

  run(providers, oooo);
  const first = inUse(providers);
  assert.equal(first.keyFingerprint, fingerprint(oooo.openAiKey));

  run(providers, pppp);
  const second = inUse(providers);
  assert.equal(second.keyFingerprint, fingerprint(pppp.openAiKey));
  assert.equal(providers.find((p) => p.id === first.id).status, "Disabled");

  // Back to the first key: its disabled configuration is enabled again (no third one) and the second is disabled.
  const { steps } = plan(providers, oooo);
  assert.deepEqual(steps.map((s) => [s.action, s.id]), [["enable", first.id], ["disable", second.id]]);
  run(providers, oooo);
  assert.equal(inUse(providers).id, first.id);
  assert.equal(providers.filter((p) => p.kind === "OpenAi").length, 2);
  assert.deepEqual(plan(providers, oooo).steps, []);
});

test("a different key with the same last four characters gets a configuration of its own", () => {
  const providers = run(seed(), { openAiKey: "sk-first-key-abcd", openAiModel: "model-x" });
  const first = inUse(providers);

  const { steps } = plan(providers, { openAiKey: "sk-other-key-abcd", openAiModel: "model-x" });
  assert.deepEqual(steps.map((s) => s.action), ["create", "disable"]);
  assert.equal(steps[0].body.apiKey, "sk-other-key-abcd");
  assert.equal(steps[1].id, first.id);
});

test("a different model for the same key is a new configuration", () => {
  const providers = run(seed(), { openAiKey: "sk-test-oooo", openAiModel: "model-x" });
  assert.deepEqual(plan(providers, { openAiKey: "sk-test-oooo", openAiModel: "model-y" }).steps.map((s) => s.action), ["create", "disable"]);
});

test("a configuration with its own endpoint or another model per stage is not the one of .env", () => {
  const providers = seed();
  const fp = fingerprint("sk-test-oooo");
  providers.push(
    { id: "proxy", kind: "OpenAi", status: "Disabled", endpoint: "https://proxy.example", keyLast4: "oooo", keyFingerprint: fp, models: allStages("model-x"), updatedAt: now() },
    { id: "mixed", kind: "OpenAi", status: "Disabled", endpoint: null, keyLast4: "oooo", keyFingerprint: fp, models: { ...allStages("model-x"), Classify: "model-mini" }, updatedAt: now() },
  );
  assert.deepEqual(plan(providers, { openAiKey: "sk-test-oooo", openAiModel: "model-x" }).steps.map((s) => s.action), ["create"]);
});

test("another active OpenAI configuration is disabled while the one of .env is active", () => {
  const providers = run(seed(), { openAiKey: "sk-test-oooo", openAiModel: "model-x" });
  const mine = inUse(providers);
  providers.push({ id: "console", kind: "OpenAi", status: "Active", endpoint: null, keyLast4: "zzzz", keyFingerprint: fingerprint("sk-zzzz"), models: allStages("model-x"), updatedAt: now() });
  assert.equal(inUse(providers).id, "console");

  assert.deepEqual(plan(providers, { openAiKey: "sk-test-oooo", openAiModel: "model-x" }).steps.map((s) => [s.action, s.id]), [["disable", "console"]]);
  run(providers, { openAiKey: "sk-test-oooo", openAiModel: "model-x" });
  assert.equal(inUse(providers).id, mine.id);
});

test("without an OpenAI key the OpenAI configurations keep their stored key and are only reported", () => {
  const providers = run(seed(), { openAiKey: "sk-test-oooo", openAiModel: "model-x" });
  const { steps, notes } = plan(providers, {});
  assert.deepEqual(steps, []);
  assert.equal(notes.length, 1);
  assert.match(notes[0], /OPENAI_API_KEY is empty; OpenAI configuration .* \(key …oooo\) stays active/);
});

test("with both keys changed in one run the OpenAI configuration is the newest and in use", () => {
  const providers = seed();
  const { steps } = plan(providers, { anthropicKey: "sk-ant-1234", openAiKey: "sk-test-oooo", openAiModel: "model-x" });
  assert.deepEqual(steps.map((s) => s.action), ["enable", "create"]);
  run(providers, { anthropicKey: "sk-ant-1234", openAiKey: "sk-test-oooo", openAiModel: "model-x" });
  assert.equal(inUse(providers).kind, "OpenAi");
});

test("an OpenAI key without a model is refused before anything changes", () => {
  assert.throws(() => plan(seed(), { openAiKey: "sk-test-oooo" }), /OPENAI_MODEL is not/);
});

test("no active configuration means nothing is in use", () => {
  const providers = seed().map((p) => ({ ...p, status: "Disabled" }));
  assert.equal(inUse(providers), undefined);
});
