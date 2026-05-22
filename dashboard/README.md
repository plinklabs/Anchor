# Anchor Dashboard

Teacher dashboard for the Anchor focus-session system. Flutter Web app, deployed to Azure Static Web Apps.

## Run locally

```bash
flutter pub get
flutter run -d chrome --dart-define=API_BASE_URL=http://localhost:5000
```

`API_BASE_URL` defaults to `http://localhost:5000` if unset.

## Routes

- `/login` — sign-in stub (auth wiring lands in a follow-up issue).
- `/` — home / class list.
- `/session/:id` — live session view.

## Deployment

`.github/workflows/dashboard-deploy.yml` builds the web bundle on every PR and pushes to Azure Static Web Apps on merges to `main`. The workflow needs the repo secret `AZURE_STATIC_WEB_APPS_API_TOKEN` — grab it from the Azure portal under the `anchor-dashboard-*` SWA → *Manage deployment token*.
