import { afterEach, beforeAll, describe, expect, it } from "vitest";
import i18n from "i18next";
import { createI18n } from "../i18n";
import en from "../i18n/en/common.json";
import de from "../i18n/de/common.json";
import { ApiError, localizedFieldErrors } from "./ApiError";

/** A 400 as `EndpointSupport.Errors` sends it: English `errors`, and `errorCodes` for the checks that have a code. */
const problem = {
  title: "One or more validation errors occurred.",
  status: 400,
  errors: {
    name: ["name is required and at most 200 characters."],
    retentionYears: ["Retention must be between 1 and 30 years."],
    alwaysReviewSupplierIds: ["Unknown supplier id(s): 0f8a."],
    slug: ["Slug must be lowercase letters, digits and single dashes, at most 64 characters."],
  },
  errorCodes: {
    name: { code: "requiredMaxLength", params: { maxLength: "200" } },
    retentionYears: { code: "range", params: { min: "1", max: "30" } },
    // A code from a newer API, and a known code without the parameter its text needs.
    slug: { code: "slug", params: {} },
  },
};

beforeAll(() => {
  createI18n({ lng: "en" });
});

afterEach(async () => {
  await i18n.changeLanguage("en");
});

describe("ApiError field errors", () => {
  it("are in the UI language where the API sent a code", async () => {
    await i18n.changeLanguage("de");
    const error = new ApiError(400, problem, "/api/v1/settings");

    expect(error.fieldErrors.name).toEqual(["Pflichtfeld, höchstens 200 Zeichen."]);
    expect(error.fieldErrors.retentionYears).toEqual(["Zwischen 1 und 30."]);
    // The toast reads the message: its first field error is translated too.
    expect(error.message).toBe("name: Pflichtfeld, höchstens 200 Zeichen.");
  });

  it("keep the English text for an error without a code, or a code whose text cannot be filled", () => {
    const errors = localizedFieldErrors({ ...problem, errorCodes: { ...problem.errorCodes, name: { code: "fromTheFuture", params: {} } } }, "de");
    expect(errors.alwaysReviewSupplierIds).toEqual(["Unknown supplier id(s): 0f8a."]);
    expect(errors.slug).toEqual(["Slug must be lowercase letters, digits and single dashes, at most 64 characters."]);
    expect(errors.name).toEqual(["name is required and at most 200 characters."]);
  });

  it("are the API's text when it sends no codes at all", () => {
    const error = new ApiError(400, { status: 400, errors: { reason: ["A reason is required."] } }, "/x");
    expect(error.fieldErrors).toEqual({ reason: ["A reason is required."] });
  });
});

describe("the apiErrors texts", () => {
  it("have the same codes and placeholders in en and de", () => {
    const placeholders = (text: string) => [...text.matchAll(/\{\{(\w+)\}\}/g)].map((m) => m[1]).sort();
    expect(Object.keys(de.apiErrors).sort()).toEqual(Object.keys(en.apiErrors).sort());
    for (const [code, text] of Object.entries(en.apiErrors)) expect([code, placeholders(de.apiErrors[code as keyof typeof de.apiErrors])]).toEqual([code, placeholders(text)]);
  });

  it("cover every code EndpointSupport.ValidationCodes defines", () => {
    // src/DocProc.Api/Endpoints/EndpointSupport.cs, ValidationCodes.
    const codes = ["requiredMaxLength", "maxLength", "notBlank", "currencyCode", "countryCode", "timeZone", "slug", "range", "positive", "httpUrl"];
    expect(codes.filter((code) => !(code in en.apiErrors))).toEqual([]);
  });
});
