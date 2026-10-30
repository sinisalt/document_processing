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
