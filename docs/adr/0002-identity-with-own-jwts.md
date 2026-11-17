# Sign-in uses ASP.NET Core Identity with JWTs the API issues itself

The spec allowed either ASP.NET Core Identity issuing JWTs or Microsoft Entra ID. We chose Identity, with users stored in the FieldTrack Postgres database and the API signing its own short-lived JWTs (HS256) plus rotating refresh tokens, because it costs nothing, runs fully offline in docker compose and Testcontainers, and doesn't depend on a student Azure tenant allowing app registrations. Refresh tokens travel in the response body rather than a cookie because the dashboard (Static Web Apps) and API (Container Apps) sit on different sites, where browsers may block cross-site cookies.

## Considered Options

- **Microsoft Entra ID**: no password storage of our own, but student tenants often block app registrations and local tests would need a mock identity provider.
- **Identity's built-in bearer tokens (`MapIdentityApi`)**: less code, but the tokens are opaque rather than JWTs.
- **Refresh token in an httpOnly cookie**: safer against script injection, but needs `SameSite=None` across two sites and breaks where third-party cookies are blocked, unless both apps share a custom domain.

## Consequences

- Refresh tokens are single-use with no grace window. A refresh token presented a second time counts as copied, whether a thief, a retry after a lost response or a duplicated browser tab sent it, and every refresh token of that user is revoked, so they sign in again. The dashboard therefore runs one refresh at a time. Spending a token and issuing its successor share one transaction, so a replay racing with the refresh revokes the successor too.
- Replay detection needs the spent token's row, so the hourly maintenance job keeps a spent (revoked) refresh token for one extra refresh token lifetime (7 days) after it expires, and purges an unspent one as soon as it expires. A copied token replayed later than that, more than 14 days after it was issued, is no longer detected: it is refused like any unknown token, without revoking the family.
- Lockout is per account: 5 wrong passwords lock it for 15 minutes. Anyone who knows an account's email can keep it locked out. Sign-in answers an unknown email, a wrong password and a lockout alike, and spends a password hash on each, so emails are hard to discover. To unlock an account early, clear its `lockout_end` in the `users` table.
- Revoking refresh tokens (an Admin's revoke, a password change, a replayed token) leaves access tokens already issued valid until they expire, at most 15 minutes later. Checking the security stamp on every request would close that gap at the cost of a database read per request; until a story needs it, "cut off access" means within 15 minutes.
- Everyone sees everything except two reads that are Admin-only: the user list, because it exposes accounts, and Rejected messages, because they hold raw untrusted input from anyone who can publish to the broker. Both sit under the `Manage` policy.
- The Demo Operator is exempt from lockout and cannot change its password. Its password is shown on the login page and reset on every deploy, so a lockout or a password change would only let one visitor shut the others out. The per-address sign-in rate limit still applies.
