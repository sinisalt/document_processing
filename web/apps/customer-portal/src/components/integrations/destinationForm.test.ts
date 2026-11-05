import { describe, expect, it } from "vitest";
import type { Destination } from "./api";
import { configOf, emptyDestinationForm, formFieldOfConfigPath, formOf, parseHeaders } from "./destinationForm";

const destination = (transport: string, config: Record<string, unknown>): Destination => ({
  id: "d1",
  name: "erp-articles",
  transport,
  config,
  hasSecret: true,
  hasPreviousSecret: false,
  secretSetAt: null,
  status: "Active",
  lastTestAt: null,
  lastTestResult: null,
  createdAt: "2026-11-03T10:00:00Z",
  updatedAt: "2026-11-03T10:00:00Z",
});

describe("the destination form", () => {
  it("builds an http config with only what is filled", () => {
    expect(configOf({ ...emptyDestinationForm, urlTemplate: " http://mock-erp:8080/api/article-prices " })).toEqual({ config: { method: "POST", urlTemplate: "http://mock-erp:8080/api/article-prices" } });
  });

  it("builds every http setting: auth, headers, response mapping, update and the original file", () => {
    const built = configOf({
      ...emptyDestinationForm,
      method: "PUT",
      urlTemplate: "https://erp.example/api/ap",
      timeoutSeconds: "20",
      successCodes: "200, 201 202",
      authType: "oauth2ClientCredentials",
      tokenUrl: "https://erp.example/oauth/token",
      clientId: "docproc",
      scope: "ap.write",
      headers: "X-Mandant: 1000\n\nX-Source: dms",
      responseMapping: "erpId",
      updateMethod: "PATCH",
      updateUrlTemplate: "https://erp.example/api/ap/{{output.header.docNo}}",
      sendOriginal: true,
    });
    expect(built).toEqual({
      config: {
        method: "PUT",
        urlTemplate: "https://erp.example/api/ap",
        timeoutSeconds: 20,
        successCodes: [200, 201, 202],
        auth: { type: "oauth2ClientCredentials", tokenUrl: "https://erp.example/oauth/token", clientId: "docproc", scope: "ap.write" },
        headers: { "X-Mandant": "1000", "X-Source": "dms" },
        responseMapping: "erpId",
        updateMethod: "PATCH",
        updateUrlTemplate: "https://erp.example/api/ap/{{output.header.docNo}}",
        multipart: { parts: [{ name: "data", mapping: "self" }, { name: "file", content: "original" }] },
      },
    });
    expect(configOf({ ...emptyDestinationForm, authType: "apiKeyHeader", headerName: "X-Api-Key", urlTemplate: "https://x" })).toMatchObject({ config: { auth: { type: "apiKeyHeader", headerName: "X-Api-Key" } } });
    expect(configOf({ ...emptyDestinationForm, authType: "basic", username: "u", urlTemplate: "https://x" })).toMatchObject({ config: { auth: { type: "basic", username: "u" } } });
  });

  it("refuses a header line without a colon and a timeout that is not a whole number", () => {
    expect(parseHeaders("X-A: 1\nnot a header")).toBeNull();
    expect(configOf({ ...emptyDestinationForm, urlTemplate: "https://x", headers: "nope" })).toEqual({ problem: "headers" });
    expect(configOf({ ...emptyDestinationForm, urlTemplate: "https://x", timeoutSeconds: "2.5" })).toEqual({ problem: "number" });
  });

  it("builds the file drop and the pull feed", () => {
    expect(configOf({ ...emptyDestinationForm, transport: "blob", path: "erp/in", fileNameTemplate: "{{meta.documentId}}.json" })).toEqual({ config: { path: "erp/in", fileNameTemplate: "{{meta.documentId}}.json" } });
    expect(configOf({ ...emptyDestinationForm, transport: "pull" })).toEqual({ config: {} });
    expect(configOf({ ...emptyDestinationForm, transport: "pull", retentionDays: "30" })).toEqual({ config: { retentionDays: 30 } });
  });

  it("reads a stored destination back into the form, the same config again", () => {
    const config = { method: "POST", urlTemplate: "http://mock-erp:8080/api/article-prices", auth: { type: "bearer" }, headers: { "X-A": "1" }, multipart: { parts: [{ name: "data", mapping: "self" }, { name: "file", content: "original" }] }, successCodes: [202] };
    const values = formOf(destination("http", config));
    expect(values).toMatchObject({ name: "erp-articles", authType: "bearer", headers: "X-A: 1", sendOriginal: true, successCodes: "202", secret: "" });
    expect(configOf(values)).toEqual({ config });
    expect(formOf(null)).toEqual(emptyDestinationForm);
  });

  it("places the API's config errors on their fields", () => {
    expect(formFieldOfConfigPath("config.urlTemplate")).toBe("urlTemplate");
    expect(formFieldOfConfigPath("config.auth.headerName")).toBe("headerName");
    expect(formFieldOfConfigPath("config.headers.Authorization")).toBe("headers");
    expect(formFieldOfConfigPath("config.unknown")).toBeNull();
  });
});
