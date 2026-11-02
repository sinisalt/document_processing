/** The OpenAPI document's text without its top-level `servers` entry (see strip-openapi-servers.mjs). */
export function stripServers(text: string): string;

/** The OpenAPI document's text with the members of `components.schemas` in ordinal order (see strip-openapi-servers.mjs). */
export function sortSchemas(text: string): string;

/** What web/scripts/export-openapi.sh writes: `stripServers`, then `sortSchemas`. */
export function normalize(text: string): string;
