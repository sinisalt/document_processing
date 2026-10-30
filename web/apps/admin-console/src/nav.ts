import { LayoutDashboard } from "lucide-react";
import type { NavItem } from "@docproc/app-shell";

/**
 * Sidebar entries of the admin console, in display order. Screen agents append an entry per screen; labels live in
 * src/i18n/<lng>/admin.json under `nav`, and `policy` hides an entry from roles the API would refuse anyway
 * (e.g. `policy: "PlatformAdmin"` for tenant management, which Operators cannot do).
 */
export const nav: NavItem[] = [{ to: "/", labelKey: "admin:nav.dashboard", icon: LayoutDashboard, exact: true }];
