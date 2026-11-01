import type { ReactNode } from "react";
import type { LucideIcon } from "lucide-react";
import type { Policy, Role } from "../auth/policies";

/** One sidebar entry. Apps keep their list in `src/nav.ts`; screen agents append to it. */
export interface NavItem {
  /** Route path, e.g. `/documents`. */
  to: string;
  /** i18n key with namespace, e.g. `portal:nav.inbox`. */
  labelKey: string;
  icon?: LucideIcon;
  /** Hidden unless the signed-in user satisfies this policy (API `permissions`). */
  policy?: Policy;
  /** Hidden unless the user holds one of these roles. */
  roles?: Role[];
  /** Active only on this exact path (default for `/`); otherwise on the path and anything below it. */
  exact?: boolean;
  /** A count or marker rendered at the end of the entry. */
  badge?: ReactNode;
}

export interface NavSection {
  /** i18n key for the group label; omit for an unlabelled group. */
  labelKey?: string;
  items: NavItem[];
}

export type Nav = NavItem[] | NavSection[];

export function isNavSection(value: NavItem | NavSection): value is NavSection {
  return "items" in value && Array.isArray((value as NavSection).items);
}

export function toSections(nav: Nav): NavSection[] {
  if (nav.length === 0) return [];
  return isNavSection(nav[0]!) ? (nav as NavSection[]) : [{ items: nav as NavItem[] }];
}

export function isActivePath(pathname: string, item: NavItem): boolean {
  const target = item.to.replace(/\/+$/, "") || "/";
  const current = pathname.replace(/\/+$/, "") || "/";
  if (item.exact || target === "/") return current === target;
  return current === target || current.startsWith(`${target}/`);
}
