import { createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { Card, CardDescription, CardHeader, CardTitle } from "@docproc/ui";
import { PageHeader, useAuth } from "@docproc/app-shell";

export const Route = createFileRoute("/")({ component: DashboardPage });

function DashboardPage() {
  const { t } = useTranslation("admin");
  const { user } = useAuth();
  return (
    <>
      <PageHeader title={t("dashboard.title")} description={t("dashboard.description")} />
      <Card>
        <CardHeader>
          <CardTitle>{t("dashboard.welcome", { name: user?.displayName })}</CardTitle>
          <CardDescription>{t("dashboard.placeholder")}</CardDescription>
        </CardHeader>
      </Card>
    </>
  );
}
