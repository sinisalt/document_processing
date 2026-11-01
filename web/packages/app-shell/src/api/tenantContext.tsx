import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useAuthSession } from "../auth/AuthProvider";

export interface TenantSwitcherContextValue {
  /** The tenant a SuperAdmin acts inside (sent as `X-Tenant-Id`), or null for platform scope. */
  tenantId: string | null;
  setTenantId: (tenantId: string | null) => void;
}

const TenantSwitcherContext = createContext<TenantSwitcherContextValue | null>(null);

export const TENANT_OVERRIDE_KEY = "docproc.tenantOverride";

/** An override belongs to the user who chose it: `owner` is the `sub` of the access token it was set under. */
interface StoredOverride {
  owner: string;
  tenantId: string;
}

/**
 * The `sub` claim of a JWT access token (dev issuer and OIDC alike), or null when there is no token or it is not a
 * readable JWT. Only used to tell one signed-in user from the next; the API, not this, decides what a token may do.
 */
export function tokenSubject(accessToken: string | null): string | null {
  if (!accessToken) return null;
  const payload = accessToken.split(".")[1];
  if (!payload) return null;
  try {
    const base64 = payload.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(payload.length / 4) * 4, "=");
    const bytes = Uint8Array.from(window.atob(base64), (c) => c.charCodeAt(0));
    const claims: unknown = JSON.parse(new TextDecoder().decode(bytes));
    const sub = claims && typeof claims === "object" ? (claims as Record<string, unknown>).sub : undefined;
    return typeof sub === "string" && sub.length > 0 ? sub : null;
  } catch {
    return null;
  }
}

function readStored(): StoredOverride | null {
  try {
    const raw = window.sessionStorage.getItem(TENANT_OVERRIDE_KEY);
    if (!raw) return null;
    const parsed: unknown = JSON.parse(raw);
    if (parsed && typeof parsed === "object") {
      const { owner, tenantId } = parsed as Record<string, unknown>;
      if (typeof owner === "string" && owner && typeof tenantId === "string" && tenantId) return { owner, tenantId };
    }
    return null;
  } catch {
    // Unreadable, or the unowned plain value an older build stored: nobody's override.
    return null;
  }
}

function writeStored(value: StoredOverride | null) {
  try {
    if (value) window.sessionStorage.setItem(TENANT_OVERRIDE_KEY, JSON.stringify(value));
    else window.sessionStorage.removeItem(TENANT_OVERRIDE_KEY);
  } catch {
    // Not persisted: the choice still holds for this page.
  }
}

/**
 * Holds the SuperAdmin tenant override. It sits *outside* `ApiProvider` so the client middleware can read it, and the
 * `ApiProvider` clears the query cache when it changes (every tenant-scoped list means something else afterwards).
 *
 * The override is bound to the signed-in user (the token's `sub`): it is dropped on sign-out and whenever another user
 * signs in, and a stored override is only picked up again by the user who set it (a reload keeps the SuperAdmin's
 * scope). Otherwise the next user of the tab would send the old `X-Tenant-Id` on every call, `/me` included, and a
 * non-SuperAdmin would be locked out by the API's 403 with no switcher to clear it.
 */
export function TenantSwitcherProvider({ children }: { children: ReactNode }) {
  const session = useAuthSession();
  const owner = tokenSubject(session.accessToken);
  const [stored, setStored] = useState<StoredOverride | null>(readStored);

  // Sign-out (an owner, then none) or a different user: the old override is nobody's any more. A first token after
  // loading (none, then an owner) keeps a stored override, which only shows if it is that user's.
  const previousOwner = useRef(owner);
  useEffect(() => {
    const before = previousOwner.current;
    previousOwner.current = owner;
    const signedOut = before !== null && before !== owner;
    const foreign = owner !== null && stored !== null && stored.owner !== owner;
    if (signedOut || foreign) {
      setStored(null);
      writeStored(null);
    }
  }, [owner, stored]);

  const ownerRef = useRef(owner);
  ownerRef.current = owner;
  const setTenantId = useCallback((next: string | null) => {
    const current = ownerRef.current;
    const value = next && current ? { owner: current, tenantId: next } : null;
    setStored(value);
    writeStored(value);
  }, []);

  const tenantId = stored !== null && owner !== null && stored.owner === owner ? stored.tenantId : null;
  const value = useMemo(() => ({ tenantId, setTenantId }), [tenantId, setTenantId]);
  return <TenantSwitcherContext.Provider value={value}>{children}</TenantSwitcherContext.Provider>;
}

/** Null outside a `TenantSwitcherProvider`. */
export function useTenantSwitcherOptional(): TenantSwitcherContextValue | null {
  return useContext(TenantSwitcherContext);
}

export function useTenantSwitcher(): TenantSwitcherContextValue {
  const value = useContext(TenantSwitcherContext);
  if (!value) throw new Error("useTenantSwitcher must be used inside <TenantSwitcherProvider>.");
  return value;
}
