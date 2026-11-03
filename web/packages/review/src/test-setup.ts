import "@testing-library/jest-dom/vitest";
import { createI18n } from "@docproc/app-shell";
import { reviewResources } from "./i18n";

createI18n({ resources: reviewResources, defaultNS: "review", lng: "en" });

// jsdom has no matchMedia (useIsMobile), pointer capture or scrollIntoView, which Radix Select uses.
window.matchMedia ??= ((query: string) => ({
  matches: false,
  media: query,
  onchange: null,
  addEventListener: () => undefined,
  removeEventListener: () => undefined,
  addListener: () => undefined,
  removeListener: () => undefined,
  dispatchEvent: () => false,
})) as typeof window.matchMedia;
Element.prototype.hasPointerCapture ??= () => false;
Element.prototype.releasePointerCapture ??= () => {};
Element.prototype.scrollIntoView ??= () => {};
