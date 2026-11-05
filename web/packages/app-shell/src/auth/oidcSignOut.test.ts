import { describe, expect, it, vi } from "vitest";
import type { User } from "oidc-client-ts";
import { signOutOidc, type OidcSignOutContext } from "./oidcSignOut";

function context(user: Partial<User> | null, signoutRedirect: OidcSignOutContext["signoutRedirect"] = vi.fn(async () => {})) {
  const calls: string[] = [];
  const ctx = {
    user: user as User | null,
    signoutRedirect: vi.fn(async (args?: Parameters<OidcSignOutContext["signoutRedirect"]>[0]) => {
      calls.push("signoutRedirect");
      return signoutRedirect(args);
    }),
    removeUser: vi.fn(async () => {
      calls.push("removeUser");
    }),
  } satisfies OidcSignOutContext;
  return { ctx, calls };
}

describe("signOutOidc", () => {
  it("redirects to the end-session endpoint with the ID token as id_token_hint, without removing the user first", async () => {
    const { ctx, calls } = context({ id_token: "header.payload.signature", access_token: "at" });

    await signOutOidc(ctx);

    expect(ctx.signoutRedirect).toHaveBeenCalledWith({ id_token_hint: "header.payload.signature" });
    // oidc-client-ts reads the hint and removes the stored user itself; removing it here first would lose the hint.
    expect(calls).toEqual(["signoutRedirect"]);
  });

  it("still redirects when there is no ID token, leaving the hint out", async () => {
    const { ctx } = context(null);

    await signOutOidc(ctx);

    expect(ctx.signoutRedirect).toHaveBeenCalledWith(undefined);
    expect(ctx.removeUser).not.toHaveBeenCalled();
  });

  it("clears the local session when the redirect cannot start (no end_session_endpoint)", async () => {
    const { ctx, calls } = context({ id_token: "t" }, async () => {
      throw new Error("No end session endpoint");
    });

    await expect(signOutOidc(ctx)).resolves.toBeUndefined();

    expect(calls).toEqual(["signoutRedirect", "removeUser"]);
  });
});
