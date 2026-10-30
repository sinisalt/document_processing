/**
 * Where the development bearer token lives between page loads: `sessionStorage`, so it dies with the tab and never
 * outlives the browser session the way `localStorage` would. The OIDC mode keeps its own state through oidc-client-ts.
 */
export interface StoredToken {
  accessToken: string;
  /** Epoch milliseconds after which the token must not be used. */
  expiresAt: number;
  /** The dev user the token was issued for (shown while `/me` loads). */
  username?: string;
  displayName?: string;
}

export interface SaveTokenInput {
  accessToken: string;
  /** Lifetime in seconds, as the OAuth token response carries it. */
  expiresIn: number;
  username?: string;
  displayName?: string;
}

export interface TokenStore {
  /** The stored token, or null when there is none, it is unreadable, or it has expired (an expired token is removed). */
  load(): StoredToken | null;
  save(input: SaveTokenInput): StoredToken;
  clear(): void;
  /** Milliseconds until expiry, or 0 when there is no usable token. */
  remainingMs(): number;
}

export const DEFAULT_TOKEN_KEY = "docproc.auth.dev";

/** Safety margin: a token is treated as expired this long before the server would reject it, so a request never races the clock. */
export const EXPIRY_SKEW_MS = 30_000;

/** `Storage` access throws in some private-browsing modes; fall back to memory in that case. */
export function safeSessionStorage(): Storage | null {
  try {
    if (typeof window === "undefined" || !window.sessionStorage) return null;
    const probe = "__docproc_probe__";
    window.sessionStorage.setItem(probe, "1");
    window.sessionStorage.removeItem(probe);
    return window.sessionStorage;
  } catch {
    return null;
  }
}

export function createTokenStore(storage: Storage | null, options: { key?: string; now?: () => number } = {}): TokenStore {
  const key = options.key ?? DEFAULT_TOKEN_KEY;
  const now = options.now ?? (() => Date.now());
  let memory: StoredToken | null = null;

  const read = (): StoredToken | null => {
    if (!storage) return memory;
    try {
      const raw = storage.getItem(key);
      if (!raw) return null;
      const parsed: unknown = JSON.parse(raw);
      return isStoredToken(parsed) ? parsed : null;
    } catch {
      return null;
    }
  };

  const write = (token: StoredToken | null) => {
    memory = token;
    if (!storage) return;
    try {
      if (token) storage.setItem(key, JSON.stringify(token));
      else storage.removeItem(key);
    } catch {
      // Storage full or unavailable: the in-memory copy still serves this page.
    }
  };

  const load = (): StoredToken | null => {
    const token = read();
    if (!token) return null;
    if (token.expiresAt - EXPIRY_SKEW_MS <= now()) {
      write(null);
      return null;
    }
    return token;
  };

  return {
    load,
    save(input) {
      const token: StoredToken = {
        accessToken: input.accessToken,
        expiresAt: now() + Math.max(0, input.expiresIn) * 1000,
        ...(input.username ? { username: input.username } : {}),
        ...(input.displayName ? { displayName: input.displayName } : {}),
      };
      write(token);
      return token;
    },
    clear() {
      write(null);
    },
    remainingMs() {
      const token = load();
      return token ? Math.max(0, token.expiresAt - EXPIRY_SKEW_MS - now()) : 0;
    },
  };
}

function isStoredToken(value: unknown): value is StoredToken {
  if (typeof value !== "object" || value === null) return false;
  const v = value as Record<string, unknown>;
  return typeof v.accessToken === "string" && v.accessToken.length > 0 && typeof v.expiresAt === "number" && Number.isFinite(v.expiresAt);
}
