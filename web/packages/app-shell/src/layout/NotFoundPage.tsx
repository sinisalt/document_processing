import { useTranslation } from "react-i18next";
import { useLocation } from "@tanstack/react-router";
import { FileQuestion } from "lucide-react";
import { Button } from "@docproc/ui";
import { EmptyState } from "../components/EmptyState";

/** TanStack Router's `defaultNotFoundComponent`; also usable for an unknown id inside a screen. */
export function NotFoundPage() {
  const homeHref = "/";
  const { t } = useTranslation("common");
  const pathname = useLocation({ select: (location) => location.pathname });
  return (
    <EmptyState
      icon={FileQuestion}
      title={t("notFound.title")}
      description={t("notFound.description", { path: pathname })}
      action={
        <Button asChild variant="outline">
          <a href={homeHref}>{t("notFound.goHome")}</a>
        </Button>
      }
      className="min-h-[50vh]"
    />
  );
}
