import type { ComponentProps, ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Link, useLocation } from "@tanstack/react-router";
import { FileText } from "lucide-react";
import {
  Separator,
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupContent,
  SidebarGroupLabel,
  SidebarHeader,
  SidebarInset,
  SidebarMenu,
  SidebarMenuBadge,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarProvider,
  SidebarRail,
  SidebarTrigger,
} from "@docproc/ui";
import { useAuth } from "../auth/useAuth";
import { TenantSwitcher } from "../api/TenantSwitcher";
import { UserMenu } from "./UserMenu";
import { isActivePath, toSections, type Nav, type NavItem } from "./nav";

export interface AppLayoutProps {
  nav: Nav;
  appName: string;
  /** Replaces the default icon next to the app name. */
  logo?: ReactNode;
  /** Header content after the sidebar trigger (breadcrumbs, a page title). */
  headerStart?: ReactNode;
  /** Header content before the user menu (search, notifications). */
  headerEnd?: ReactNode;
  /** The admin console sets this: a SuperAdmin picks the tenant to act inside. */
  showTenantSwitcher?: boolean;
  /** Sidebar footer (version, help link). */
  sidebarFooter?: ReactNode;
  children: ReactNode;
}

type LinkProps = ComponentProps<typeof Link>;

/**
 * The frame of both apps: shadcn sidebar with the app's nav (filtered by policy), a header with the user menu, and the
 * routed content. Nav links are plain strings; the apps register their typed route tree, so the `to` is widened here.
 */
export function AppLayout({ nav, appName, logo, headerStart, headerEnd, showTenantSwitcher = false, sidebarFooter, children }: AppLayoutProps) {
  const { t } = useTranslation("common");
  const { hasPolicy, hasRole } = useAuth();
  const pathname = useLocation({ select: (location) => location.pathname });

  const visible = (item: NavItem) => (!item.policy || hasPolicy(item.policy)) && (!item.roles || hasRole(...item.roles));
  const sections = toSections(nav)
    .map((section) => ({ ...section, items: section.items.filter(visible) }))
    .filter((section) => section.items.length > 0);

  return (
    <SidebarProvider>
      <a href="#main-content" className="sr-only focus:not-sr-only focus:absolute focus:z-50 focus:m-2 focus:rounded focus:bg-background focus:px-3 focus:py-2">
        {t("app.skipToContent")}
      </a>
      <Sidebar collapsible="icon">
        <SidebarHeader>
          <SidebarMenu>
            <SidebarMenuItem>
              <SidebarMenuButton size="lg" asChild>
                <Link {...({ to: "/" } as LinkProps)}>
                  <div className="flex aspect-square size-8 items-center justify-center rounded-lg bg-primary text-primary-foreground">{logo ?? <FileText className="size-4" aria-hidden />}</div>
                  <span className="truncate font-semibold">{appName}</span>
                </Link>
              </SidebarMenuButton>
            </SidebarMenuItem>
          </SidebarMenu>
        </SidebarHeader>
        <SidebarContent>
          <nav aria-label={t("nav.main")} className="contents">
            {sections.map((section, index) => (
              <SidebarGroup key={section.labelKey ?? index}>
                {section.labelKey && <SidebarGroupLabel>{t(section.labelKey)}</SidebarGroupLabel>}
                <SidebarGroupContent>
                  <SidebarMenu>
                    {section.items.map((item) => {
                      const label = t(item.labelKey);
                      const active = isActivePath(pathname, item);
                      const Icon = item.icon;
                      return (
                        <SidebarMenuItem key={item.to}>
                          <SidebarMenuButton asChild isActive={active} tooltip={label}>
                            <Link {...({ to: item.to } as LinkProps)} aria-current={active ? "page" : undefined}>
                              {Icon && <Icon aria-hidden />}
                              <span>{label}</span>
                            </Link>
                          </SidebarMenuButton>
                          {item.badge != null && <SidebarMenuBadge>{item.badge}</SidebarMenuBadge>}
                        </SidebarMenuItem>
                      );
                    })}
                  </SidebarMenu>
                </SidebarGroupContent>
              </SidebarGroup>
            ))}
          </nav>
        </SidebarContent>
        {sidebarFooter && <SidebarFooter>{sidebarFooter}</SidebarFooter>}
        <SidebarRail />
      </Sidebar>
      {/* min-w-0: a flex item otherwise grows to its content, so a wide table or the review grid pushed the page off-screen. */}
      <SidebarInset className="min-w-0">
        <header role="banner" className="flex h-14 shrink-0 items-center gap-2 border-b px-4">
          <SidebarTrigger className="-ml-1" aria-label={t("nav.toggleSidebar")} />
          <Separator orientation="vertical" className="mr-2 data-[orientation=vertical]:h-4" />
          <div className="flex min-w-0 flex-1 items-center gap-2">{headerStart}</div>
          <div className="flex items-center gap-2">
            {showTenantSwitcher && <TenantSwitcher className="max-w-72" />}
            {headerEnd}
            <UserMenu />
          </div>
        </header>
        <div id="main-content" className="flex flex-1 flex-col gap-6 p-4 md:p-6" tabIndex={-1}>
          {children}
        </div>
      </SidebarInset>
    </SidebarProvider>
  );
}
