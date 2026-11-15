# Sign-in uses ASP.NET Core Identity with JWTs the API issues itself

The spec allowed either ASP.NET Core Identity issuing JWTs or Microsoft Entra ID. We chose Identity, with users stored in the FieldTrack Postgres database and the API signing its own short-lived JWTs (HS256) plus rotating refresh tokens, because it costs nothing, runs fully offline in docker compose and Testcontainers, and doesn't depend on a student Azure tenant allowing app registrations. Refresh tokens travel in the response body rather than a cookie because the dashboard (Static Web Apps) and API (Container Apps) sit on different sites, where browsers may block cross-site cookies.

## Considered Options

- **Microsoft Entra ID**: no password storage of our own, but student tenants often block app registrations and local tests would need a mock identity provider.
- **Identity's built-in bearer tokens (`MapIdentityApi`)**: less code, but the tokens are opaque rather than JWTs.
- **Refresh token in an httpOnly cookie**: safer against script injection, but needs `SameSite=None` across two sites and breaks where third-party cookies are blocked, unless both apps share a custom domain.
