import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from "react";

export interface TenantSwitcherContextValue {
  /** The tenant a SuperAdmin acts inside (sent as `X-Tenant-Id`), or null for platform scope. */
  tenantId: string | null;
  setTenantId: (tenantId: string | null) => void;
}

const TenantSwitcherContext = createContext<TenantSwitcherContextValue | null>(null);

export const TENANT_OVERRIDE_KEY = "docproc.tenantOverride";

function readStored(): string | null {
  try {
    return window.sessionStorage.getItem(TENANT_OVERRIDE_KEY);
  } catch {
    return null;
  }
}

/**
 * Holds the SuperAdmin tenant override. It sits *outside* `ApiProvider` so the client middleware can read it, and the
 * `ApiProvider` clears the query cache when it changes (every tenant-scoped list means something else afterwards).
 */
export function TenantSwitcherProvider({ children }: { children: ReactNode }) {
  const [tenantId, setState] = useState<string | null>(readStored);
  const setTenantId = useCallback((next: string | null) => {
    setState(next);
    try {
      if (next) window.sessionStorage.setItem(TENANT_OVERRIDE_KEY, next);
      else window.sessionStorage.removeItem(TENANT_OVERRIDE_KEY);
    } catch {
      // Not persisted: the choice still holds for this page.
    }
  }, []);
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
