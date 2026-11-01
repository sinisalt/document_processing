import { Component, type ErrorInfo, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import type { ErrorComponentProps } from "@tanstack/react-router";
import { AlertTriangle } from "lucide-react";
import { Button, Card, CardContent, CardDescription, CardHeader, CardTitle } from "@docproc/ui";
import { errorMessage } from "../api/ApiError";

export interface ErrorPageProps {
  error: unknown;
  /** Re-renders the failed subtree (route error boundary `reset`, or the shell boundary's own). */
  reset?: () => void;
  /** Defaults to `/`; null hides the home link. */
  homeHref?: string | null;
  /** Extra buttons next to the standard ones. */
  actions?: ReactNode;
  className?: string;
}

/** What the user sees for an unrecoverable render or route error: plain words, the way out, the details folded away. */
export function ErrorPage({ error, reset, homeHref = "/", actions, className }: ErrorPageProps) {
  const { t } = useTranslation("common");
  const message = errorMessage(error, t("errors.description"));
  const stack = error instanceof Error ? error.stack : undefined;
  return (
    <div role="alert" className={className ?? "flex min-h-[50vh] items-center justify-center p-6"}>
      <Card className="w-full max-w-lg">
        <CardHeader>
          <div className="flex items-center gap-2 text-destructive">
            <AlertTriangle className="size-5" aria-hidden />
            <CardTitle>{t("errors.title")}</CardTitle>
          </div>
          <CardDescription>{message}</CardDescription>
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          <div className="flex flex-wrap gap-2">
            {reset && (
              <Button type="button" onClick={reset}>
                {t("errors.tryAgain")}
              </Button>
            )}
            <Button type="button" variant="outline" onClick={() => window.location.reload()}>
              {t("errors.reload")}
            </Button>
            {homeHref && (
              <Button asChild variant="ghost">
                <a href={homeHref}>{t("errors.goHome")}</a>
              </Button>
            )}
            {actions}
          </div>
          {stack && (
            <details className="text-xs text-muted-foreground">
              <summary className="cursor-pointer">{t("errors.details")}</summary>
              <pre className="mt-2 max-h-48 overflow-auto rounded bg-muted p-2 whitespace-pre-wrap">{stack}</pre>
            </details>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

/** TanStack Router's `defaultErrorComponent` / a route's `errorComponent`. */
export function RouteErrorPage({ error, reset }: ErrorComponentProps) {
  return <ErrorPage error={error} reset={reset} />;
}

export interface ErrorBoundaryProps {
  children: ReactNode;
  fallback?: (error: unknown, reset: () => void) => ReactNode;
  onError?: (error: Error, info: ErrorInfo) => void;
}

interface ErrorBoundaryState {
  error: Error | null;
}

/** Catches render errors outside the router (providers, layout) so the app never ends as a blank page. */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { error: null };

  static getDerivedStateFromError(error: Error): ErrorBoundaryState {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    this.props.onError?.(error, info);
    if (!this.props.onError) console.error("Unhandled render error", error, info.componentStack);
  }

  reset = () => this.setState({ error: null });

  render() {
    if (this.state.error) {
      return this.props.fallback ? this.props.fallback(this.state.error, this.reset) : <ErrorPage error={this.state.error} reset={this.reset} />;
    }
    return this.props.children;
  }
}
