import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "@docproc/ui/styles.css";

// Placeholder shell; replaced by the application (see docs/05-api-and-ui.md).
createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <main className="p-8 text-foreground">DocProc</main>
  </StrictMode>,
);
