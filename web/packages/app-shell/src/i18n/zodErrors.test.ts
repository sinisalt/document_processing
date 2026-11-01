import { z } from "zod";
import { createI18n, i18n } from "./index";

createI18n({ lng: "en" });

function messages(schema: z.ZodType, value: unknown): string[] {
  const result = schema.safeParse(value);
  return result.success ? [] : result.error.issues.map((issue) => issue.message);
}

describe("zod's global error map", () => {
  afterEach(async () => {
    await i18n.changeLanguage("en");
  });

  it("translates checks that carry no message, in the current language", async () => {
    const schema = z.object({ name: z.string().trim().min(1).max(5), count: z.coerce.number().int().min(1).max(10), tags: z.array(z.string()).min(1), kind: z.enum(["a", "b"]) });
    expect(messages(schema.shape.name, "")).toEqual(["Required."]);
    expect(messages(schema.shape.name, "toolong")).toEqual(["At most 5 characters."]);
    expect(messages(schema.shape.count, "1.5")).toEqual(["A whole number."]);
    expect(messages(schema.shape.count, "11")).toEqual(["At most 10."]);
    expect(messages(schema.shape.tags, [])).toEqual(["Pick at least 1."]);
    expect(messages(schema.shape.kind, "c")).toEqual(["Pick one of the options."]);
    expect(messages(z.string().email(), "x")).toEqual(["An e-mail address."]);
    expect(messages(z.string(), undefined)).toEqual(["Required."]);

    await i18n.changeLanguage("de");
    expect(messages(schema.shape.name, "")).toEqual(["Pflichtfeld."]);
    expect(messages(schema.shape.name, "toolong")).toEqual(["Höchstens 5 Zeichen."]);
  });

  it("leaves a schema's own message alone", () => {
    expect(messages(z.string().min(1, { error: "Own text." }), "")).toEqual(["Own text."]);
  });
});
