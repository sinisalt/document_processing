import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { Building2, Globe, Loader2, LogIn } from "lucide-react";
import { Alert, AlertDescription, AlertTitle, Badge, Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Skeleton, cn } from "@docproc/ui";
import { useAuth } from "../auth/useAuth";
import { useApi } from "../api/ApiProvider";
import { queryKeys } from "../api/queryKeys";
import { listDevUsers, type DevUser } from "../auth/devAuthApi";
import { errorMessage, initials } from "../lib";

export interface SignInPageProps {
  appName?: string;
  className?: string;
}

/**
 * `dev`: the users of `GET /dev/users` as cards, one click signs in (`POST /dev/token`). `oidc`: a single button that
 * starts the redirect flow. The `AppShell` sends the user on once the token is there.
 */
export function SignInPage({ appName, className }: SignInPageProps) {
  const { t } = useTranslation("common");
  const auth = useAuth();
  const title = appName ?? t("app.name");

  return (
    <main className={cn("flex min-h-screen flex-col items-center justify-center gap-8 bg-muted/30 p-6", className)}>
      <div className="text-center">
        <h1 className="text-3xl font-semibold tracking-tight">{title}</h1>
        <p className="mt-2 text-sm text-muted-foreground">{auth.mode === "dev" ? t("auth.devSubtitle") : t("auth.oidcSubtitle")}</p>
      </div>
      {auth.error && (
        <Alert variant="destructive" className="max-w-xl">
          <AlertTitle>{t("errors.title")}</AlertTitle>
          <AlertDescription>{t("auth.sessionError", { message: auth.error })}</AlertDescription>
        </Alert>
      )}
      {auth.mode === "dev" ? <DevUserCards signIn={auth.signIn} /> : <OidcSignIn appName={title} signIn={auth.signIn} />}
    </main>
  );
}

function OidcSignIn({ appName, signIn }: { appName: string; signIn: () => Promise<void> }) {
  const { t } = useTranslation("common");
  const [busy, setBusy] = useState(false);
  return (
    <Card className="w-full max-w-sm">
      <CardHeader>
        <CardTitle>{t("auth.oidcTitle", { app: appName })}</CardTitle>
        <CardDescription>{t("auth.oidcSubtitle")}</CardDescription>
      </CardHeader>
      <CardContent>
        <Button
          className="w-full"
          disabled={busy}
          onClick={() => {
            setBusy(true);
            void signIn().finally(() => setBusy(false));
          }}
        >
          {busy ? <Loader2 className="animate-spin" aria-hidden /> : <LogIn aria-hidden />}
          {busy ? t("auth.signingIn") : t("auth.signIn")}
        </Button>
      </CardContent>
    </Card>
  );
}

function DevUserCards({ signIn }: { signIn: (username: string) => Promise<void> }) {
  const { t } = useTranslation("common");
  const { baseUrl } = useApi();
  const [pending, setPending] = useState<string | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const users = useQuery({
    queryKey: queryKeys.devUsers(),
    queryFn: () => listDevUsers(baseUrl),
    retry: false,
    staleTime: Infinity,
    meta: { silent: true },
  });

  const pick = async (user: DevUser) => {
    setPending(user.username);
    setFailure(null);
    try {
      await signIn(user.username);
    } catch (e) {
      setFailure(errorMessage(e));
    } finally {
      setPending(null);
    }
  };

  if (users.isError) {
    return (
      <Alert variant="destructive" className="max-w-xl">
        <AlertTitle>{t("auth.devTitle")}</AlertTitle>
        <AlertDescription>{t("auth.devUnavailable", { message: errorMessage(users.error) })}</AlertDescription>
      </Alert>
    );
  }

  return (
    <section aria-labelledby="dev-sign-in-title" className="w-full max-w-4xl">
      <h2 id="dev-sign-in-title" className="sr-only">
        {t("auth.devTitle")}
      </h2>
      {failure && (
        <Alert variant="destructive" className="mb-4">
          <AlertDescription>{failure}</AlertDescription>
        </Alert>
      )}
      <ul className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3" aria-busy={users.isPending}>
        {users.isPending &&
          Array.from({ length: 6 }, (_, i) => (
            <li key={i}>
              <Skeleton className="h-28 w-full rounded-xl" />
            </li>
          ))}
        {users.data?.map((user) => {
          const busy = pending === user.username;
          return (
            <li key={user.username} className="flex">
              <button
                type="button"
                className="group h-full w-full rounded-xl text-left outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:cursor-wait"
                onClick={() => void pick(user)}
                disabled={pending !== null}
                aria-label={t("auth.signInAs", { name: user.displayName })}
                aria-busy={busy}
              >
                <Card className="h-full transition-colors group-hover:border-primary/50 group-hover:bg-accent/40">
                  <CardHeader className="flex-row items-center gap-3">
                    <div className="flex size-10 shrink-0 items-center justify-center rounded-full bg-primary/10 text-sm font-semibold text-primary" aria-hidden>
                      {busy ? <Loader2 className="size-4 animate-spin" /> : initials(user.displayName)}
                    </div>
                    <div className="min-w-0">
                      <CardTitle className="truncate text-base">{user.displayName}</CardTitle>
                      <CardDescription className="truncate">{user.email}</CardDescription>
                    </div>
                  </CardHeader>
                  <CardContent className="flex flex-wrap items-center gap-1.5">
                    {user.roles.map((role) => (
                      <Badge key={role} variant="secondary">
                        {t(`roles.${role}`, { defaultValue: role })}
                      </Badge>
                    ))}
                    <Badge variant="outline" className="gap-1">
                      {user.tenantId ? <Building2 className="size-3" aria-hidden /> : <Globe className="size-3" aria-hidden />}
                      {user.tenantId ? (user.tenantName ?? t("auth.tenant", { id: user.tenantId.slice(0, 8) })) : t("auth.platformScope")}
                    </Badge>
                  </CardContent>
                </Card>
              </button>
            </li>
          );
        })}
      </ul>
    </section>
  );
}
