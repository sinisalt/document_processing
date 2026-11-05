import { Building2, ChartNoAxesCombined, ClipboardList, Inbox, LayoutDashboard, Settings } from "lucide-react";
import type { components } from "@docproc/api-client";
import { Policies, type AuthUser, type NavItem } from "@docproc/app-shell";

type TenantFeatures = components["schemas"]["TenantFeaturesDto"];

/**
 * Sidebar entries of the customer portal, in display order. Screen agents append an entry per screen; labels live in
 * src/i18n/<lng>/portal.json under `nav`, and `policy` hides an entry from roles the API would refuse anyway.
 */
export const nav: NavItem[] = [
  { to: "/", labelKey: "portal:nav.dashboard", icon: LayoutDashboard, exact: true },
  { to: "/documents", labelKey: "portal:nav.inbox", icon: Inbox, policy: Policies.Read },
  { to: "/review", labelKey: "portal:nav.review", icon: ClipboardList, policy: Policies.Review },
  { to: "/suppliers", labelKey: "portal:nav.suppliers", icon: Building2, policy: Policies.Read },
  { to: "/insights", labelKey: "portal:nav.insights", icon: ChartNoAxesCombined, policy: Policies.Read },
  { to: "/settings", labelKey: "portal:nav.settings", icon: Settings, policy: Policies.TenantAdmin },
];

/** Screens whose data belongs to a tenant: a platform account (Operator, SuperAdmin without X-Tenant-Id) gets 400 there. */
const tenantOnly = new Set(["/suppliers", "/insights", "/settings"]);

/**
 * Whether the tenant is offered Insights > Onboarding (docs/11 §17): with one of the P4/P5 switches on (source routing,
 * supplier layouts, custom types, configurable steps). A tenant with none of them, or whose switches are not read yet,
 * keeps the sidebar it had before P5 (principle 6); the screen itself still answers at its address.
 */
export function insightsOffered(features: TenantFeatures | null | undefined): boolean {
  return !!features && (features.sourceRouting || features.supplierLayouts || features.customTypes || features.configurableSteps);
}

/** The entries for this user: `NavItem` filters by policy and role only, so the scope and the tenant's switches are applied here. */
export function navFor(user: Pick<AuthUser, "scope"> | null | undefined, features?: TenantFeatures | null): NavItem[] {
  if (user?.scope === "platform") return nav.filter((item) => !tenantOnly.has(item.to));
  return insightsOffered(features) ? nav : nav.filter((item) => item.to !== "/insights");
}
