import type { ReactNode } from "react";
import type { UseQueryResult } from "@tanstack/react-query";

/**
 * What a stat card shows for a query: the value once it answered, "—" when it failed or is not run for this role
 * (a disabled query stays pending forever, so a skeleton would never go away), a skeleton while it loads.
 */
export function statValue<T>(query: Pick<UseQueryResult<T>, "data" | "isError">, enabled: boolean, render: (data: T) => ReactNode, skeleton: ReactNode): ReactNode {
  if (!enabled || query.isError) return "—";
  if (query.data !== undefined) return render(query.data);
  return skeleton;
}

export type ByStatusScope = "tenant" | "platformSwitchable" | "platform" | "ownTenant";

/**
 * Which description the "Documents by status" card shows. Only a SuperAdmin has the header's tenant switcher
 * (`TenantSwitcher`), so only they are told to pick a tenant there; an Operator also sees every tenant but has no
 * such control, and anyone else is pinned to their own tenant by the API.
 */
export function byStatusScope({ selectedTenant, isPlatform, canSwitchTenant }: { selectedTenant: string | null; isPlatform: boolean; canSwitchTenant: boolean }): ByStatusScope {
  if (selectedTenant) return "tenant";
  if (!isPlatform) return "ownTenant";
  return canSwitchTenant ? "platformSwitchable" : "platform";
}
