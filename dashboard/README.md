# Anchor Dashboard

Teacher dashboard for the Anchor focus-session system. Flutter Web app, deployed to Azure Static Web Apps.

## Run locally

```bash
flutter pub get
flutter run -d chrome --web-port 5173 \
  --dart-define=API_BASE_URL=http://localhost:5276 \
  --dart-define=ENTRA_TENANT_ID=<tenant-guid> \
  --dart-define=ENTRA_CLIENT_ID=<spa-app-client-id> \
  --dart-define=API_SCOPE=<api-client-id>/.default
```

`--web-port 5173` is **required**: the backend's dev CORS policy (`Cors:AllowedOrigins`) only allows `http://localhost:5173`. Serving on any other port (which is what `flutter run` does by default) makes every API call fail in the browser with "Failed to fetch", even though sign-in still works (MSAL talks to Microsoft directly, not the backend). VS Code users can press **F5** instead — `../.vscode/launch.json` pins the port.

All `--dart-define` values are optional and default to the development values baked into the app (`lib/main.dart`, `lib/auth/msal_config.dart`) — the same tenant + app registration used as both SPA and API audience.

| Key | Default | Purpose |
| --- | --- | --- |
| `API_BASE_URL` | `http://localhost:5276` | ASP.NET Core backend base URL. |
| `ENTRA_TENANT_ID` | dev tenant | Entra tenant the dashboard signs into. |
| `ENTRA_CLIENT_ID` | dev SPA client | Entra app registration client id used by MSAL.js. |
| `API_SCOPE` | `<dev-client-id>/.default` | Scope requested when obtaining an access token for the backend. Use the bare GUID (no `api://` prefix) when the SPA and API share the same app registration — Entra rejects `api://`-form requests with `AADSTS90009`. The backend accepts both audience forms. |

The Entra app registration must include `http://localhost:5173` as an SPA redirect URI (matching `--web-port 5173` above). Entra treats `http://localhost` loopback as valid on any port for SPA/public clients, so sign-in works even before you pin the port — but the backend CORS policy does not, which is why the port still has to match.

## Routes

- `/login` — Microsoft sign-in via MSAL.js (popup flow).
- `/` — class picker + "Start session" button. Defaults to the class matching the teacher's `department` claim if present, otherwise the first class returned by the API. Sessions start with no bundles (baseline-only enforcement).
- `/session/:id` — live session view. Opens a SignalR connection to `/hubs/session`, lists incoming events (`SessionStarted`, `SessionEnded`, `UnblockRequested`). Bundles are added/removed here at any time via `PUT /sessions/{id}/bundles`, which pushes the recomputed allowlist to agents/extensions. "End session" button calls `POST /sessions/{id}/end`.

## Auth flow

1. `web/anchor_auth.js` is a thin wrapper around `@azure/msal-browser` (loaded via CDN in `web/index.html`).
2. `lib/auth/msal_auth_service.dart` is a Dart-side facade with conditional imports — the real JS-interop implementation only loads on Web; non-web builds (e.g. `flutter test`) get a stub.
3. After sign-in the access token is held in `AuthTokenStore` and attached as `Authorization: Bearer …` by `ApiClient`. The SignalR client passes it via the `access_token` query parameter (the backend `JwtBearerEvents` honors that for the hub path).

## Deployment

`.github/workflows/dashboard-deploy.yml` builds the web bundle on every PR and pushes to Azure Static Web Apps on merges to `main`. The workflow needs the repo secret `AZURE_STATIC_WEB_APPS_API_TOKEN` — grab it from the Azure portal under the `anchor-dashboard` SWA → *Manage deployment token*.
