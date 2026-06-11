# Anchor

A school-internal focus-session system for classroom device management on Windows BYOD laptops. A teacher starts a session for a class; for its duration each student's laptop softly enforces an allowlist of apps and websites, and off-list activity surfaces live on the teacher's dashboard.

Design rationale, technology decisions, data model, and phasing live in [focus-system-design.md](focus-system-design.md). This README is the practical "how do I run the pieces locally" doc.

## Architecture

```
┌─────────────────────────────────────────┐
│ Student Windows laptop                  │
│                                         │
│  ┌──────────────┐    ┌───────────────┐  │
│  │ FocusAgent   │    │ Edge          │  │
│  │ (WinUI 3)    │    │ + extension   │  │
│  │              │    │               │  │
│  │ • tray       │    │ • URL filter  │  │
│  │ • overlay    │    │ • reports     │  │
│  │ • Win32 hooks│    │   active tab  │  │
│  └──────┬───────┘    └───────┬───────┘  │
│         │ SignalR            │ SignalR  │
└─────────┼────────────────────┼──────────┘
          │                    │
          ▼                    ▼
┌─────────────────────────────────────────┐
│ Azure                                   │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │ App Service (ASP.NET Core)      │    │
│  │ • REST API                      │    │
│  │ • SignalR hub                   │    │
│  │ • Entra auth                    │    │
│  └─────────────┬───────────────────┘    │
│                │                        │
│  ┌─────────────▼───────────────────┐    │
│  │ Azure SQL (Serverless)          │    │
│  │ • users, classes, sessions      │    │
│  │ • allowlists, events            │    │
│  └─────────────────────────────────┘    │
└─────────────────────────────────────────┘
          ▲
          │
┌─────────┴───────────────────────────────┐
│ Teacher dashboard                       │
│ (Flutter Web on Static Web Apps)        │
└─────────────────────────────────────────┘
```

## Components

| Component | Path | Status | Stack |
| --- | --- | --- | --- |
| Backend API | [backend/](backend/) | Scaffolded | ASP.NET Core 8, EF Core, SignalR, Entra (Microsoft.Identity.Web) |
| Teacher dashboard | [dashboard/](dashboard/) | Scaffolded | Flutter Web, MSAL.js |
| Student agent | [agent/](agent/) | Scaffolded | WinUI 3 + C#, MSIX (later), WAM silent auth (later) |
| Edge extension | [extension/](extension/) | URL filter + block page | TypeScript, Edge (Chromium) MV3 |
| Azure infra | [infra/](infra/) | Scaffolded | Bicep — App Service, Azure SQL, SignalR, Static Web Apps |

## Prerequisites

Install only what you need for the components you intend to run.

- [ ] [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) — backend API, student agent
- [ ] [Node.js LTS](https://nodejs.org/) — edge extension
- [ ] [Flutter](https://docs.flutter.dev/get-started/install) (stable channel) — teacher dashboard
- [ ] [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) — provisioning Azure resources via [infra/main.bicep](infra/main.bicep)
- [ ] An [Entra ID tenant](https://learn.microsoft.com/entra/fundamentals/create-new-tenant) you control for testing — required for backend, dashboard, and (later) agent/extension auth

## Build and run

### Backend API

```powershell
cd backend
dotnet restore
dotnet run --project src/Anchor.Api
```

Defaults to `http://localhost:5276` (see [backend/src/Anchor.Api/Properties/launchSettings.json](backend/src/Anchor.Api/Properties/launchSettings.json)). Configuration (Entra tenant/client IDs, SQL connection string) lives in [backend/src/Anchor.Api/appsettings.Development.json](backend/src/Anchor.Api/appsettings.Development.json). The dev profile uses a local SQLite file (`anchor.dev.db`) so no Azure SQL is required for local work.

Run tests:

```powershell
cd backend
dotnet test
```

### Teacher dashboard

See [dashboard/README.md](dashboard/README.md) for full details (auth flow, `--dart-define` overrides, routes). Quick start:

```powershell
cd dashboard
flutter pub get
flutter run -d chrome --web-port 5173
```

`--web-port 5173` is required: the backend's dev CORS policy (`Cors:AllowedOrigins` in [appsettings.Development.json](backend/src/Anchor.Api/appsettings.Development.json)) only allows `http://localhost:5173`, so serving the dashboard on any other port makes every API call fail with "Failed to fetch". VS Code users can instead just press **F5** — the committed *dashboard (Chrome/Edge, port 5173)* configs in [.vscode/launch.json](.vscode/launch.json) pin the port for you.

### Student agent

See [agent/README.md](agent/README.md) for full details (layout, prerequisites, configuration, log paths). Quick start:

```powershell
cd agent
dotnet restore
dotnet run --project src/FocusAgent.App -c Debug -p:Platform=x64
```

This is the Phase 2 scaffold — tray icon, hidden main window, single instance, settings, and logging. Auth, SignalR listener, foreground hook, and MSIX packaging land in follow-up issues.

### Edge extension

See [extension/README.md](extension/README.md) for full details (layout, dev-load loop, sideload shape). Quick start:

```powershell
cd extension
npm install
npm run build
```

Output lands in `extension/dist/`. Load it via `edge://extensions` → **Developer mode** → **Load unpacked**. Phase 3 v1 lands the URL filter, friendly block page, and SignalR client; configuration + smoke-test steps are in [extension/README.md](extension/README.md). Production Entra auth (`chrome.identity`) is a follow-up issue.

### Azure infrastructure

See [infra/README.md](infra/README.md) — Bicep template (Option A) is the recommended path; portal walkthrough (Option B) is provided as a fallback.

## Contributing

This is a single-developer project, but the GitHub workflow is set up to support that cleanly.

- **Issue templates** live in [.github/ISSUE_TEMPLATE/](.github/ISSUE_TEMPLATE/): `bug`, `feature`, `enhancement`, `chore`, `docs`. Pick the type that matches the work before filing.
- **Labels** mirror the templates: `bug`, `feature`, `enhancement`, `chore`, `docs`, plus `blocked` for work waiting on something external.
- **PR template** is at [.github/pull_request_template.md](.github/pull_request_template.md). Every PR links the issue it closes.
- Branch naming: `<type>/issue-<N>-<slug>` (e.g. `feature/issue-42-add-login-screen`).
