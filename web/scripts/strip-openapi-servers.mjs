// Removes the top-level `servers` entry from the OpenAPI document the API serves. ASP.NET fills it from the request's
// host and port, so an export on another port (OPENAPI_EXPORT_PORT) would otherwise rewrite the committed contract and
// fail CI's drift check, and the published contract would advertise a meaningless localhost server.
//
// It also lists `components.schemas` in ordinal order (sortSchemas). ASP.NET sorts them with the culture-sensitive
// default comparer after every document transformer has run, which is ordinal only in invariant globalization mode;
// the API runs with ICU because Microsoft.Data.SqlClient refuses invariant mode, and there "CreatedApiKey" sorts between
// "CreateChannelRequest" and "CreateLlmProviderRequest". The committed contract must not depend on that either.
//
// Both edits work on the text instead of re-serialising the document: JSON.parse moves integer-like keys ("400",
// "404") to the front, which would reorder every responses object.
//
// Usage: node strip-openapi-servers.mjs <in.json> <out.json>
import { readFileSync, writeFileSync } from "node:fs";
import { pathToFileURL } from "node:url";

const serversBlock = /^ {2}"servers": \[\n(?: {4}.*\n)*? {2}\](,?)\n/m;

/**
 * @param {string} text an OpenAPI document as the API serializes it (two-space indentation)
 * @returns {string} the same text without the top-level `servers` entry
 */
export function stripServers(text) {
  const document = JSON.parse(text);
  if (!Object.hasOwn(document, "servers")) return text;

  const match = serversBlock.exec(text);
  if (!match) throw new Error("The OpenAPI document has a top-level 'servers' entry in an unexpected layout.");
  let before = text.slice(0, match.index);
  const after = text.slice(match.index + match[0].length);
  // `servers` was the last member: the member before it loses its trailing comma.
  if (match[1] === "") before = before.replace(/,\n$/, "\n");
  const stripped = before + after;

  delete document.servers;
  if (JSON.stringify(JSON.parse(stripped)) !== JSON.stringify(document)) {
    throw new Error("Removing 'servers' from the OpenAPI document changed more than that entry.");
  }
  return stripped;
}

const schemasBlock = /^ {4}"schemas": \{\n((?: {6}.*\n)*?) {4}\}(,?)\n/m;
const memberStart = /^ {6}("(?:[^"\\]|\\.)*"): /;
const ordinal = (a, b) => (a < b ? -1 : a > b ? 1 : 0);

/** The document with its schemas in ordinal order, as a string: the comparison sortSchemas checks itself with. */
const canonical = (document) =>
  JSON.stringify({
    ...document,
    components: {
      ...document.components,
      schemas: Object.fromEntries(Object.keys(document.components.schemas).sort(ordinal).map((key) => [key, document.components.schemas[key]])),
    },
  });

/**
 * @param {string} text an OpenAPI document as the API serializes it (two-space indentation)
 * @returns {string} the same text with the members of `components.schemas` in ordinal order, each member's text unchanged
 */
export function sortSchemas(text) {
  const document = JSON.parse(text);
  if (Object.keys(document.components?.schemas ?? {}).length < 2) return text;

  const match = schemasBlock.exec(text);
  if (!match) throw new Error("The OpenAPI document has 'components.schemas' in an unexpected layout.");
  const members = [];
  for (const line of match[1].split("\n").slice(0, -1)) {
    const start = memberStart.exec(line);
    if (start) members.push({ key: JSON.parse(start[1]), lines: [line] });
    else if (members.length > 0) members.at(-1).lines.push(line);
    else throw new Error("The OpenAPI document has 'components.schemas' in an unexpected layout.");
  }

  members.sort((a, b) => ordinal(a.key, b.key));
  const body = members
    .map((member, index) => {
      const lines = [...member.lines];
      lines[lines.length - 1] = lines.at(-1).replace(/,$/, "") + (index < members.length - 1 ? "," : "");
      return `${lines.join("\n")}\n`;
    })
    .join("");
  const sorted = `${text.slice(0, match.index)}    "schemas": {\n${body}    }${match[2]}\n${text.slice(match.index + match[0].length)}`;

  const result = JSON.parse(sorted);
  const keys = Object.keys(result.components.schemas);
  if (keys.join("\n") !== [...keys].sort(ordinal).join("\n") || canonical(result) !== canonical(document)) {
    throw new Error("Sorting 'components.schemas' in the OpenAPI document changed more than their order.");
  }
  return sorted;
}

/** What the export writes: the served document without `servers` and with its schemas in ordinal order. */
export function normalize(text) {
  return sortSchemas(stripServers(text));
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [input, output] = process.argv.slice(2);
  if (!input || !output) {
    console.error("usage: node strip-openapi-servers.mjs <in.json> <out.json>");
    process.exit(2);
  }
  writeFileSync(output, normalize(readFileSync(input, "utf8")));
}
