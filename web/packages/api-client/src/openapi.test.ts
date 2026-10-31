import { describe, expect, it } from "vitest";
import committed from "../openapi/v1.json";
import { stripServers } from "../../../scripts/strip-openapi-servers.mjs";

// web/scripts/export-openapi.sh writes openapi/v1.json; the API it downloads from names its own host and port in
// `servers`, which must not reach the committed contract (an export on OPENAPI_EXPORT_PORT would fail CI's drift check).

const served = (servers: string) => `{
  "openapi": "3.1.1",
  "info": {
    "title": "DocProc.Api | v1",
    "version": "1.0.0"
  },
${servers}  "paths": {
    "/api/v1/version": {
      "get": {
        "responses": {
          "404": {
            "description": "Not Found"
          },
          "400": {
            "description": "Bad Request"
          }
        }
      }
    }
  }
}`;

const oneServer = (port: number) => `  "servers": [
    {
      "url": "http://127.0.0.1:${port}/"
    }
  ],
`;

describe("the committed OpenAPI document", () => {
  it("names no server, so it does not depend on the port it was exported from", () => {
    expect(committed).not.toHaveProperty("servers");
    expect(Object.keys(committed.paths)).toContain("/api/v1/me");
  });
});

describe("stripServers", () => {
  it("gives the same text whatever port the API was exported from", () => {
    expect(stripServers(served(oneServer(5199)))).toBe(stripServers(served(oneServer(5349))));
    expect(stripServers(served(oneServer(5199)))).toBe(served(""));
  });

  it("keeps the member order, which JSON.parse would change for integer-like keys", () => {
    const stripped = stripServers(served(oneServer(5199)));

    expect(stripped.indexOf('"404"')).toBeLessThan(stripped.indexOf('"400"'));
  });

  it("removes several servers, and a servers entry that is the last member", () => {
    const two = `  "servers": [
    {
      "url": "http://127.0.0.1:5199/"
    },
    {
      "url": "https://localhost:5101/"
    }
  ],
`;
    expect(stripServers(served(two))).toBe(served(""));

    const last = `{
  "openapi": "3.1.1",
  "servers": [
    {
      "url": "http://127.0.0.1:5199/"
    }
  ]
}`;
    expect(stripServers(last)).toBe(`{
  "openapi": "3.1.1"
}`);
  });

  it("leaves a document without servers untouched", () => {
    expect(stripServers(served(""))).toBe(served(""));
  });

  it("refuses a layout it cannot cut cleanly instead of writing a changed contract", () => {
    expect(() => stripServers('{"openapi":"3.1.1","servers":[{"url":"http://127.0.0.1:5199/"}]}')).toThrow(/unexpected layout/);
  });
});
