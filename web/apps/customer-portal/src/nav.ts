import { Building2, ClipboardList, Inbox, LayoutDashboard, Settings } from "lucide-react";
import { Policies, type AuthUser, type NavItem } from "@docproc/app-shell";

/**
 * Sidebar entries of the customer portal, in display order. Screen agents append an entry per screen; labels live in
 * src/i18n/<lng>/portal.json under `nav`, and `policy` hides an entry from roles the API would refuse anyway.
 */
export const nav: NavItem[] = [
  { to: "/", labelKey: "portal:nav.dashboard", icon: LayoutDashboard, exact: true },
  { to: "/documents", labelKey: "portal:nav.inbox", icon: Inbox, policy: Policies.Read },
  { to: "/review", labelKey: "portal:nav.review", icon: ClipboardList, policy: Policies.Review },
  { to: "/suppliers", labelKey: "portal:nav.suppliers", icon: Building2, policy: Policies.Read },
  { to: "/settings", labelKey: "portal:nav.settings", icon: Settings, policy: Policies.TenantAdmin },
];

/** Screens whose data belongs to a tenant: a platform account (Operator, SuperAdmin without X-Tenant-Id) gets 400 there. */
const tenantOnly = new Set(["/suppliers", "/settings"]);

/** The entries for this user: `NavItem` filters by policy and role only, so the scope is applied here. */
export function navFor(user: Pick<AuthUser, "scope"> | null | undefined): NavItem[] {
  return user?.scope === "platform" ? nav.filter((item) => !tenantOnly.has(item.to)) : nav;
}
