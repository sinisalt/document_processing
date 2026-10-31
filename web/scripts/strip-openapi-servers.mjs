// Removes the top-level `servers` entry from the OpenAPI document the API serves. ASP.NET fills it from the request's
// host and port, so an export on another port (OPENAPI_EXPORT_PORT) would otherwise rewrite the committed contract and
// fail CI's drift check, and the published contract would advertise a meaningless localhost server.
//
// The entry is cut out of the text instead of re-serialising the document: JSON.parse moves integer-like keys ("400",
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

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [input, output] = process.argv.slice(2);
  if (!input || !output) {
    console.error("usage: node strip-openapi-servers.mjs <in.json> <out.json>");
    process.exit(2);
  }
  writeFileSync(output, stripServers(readFileSync(input, "utf8")));
}
