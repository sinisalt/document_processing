import { defineConfig } from "vitest/config";
import { docprocAlias } from "@docproc/ui/vite";

export default defineConfig({
  plugins: [docprocAlias()],
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/test-setup.ts"],
  },
});
