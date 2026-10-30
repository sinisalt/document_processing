import { Building2, Cpu, FileText, LayoutDashboard, ListChecks } from "lucide-react";
import { Policies, type NavItem } from "@docproc/app-shell";

/**
 * Sidebar entries of the admin console, in display order. Labels live in src/i18n/<lng>/admin.json under `nav`, and
 * `policy` hides an entry from roles the API would refuse anyway: Operators (policy `Platform`) see tenants (read-only),
 * documents and the review queue; the platform AI providers (`/llm/*`) are SuperAdmin-only (`PlatformAdmin`).
 */
export const nav: NavItem[] = [
  { to: "/", labelKey: "admin:nav.dashboard", icon: LayoutDashboard, exact: true },
  { to: "/tenants", labelKey: "admin:nav.tenants", icon: Building2, policy: Policies.Platform },
  { to: "/documents", labelKey: "admin:nav.documents", icon: FileText, policy: Policies.Read },
  { to: "/review", labelKey: "admin:nav.review", icon: ListChecks, policy: Policies.Review },
  { to: "/llm", labelKey: "admin:nav.llm", icon: Cpu, policy: Policies.PlatformAdmin },
];
