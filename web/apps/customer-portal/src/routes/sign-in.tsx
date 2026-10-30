import { createFileRoute } from "@tanstack/react-router";
import { SignInPage } from "@docproc/app-shell";
import { shellConfig } from "@/shell-config";

/** The only public route; `AppShell` sends anonymous visitors here with `?redirect=` and signed-in ones back. */
export const Route = createFileRoute("/sign-in")({ component: () => <SignInPage appName={shellConfig.appName} /> });
