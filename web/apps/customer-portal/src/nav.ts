import { LayoutDashboard } from "lucide-react";
import type { NavItem } from "@docproc/app-shell";

/**
 * Sidebar entries of the customer portal, in display order. Screen agents append an entry per screen; labels live in
 * src/i18n/<lng>/portal.json under `nav`, and `policy` hides an entry from roles the API would refuse anyway.
 */
export const nav: NavItem[] = [{ to: "/", labelKey: "portal:nav.dashboard", icon: LayoutDashboard, exact: true }];
