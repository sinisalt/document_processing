import { useTranslation } from "react-i18next";
import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { ArrowLeft } from "lucide-react";
import { Button, Skeleton } from "@docproc/ui";
import { ApiError, EmptyState, Policies, useAuth } from "@docproc/app-shell";
import { ReviewScreen } from "@/components/review/ReviewScreen";
import { useDocument, useTenantSettings } from "@/lib/queries";

export const Route = createFileRoute("/documents/$documentId")({ component: ReviewPage });

function ReviewPage() {
  const { documentId } = Route.useParams();
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const { hasPolicy, user } = useAuth();
  const document = useDocument(documentId);
  const settings = useTenantSettings(user?.scope === "tenant");

  if (document.isPending) {
    return (
      <div className="flex flex-col gap-4" aria-busy>
        <Skeleton className="h-10 w-1/2" />
        <Skeleton className="h-96" />
      </div>
    );
  }
  if (document.isError || !document.data) {
    const status = document.error instanceof ApiError ? document.error.status : undefined;
    return (
      <EmptyState
        title={status === 404 ? t("review.notFound") : t("common:errors.title")}
        description={status === 404 ? t("review.notFoundDescription") : document.error instanceof Error ? document.error.message : undefined}
        action={
          <Button variant="outline" onClick={() => void navigate({ to: "/documents" })}>
            <ArrowLeft aria-hidden />
            {t("review.back")}
          </Button>
        }
      />
    );
  }

  const threshold = Number(settings.data?.settings.autoApproveConfidenceThreshold ?? 0.8);
  return <ReviewScreen key={`${document.data.id}:${document.data.updatedAt}`} document={document.data} canReview={hasPolicy(Policies.Review)} threshold={threshold} locale={i18n.language} />;
}
