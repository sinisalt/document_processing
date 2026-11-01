import { useTranslation } from "react-i18next";
import { createFileRoute, Link } from "@tanstack/react-router";
import { useQueries } from "@tanstack/react-query";
import { ArrowRight, Building2, ClipboardList, Inbox, Settings, Upload } from "lucide-react";
import { Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Skeleton } from "@docproc/ui";
import { PageHeader, Policies, formatNumber, queryKeys, unwrap, useApi, useAuth } from "@docproc/app-shell";
import { countedTabs, statusFilterOf, type InboxTab } from "@/lib/inbox";

export const Route = createFileRoute("/")({ component: DashboardPage });

const COUNT_LIMIT = 200;

/** Counts by status from the first page of `GET /documents` per status group (the API has no count endpoint); quick links. */
function DashboardPage() {
  const { t, i18n } = useTranslation();
  const { user, hasPolicy } = useAuth();
  const { client } = useApi();

  const counts = useQueries({
    queries: countedTabs.map((tab) => {
      const query = { status: statusFilterOf(tab), limit: COUNT_LIMIT };
      return {
        queryKey: queryKeys.documents.list(query),
        queryFn: async () => unwrap(await client.GET("/api/v1/documents", { params: { query } })),
        staleTime: 30_000,
      };
    }),
  });

  return (
    <>
      <PageHeader title={t("dashboard.title")} description={t("dashboard.welcome", { name: user?.displayName })} />
      <section aria-labelledby="dashboard-counts">
        <h2 id="dashboard-counts" className="sr-only">
          {t("dashboard.countsHeading")}
        </h2>
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-5">
          {countedTabs.map((tab, index) => {
            const result = counts[index];
            const page = result?.data;
            const count = page ? page.items.length : null;
            const more = !!page?.nextCursor;
            return (
              <Card key={tab} data-testid={`count-${tab}`}>
                <CardHeader className="pb-2">
                  <CardDescription>{t(`inbox.tabs.${tab}`)}</CardDescription>
                  <CardTitle className="text-3xl tabular-nums">
                    {result?.isPending ? <Skeleton className="h-9 w-16" /> : result?.isError ? "—" : `${formatNumber(count, i18n.language)}${more ? "+" : ""}`}
                  </CardTitle>
                </CardHeader>
                <CardContent>
                  {/* Nothing to open behind a zero: the card says so instead of linking to an empty list. */}
                  {count === 0 && !more ? (
                    <span className="text-sm text-muted-foreground" data-testid={`count-${tab}-empty`}>
                      {t("dashboard.nothing")}
                    </span>
                  ) : (
                    <Button variant="link" size="sm" className="h-auto p-0 has-[>svg]:px-0" asChild>
                      <Link to="/documents" search={{ tab: tab as InboxTab }}>
                        {t("dashboard.open")}
                        <ArrowRight aria-hidden />
                      </Link>
                    </Button>
                  )}
                </CardContent>
              </Card>
            );
          })}
        </div>
      </section>
      <section aria-labelledby="dashboard-links">
        <h2 id="dashboard-links" className="mb-3 text-lg font-semibold">
          {t("dashboard.quickLinks")}
        </h2>
        {/* As many columns as fit: five links share one row on a desktop instead of leaving one alone on a second. */}
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-[repeat(auto-fit,minmax(13rem,1fr))]">
          <QuickLink to="/documents" icon={Inbox} title={t("nav.inbox")} description={t("dashboard.links.inbox")} />
          {/* Uploads, suppliers and settings belong to a tenant: a platform account gets 400 there (navFor hides them too). */}
          {user?.scope === "tenant" && hasPolicy(Policies.Ingest) && <QuickLink to="/documents" icon={Upload} title={t("upload.button")} description={t("dashboard.links.upload")} />}
          {hasPolicy(Policies.Review) && <QuickLink to="/review" icon={ClipboardList} title={t("nav.review")} description={t("dashboard.links.review")} />}
          {user?.scope === "tenant" && <QuickLink to="/suppliers" icon={Building2} title={t("nav.suppliers")} description={t("dashboard.links.suppliers")} />}
          {user?.scope === "tenant" && hasPolicy(Policies.TenantAdmin) && <QuickLink to="/settings" icon={Settings} title={t("nav.settings")} description={t("dashboard.links.settings")} />}
        </div>
      </section>
    </>
  );
}

function QuickLink({ to, icon: Icon, title, description }: { to: "/documents" | "/review" | "/suppliers" | "/settings"; icon: typeof Inbox; title: string; description: string }) {
  return (
    <Card className="transition-colors hover:bg-accent/40">
      <Link to={to} className="block h-full outline-none focus-visible:ring-2 focus-visible:ring-ring">
        <CardHeader>
          <div className="flex items-center gap-2">
            <Icon className="size-5 text-muted-foreground" aria-hidden />
            <CardTitle>{title}</CardTitle>
          </div>
          <CardDescription>{description}</CardDescription>
        </CardHeader>
      </Link>
    </Card>
  );
}
