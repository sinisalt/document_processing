/** `navigator.userAgentData` is Chromium-only and not in the DOM typings yet. */
type NavigatorWithUaData = Navigator & { userAgentData?: { platform?: string } };

/**
 * Whether the browser runs on an Apple platform, where the review shortcuts use Cmd instead of Ctrl. Prefers User-Agent
 * Client Hints and falls back to the deprecated `navigator.platform`.
 */
export function isApplePlatform(nav: Pick<NavigatorWithUaData, "platform" | "userAgentData"> | undefined = typeof navigator === "undefined" ? undefined : navigator): boolean {
  if (!nav) return false;
  const platform = nav.userAgentData?.platform || nav.platform || "";
  return /mac|iphone|ipad|ipod/i.test(platform);
}
