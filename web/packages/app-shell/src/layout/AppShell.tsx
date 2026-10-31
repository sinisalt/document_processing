import { useEffect, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Outlet, useLocation, useRouter, useRouterState } from "@tanstack/react-router";
import { Button } from "@docproc/ui";
import { useAuth } from "../auth/useAuth";
import { AppLayout, type AppLayoutProps } from "./AppLayout";
import { LoadingScreen } from "./LoadingScreen";
import { ErrorPage } from "./ErrorBoundary";

export const REDIRECT_PARAM = "redirect";

function normalizePath(pathname: string): string {
  return pathname.replace(/\/+$/, "") || "/";
}

/** Only same-origin paths may be redirect targets; anything else (`//evil`, `https:`) falls back to `/`. */
export function safeRedirect(value: string | null | undefined, fallback = "/"): string {
  if (!value || !value.startsWith("/") || value.startsWith("//") || value.includes("\\")) return fallback;
  return value;
}

/** Navigates by URL string; the app registers its typed route tree, so the shell cannot use typed `to` values. */
export function Redirect({ href }: { href: string }) {
  const router = useRouter();
  useEffect(() => {
    void router.navigate({ href, replace: true });
  }, [href, router]);
  return null;
}

export interface AppShellProps extends Omit<AppLayoutProps, "children"> {
  /** The public route that renders `<SignInPage />`. */
  signInPath?: string;
  /** Defaults to the router's `<Outlet />`. */
  children?: ReactNode;
}

/**
 * The root route's component. Anonymous visitors see only the sign-in route (and are sent there with a `redirect`
 * back); signed-in users get the layout around the matched route; the sign-in route sends a signed-in user on.
 */
export function AppShell({ signInPath = "/sign-in", children, ...layout }: AppShellProps) {
  const { t } = useTranslation("common");
  const auth = useAuth();
  const location = useLocation({ select: (l) => ({ pathname: l.pathname, searchStr: l.searchStr }) });
  const pathname = normalizePath(location.pathname);
  const onSignIn = pathname === signInPath;
  // The route the router has actually rendered. It trails `location` while the next route is loading (its code-split
  // chunk, a loader): right after sign-out the location is already the sign-in page but the matches are still the
  // screen the user was on, so an `<Outlet />` here would mount that screen again, anonymously, and fire its queries
  // without a token (a burst of 401s on every sign-out).
  const signInRendered = useRouterState({ select: (state) => state.matches.some((match) => normalizePath(match.pathname) === signInPath) });

  if (auth.status === "loading") {
    return <LoadingScreen label={t("auth.checking")} />;
  }

  if (auth.status === "error") {
    return (
      <ErrorPage
        error={new Error(auth.error ?? t("auth.userError"))}
        reset={() => void auth.refetchUser()}
        homeHref={null}
        className="flex min-h-screen items-center justify-center p-6"
        actions={
          <Button variant="outline" onClick={() => void auth.signOut()}>
            {t("auth.signOut")}
          </Button>
        }
      />
    );
  }

  if (!auth.isAuthenticated) {
    if (onSignIn) return signInRendered ? <Outlet /> : <LoadingScreen label={t("auth.checking")} />;
    const back = location.pathname + location.searchStr;
    const query = back && back !== "/" ? `?${new URLSearchParams({ [REDIRECT_PARAM]: back }).toString()}` : "";
    return <Redirect href={`${signInPath}${query}`} />;
  }

  if (onSignIn) {
    const wanted = new URLSearchParams(location.searchStr).get(REDIRECT_PARAM);
    return <Redirect href={safeRedirect(wanted)} />;
  }

  return <AppLayout {...layout}>{children ?? <Outlet />}</AppLayout>;
}
