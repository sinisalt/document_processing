import type { AuthContextProps } from "react-oidc-context";

/** The part of react-oidc-context's context that signing out needs. */
export type OidcSignOutContext = Pick<AuthContextProps, "user" | "signoutRedirect" | "removeUser">;

/**
 * Ends the session at the identity provider, not only in this tab. The redirect to the end-session endpoint carries the
 * ID token as `id_token_hint`, which is how Keycloak knows whose session to end without asking "Do you want to log
 * out?"; oidc-client-ts removes the stored user itself before it navigates. Removing the user first would drop the ID
 * token, Keycloak would ask for confirmation, and a user who did not confirm stayed signed in at Keycloak, so the next
 * "Sign in" came straight back without a password. Only when the redirect cannot start (no `end_session_endpoint`,
 * metadata unreachable) is the local session cleared on its own.
 */
export async function signOutOidc(oidc: OidcSignOutContext): Promise<void> {
  const idToken = oidc.user?.id_token;
  try {
    await oidc.signoutRedirect(idToken ? { id_token_hint: idToken } : undefined);
  } catch {
    await oidc.removeUser();
  }
}
