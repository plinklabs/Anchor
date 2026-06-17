# Fork setup — standing up your own Anchor cloud by hand

This is the **manual fallback** for [`scripts/setup.ps1`](../scripts/setup.ps1).
That script provisions a fork's whole Azure + GitHub environment in one command,
but it won't always succeed end to end (Azure region/quota quirks, admin-consent
restrictions, TPM/login issues, an org that manages app registrations out of
band). When it can't, this guide walks every step it would have done so you can do
them in the portal / CLI yourself.

It mirrors the script step for step and uses the **same names** the script and
[`infra/main.bicep`](../infra/main.bicep) use, so a deploy works whether you ran
the script or followed this by hand. Steps that the script can only *attempt*
(tenant admin-consent, which needs directory-admin rights) are flagged
**[MAY NEED A HUMAN]** — the script tries them and prints the command to run by
hand if it couldn't.

Related references:

- [`infra/README.md`](../infra/README.md) — the direct-Bicep deploy and the
  pure-portal resource walk-through (both manual alternatives to the script),
  folded into Step 3 below.
- [`docs/RELEASE.md`](RELEASE.md) — the deploy pipeline and the authoritative
  inventory of every secret/variable the workflows consume.
- [ROADMAP.md](../ROADMAP.md) — where fork bootstrap (workstream D) fits.

## What you're building

One Azure resource group containing:

| Resource | Type | Tier | Default name |
| --- | --- | --- | --- |
| Resource group | — | — | `anchor-rg` |
| SQL logical server | Azure SQL | — | `anchor-sql-<suffix>` |
| SQL database | Azure SQL DB | GP Serverless, 0.5–2 vCores | `anchordb` |
| App Service (backend API) | App Service | F1 Free, Linux | `anchor-api-<suffix>` |
| App Service Plan | App Service Plan | F1 Free, Linux | `ASP-anchorrg-b49b` |
| SignalR Service | SignalR | Free | `anchor-signalr` |
| Static Web App (dashboard) | Static Web App | Free | `anchor-dashboard` |

Plus **three Entra ID (Azure AD) app registrations** — these are *not* deployed by
Bicep; they are created in Entra and their IDs are passed *into* the deploy:

- **API** app registration — exposes an `access_as_user` scope and an
  `api://<client-id>` identifier URI, and holds a client secret for the
  on-behalf-of Graph directory search.
- **Dashboard SPA** app registration — a redirect URI pointing at the Static Web
  App, the Graph `User.Read` permission, and pre-authorization to call the API
  scope.
- **Agent** app registration — a Windows desktop **public client** for the agent's
  WAM/broker sign-in. It needs the broker redirect URI
  `ms-appx-web://Microsoft.AAD.BrokerPlugin/<agent-client-id>`, **Allow public
  client flows** enabled, and the API `access_as_user` permission. This must be a
  *separate* registration from the Dashboard SPA: an SPA registration carries
  neither the broker redirect URI nor public-client flows, so reusing its id makes
  the agent fail at sign-in with `WAM_provider_error_…` (`0xCAA2000x`,
  "IncorrectConfiguration") in release builds (#271).

`<suffix>` is your fork-specific [`uniqueSuffix`](../infra/main.bicep) — pick
something unique (e.g. your school) so globally-unique resource names don't
collide with the original `arcadia` deployment. This guide uses `yourschool`.

## What the script automates

The script (`scripts/setup.ps1`) automates **all** the steps below — including
the two that used to be manual:

- **API client secret → App Service** — the script mints the `anchor-obo` secret
  and writes it to the App Service as `AzureAd__ClientCredentials` after the
  deploy (re-applying it each run, since the Bicep deploy rewrites the App
  Service's app settings). See [Step 6c](#6c-add-the-api-client-secret-to-the-app-service).
- **Entra service principals** — created for all three app registrations so admin
  consent has a service to consent against. See [Step 3d](#3d-create-service-principals).

**One step may still need a human:**

- **[MAY NEED A HUMAN] Entra admin consent** — granting tenant-wide consent for
  the API permissions (and for the SPA to call the API) needs a Global
  Administrator / Privileged Role Administrator. The script attempts it and
  prints the command to run by hand if it lacks the rights (or the new
  registration hasn't replicated yet). See [Step 7](#step-7--grant-admin-consent-may-need-a-human).

## Prerequisites

- [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli) (`az`)
  — and `az login`.
- [GitHub CLI](https://cli.github.com/) (`gh`) — and `gh auth status` logged in —
  *or* you can set the GitHub secrets/variables in the repo web UI (Step 8).
- An Azure subscription, and rights to create Entra app registrations in your
  tenant.
- Your fork cloned, and the repo identified as `OWNER/REPO` (e.g. `yourschool/Anchor`).

> **Tip — dry run the script first.** Even if you intend to go fully manual, a
> dry run prints the exact plan grounded in your real account without changing
> anything:
>
> ```powershell
> ./scripts/setup.ps1 -UniqueSuffix yourschool -WhatIf
> ```

---

## Step 1 — Preflight

Confirm tooling and capture the values later steps reuse.

```bash
az account show -o json     # confirms you're logged in; note "id" (subscription) and "tenantId"
gh auth status              # confirms gh is logged in (skip if setting secrets in the web UI)
```

Note your **tenant ID** (`tenantId` from `az account show`) — you'll pass it to
the deploy and store it as a GitHub variable. Have a **strong SQL admin password**
ready; the script prompts for it, here you'll pass it to the deploy in Step 4.

---

## Step 2 — Resource group

```bash
az group create --name anchor-rg --location westeurope
```

`anchor-rg` / `westeurope` are the script + Bicep defaults. If you use a different
group name, pass it to every `az` command below with `--resource-group`.

---

## Step 3 — Entra app registrations

Create all three registrations **before** the deploy — the deploy needs the API
client ID. (The SPA redirect URI is finished in [Step 5](#finish-the-spa-redirect-uri--api-pre-authorization)
after the deploy, once you know the Static Web App URL; the agent registration is
self-contained and needs no post-deploy step.)

You can do this with `az` (mirrors the script) or in the **Entra portal → App
registrations**.

### 3a. API app registration

```bash
# Create (single-tenant), capture its appId (the API client ID).
az ad app create \
  --display-name "Anchor API (yourschool)" \
  --sign-in-audience AzureADMyOrg \
  --query appId -o tsv
```

Save that GUID as `API_CLIENT_ID`. Then:

```bash
# Identifier URI: api://<API_CLIENT_ID>
az ad app update --id "$API_CLIENT_ID" --identifier-uris "api://$API_CLIENT_ID"
```

**Expose the `access_as_user` scope.** In the portal: **App registrations → Anchor
API → Expose an API → Add a scope**:

- Scope name: `access_as_user`
- Who can consent: **Admins and users**
- Admin consent display name: `Access Anchor API`
- Admin consent description: `Allow the app to access the Anchor API on behalf of the signed-in user.`
- State: **Enabled**

(Note the **scope's id** GUID — you'll reference this scope when pre-authorizing
the SPA in Step 5.)

**Create a client secret** for the on-behalf-of Graph directory search:

```bash
az ad app credential reset --id "$API_CLIENT_ID" --append \
  --display-name anchor-obo --query password -o tsv
```

Copy the printed secret value now — it is shown **once**. You'll add it to the App
Service in [Step 6c](#6c-add-the-api-client-secret-to-the-app-service).

### 3b. Dashboard SPA app registration

```bash
az ad app create \
  --display-name "Anchor Dashboard (yourschool)" \
  --sign-in-audience AzureADMyOrg \
  --query appId -o tsv
```

Save that GUID as `SPA_CLIENT_ID`. Grant the baseline Graph **User.Read**
(delegated) so sign-in works:

```bash
# 00000003-...c000 = Microsoft Graph; e1fe6dd8-... = User.Read (delegated)
az ad app permission add --id "$SPA_CLIENT_ID" \
  --api 00000003-0000-0000-c000-000000000000 \
  --api-permissions e1fe6dd8-ba31-4d61-89e7-88639da4683d=Scope
```

The SPA's **redirect URI** and its **pre-authorization to call the API scope** are
set in [Step 5](#finish-the-spa-redirect-uri--api-pre-authorization), after
the deploy produces the Static Web App URL.

> **Single-app simplification.** If you'd rather use one app registration for both
> the API and the SPA, you can skip 3b and reuse the API app as the SPA client.
> Entra then requires the dashboard's `API_SCOPE` to use the GUID client ID with
> no `api://` prefix (see the note in [`dashboard/lib/auth/msal_config.dart`](../dashboard/lib/auth/msal_config.dart)).
> The two-app layout below matches what the script provisions. **This
> simplification does not extend to the agent** — the agent always needs its own
> public-client registration (3c), because WAM/broker sign-in is incompatible with
> an SPA/web registration.

### 3c. Agent (Windows desktop) app registration

The agent signs in through **WAM** (the Windows Web Account Manager broker) — a
**public-client** flow. This needs its own registration, distinct from the
Dashboard SPA: WAM requires the broker redirect URI and "allow public client
flows", neither of which an SPA registration carries. Reusing the SPA id makes the
agent fail at sign-in with `WAM_provider_error_…` (`0xCAA2000x`,
"IncorrectConfiguration") in release builds (#271).

```bash
# Create (single-tenant), capture its appId (the AGENT client ID).
AGENT_CLIENT_ID=$(az ad app create \
  --display-name "Anchor Agent (yourschool)" \
  --sign-in-audience AzureADMyOrg \
  --query appId -o tsv)

# Mark it a public client and register the WAM broker redirect URI.
az ad app update --id "$AGENT_CLIENT_ID" \
  --set isFallbackPublicClient=true \
  --public-client-redirect-uris "ms-appx-web://Microsoft.AAD.BrokerPlugin/$AGENT_CLIENT_ID"

# Request the API's access_as_user scope so the agent can get a backend token.
# <scope-id> is the access_as_user scope GUID from Step 3a.
az ad app permission add --id "$AGENT_CLIENT_ID" \
  --api "$API_CLIENT_ID" --api-permissions "<scope-id>=Scope"
```

In the portal the equivalents are **Authentication → Add a platform → Mobile and
desktop applications → Custom redirect URI**
`ms-appx-web://Microsoft.AAD.BrokerPlugin/<AGENT_CLIENT_ID>`, **Advanced settings →
Allow public client flows → Yes**, and **API permissions → Add → Anchor API →
`access_as_user`**.

Save the GUID as `AGENT_CLIENT_ID` — it becomes the GitHub Actions variable of the
same name that the [agent release workflow](../.github/workflows/agent-release.yml)
bakes into the shipped agent (separate from the SPA's `ENTRA_CLIENT_ID`).

### 3d. Create service principals

`az ad app create` makes only the application *object*. Admin consent (Step 7)
can only be granted against an app that also has a **service principal** in the
tenant — without one, consenting the SPA against the API fails with *"your
organization has not subscribed to service(s) (&lt;api-client-id&gt;)"*. Create
one for each registration (idempotent — skip any that already exists):

```bash
az ad sp create --id "$API_CLIENT_ID"
az ad sp create --id "$SPA_CLIENT_ID"
az ad sp create --id "$AGENT_CLIENT_ID"
```

---

## Step 4 — Deploy the infrastructure (Bicep)

This is the direct-Bicep deploy from `infra/README.md`. It creates the SQL server
+ database, App Service + plan, SignalR, and the Static Web App, and wires the
Entra/CORS values as App Service application settings.

```bash
az deployment group create \
  --resource-group anchor-rg \
  --template-file infra/main.bicep \
  --parameters uniqueSuffix=yourschool \
               entraTenantId='<your-tenant-guid>' \
               entraClientId="$API_CLIENT_ID" \
               sqlAdminPassword='<your-strong-password>'
```

`entraClientId` is the **API** client ID from Step 3a (the audience the API
validates). The deploy takes ~3 minutes.

### Bicep parameters

All names are parameters so a fork stands up its own environment with no source
edits; the defaults reproduce the live `arcadia` deployment. (Full table in
[`infra/README.md`](../infra/README.md#parameters).)

| Parameter | Default | Notes |
| --- | --- | --- |
| `uniqueSuffix` | `arcadia` | Drives the resource-name defaults; set it to your fork suffix. |
| `entraTenantId` | empty | Your tenant GUID — **required** for a working deploy. |
| `entraClientId` | empty | The **API** app registration client ID (Step 3a). |
| `entraAudience` | `api://<entraClientId>` | JWT audience the API validates; auto-derived. |
| `entraInstance` | current cloud login endpoint | Entra authority. |
| `sqlAdminLogin` / `sqlAdminPassword` | `anchoradmin` / *(required, secure)* | SQL admin credentials. |
| `dashboardCorsOriginOverride` | empty → deployed SWA URL | Allowed CORS origin (`Cors__AllowedOrigins__0`). |
| `sqlServerName` / `sqlDatabaseName` | `anchor-sql-<suffix>` / `anchordb` | Override to reuse manually-created resources. |
| `appServiceName` / `appServicePlanName` | `anchor-api-<suffix>` / `ASP-anchorrg-b49b` | Backend App Service + plan. |
| `signalrName` / `staticWebAppName` | `anchor-signalr` / `anchor-dashboard` | SignalR + dashboard SWA. |

> **Portal fallback (no CLI).** If `az` gives you trouble, create each resource by
> hand following the [portal walk-through in `infra/README.md`](../infra/README.md#alternative-manual-setup-via-the-azure-portal)
> (SQL DB, App Service, SignalR, Static Web App), then add the App Service
> application settings from [Step 6](#step-6--app-service-application-settings)
> manually — Bicep would otherwise have wired them.

---

## Step 5 — Read the deployment outputs

The deploy emits the resource names + URLs and echoes back the Entra/CORS values
it applied. Read them back:

```bash
az deployment group show --resource-group anchor-rg \
  --name main --query properties.outputs -o json
```

You need these (Bicep [output](../infra/main.bicep) names):

| Output | Example | Used for |
| --- | --- | --- |
| `appServiceName` | `anchor-api-yourschool` | GitHub variable `AZURE_WEBAPP_NAME`; publish profile |
| `appServiceUrl` | `https://anchor-api-yourschool.azurewebsites.net` | GitHub variable `API_BASE_URL` |
| `staticWebAppName` | `anchor-dashboard` | SWA deployment token |
| `swaUrl` | `https://<host>.azurestaticapps.net` | SPA redirect URI; CORS origin |
| `entraTenantId` | your tenant GUID | GitHub variable `ENTRA_TENANT_ID` |
| `entraClientId` | the API client ID | echo of what you supplied |
| `entraAudience` | `api://<api-client-id>` | base of the dashboard `API_SCOPE` |

### Finish the SPA redirect URI + API pre-authorization

Now that you know `swaUrl`, complete the SPA app registration from Step 3b.

```bash
# Redirect URI is the SWA URL with a trailing slash.
az ad app update --id "$SPA_CLIENT_ID" --web-redirect-uris "<swaUrl>/"

# Pre-authorize the SPA to request the API's access_as_user scope.
# <scope-id> is the access_as_user scope GUID from Step 3a.
az ad app permission add --id "$SPA_CLIENT_ID" \
  --api "$API_CLIENT_ID" --api-permissions "<scope-id>=Scope"
```

> **Redirect URI type.** The dashboard is an MSAL.js SPA, so the redirect should
> be registered under the app's **Single-page application** platform in the portal
> (**Authentication → Add a platform → Single-page application → `<swaUrl>/`**).
> The script registers it via `--web-redirect-uris` for simplicity; if browser
> sign-in fails with a redirect/PKCE error, re-add the URI under the SPA platform.

---

## Step 6 — App Service application settings

The Bicep deploy already set most of these (`ASPNETCORE_ENVIRONMENT`,
`Azure__SignalR__ConnectionString`, `AzureAd__Instance`, `AzureAd__TenantId`,
`AzureAd__ClientId`, `AzureAd__Audience`, `Cors__AllowedOrigins__0`, and the
`DefaultConnection` connection string). If you used the **portal fallback** in
Step 4, add them yourself now — see the table in
[`docs/RELEASE.md`](RELEASE.md#azure-app-service--application-settings). The
double-underscore form maps to .NET nested keys (`AzureAd__TenantId` →
`AzureAd:TenantId`).

### 6c. Add the API client secret to the App Service

The script does this automatically (after the deploy, and re-applied on every
run because Bicep rewrites the App Service's app settings). Do it by hand only if
you're following this guide manually. It adds the API client secret from Step 3a
so the on-behalf-of Graph **user-directory search** works (without it that call
fails on first use — the API still starts):

```bash
az webapp config appsettings set \
  --name anchor-api-yourschool --resource-group anchor-rg --settings \
  AzureAd__ClientCredentials__0__SourceType=ClientSecret \
  AzureAd__ClientCredentials__0__ClientSecret='<the-secret-from-step-3a>'
```

(Or App Service → **Settings → Environment variables** → add the two settings.)

> Bicep does **not** declare these two settings and overwrites the whole app-setting
> collection on each deploy, so if you redeploy by hand you must re-apply them
> afterwards (the script does this for you).

---

## Step 7 — Grant admin consent [MAY NEED A HUMAN]

The script attempts this automatically at the end of its run. It only lands in
your lap if the account running it isn't a directory admin (or the brand-new
registration/service-principal hasn't replicated yet) — in which case it prints
these commands. Tenant-wide admin consent requires a **Global Administrator /
Privileged Role Administrator**. Run, as such an admin:

```bash
az ad app permission admin-consent --id "$API_CLIENT_ID"
az ad app permission admin-consent --id "$SPA_CLIENT_ID"
```

Or in the portal: **Entra → App registrations → `<app>` → API permissions →
Grant admin consent for `<tenant>`**. Do this for all three registrations. You can
finish the rest of the setup first and grant consent afterwards.

> If consent fails with *"your organization has not subscribed to service(s)"*,
> the API's service principal is missing — run [Step 3d](#3d-create-service-principals)
> first, then retry.

---

## Step 8 — GitHub secrets and variables

The deploy workflows ([`backend-deploy.yml`](../.github/workflows/backend-deploy.yml),
[`dashboard-deploy.yml`](../.github/workflows/dashboard-deploy.yml)) and the
[`agent-release.yml`](../.github/workflows/agent-release.yml) packaging read the
deploy target from repo Actions **variables** and auth from **secrets** — so a
fork deploys to its own Azure with no source edits. The authoritative inventory is
in [`docs/RELEASE.md`](RELEASE.md#required-secrets-and-variables); set exactly
these.

### Fetch the two credentials

```bash
# Static Web App deployment token (→ AZURE_STATIC_WEB_APPS_API_TOKEN)
az staticwebapp secrets list --name anchor-dashboard \
  --query properties.apiKey -o tsv

# App Service publish profile XML (→ AZURE_WEBAPP_PUBLISH_PROFILE)
az webapp deployment list-publishing-profiles \
  --name anchor-api-yourschool --resource-group anchor-rg --xml
```

### Set the secrets

| Secret | Value | Used by |
| --- | --- | --- |
| `AZURE_WEBAPP_PUBLISH_PROFILE` | the publish-profile XML above | `backend-deploy.yml` |
| `AZURE_STATIC_WEB_APPS_API_TOKEN` | the SWA deployment token above | `dashboard-deploy.yml` |

```bash
az webapp deployment list-publishing-profiles --name anchor-api-yourschool \
  --resource-group anchor-rg --xml | gh secret set AZURE_WEBAPP_PUBLISH_PROFILE --repo OWNER/REPO --body -

az staticwebapp secrets list --name anchor-dashboard --query properties.apiKey -o tsv \
  | gh secret set AZURE_STATIC_WEB_APPS_API_TOKEN --repo OWNER/REPO --body -
```

(`GITHUB_TOKEN` is auto-provided by Actions — no setup.)

### Set the variables

| Variable | Value | Used by |
| --- | --- | --- |
| `AZURE_WEBAPP_NAME` | `appServiceName`, e.g. `anchor-api-yourschool` | `backend-deploy.yml` |
| `API_BASE_URL` | `appServiceUrl` | `dashboard-deploy.yml` |
| `ENTRA_TENANT_ID` | your tenant GUID | `dashboard-deploy.yml` |
| `ENTRA_CLIENT_ID` | the **dashboard SPA** client ID (Step 3b) | `dashboard-deploy.yml` |
| `API_SCOPE` | `<entraAudience>/access_as_user` (two-app), or `<client-id>/.default` (single-app) | `dashboard-deploy.yml` |
| `AGENT_CLIENT_ID` | the **agent** public-client ID (Step 3c) | `agent-release.yml` |

```bash
gh variable set AZURE_WEBAPP_NAME --repo OWNER/REPO --body "anchor-api-yourschool"
gh variable set API_BASE_URL      --repo OWNER/REPO --body "https://anchor-api-yourschool.azurewebsites.net"
gh variable set ENTRA_TENANT_ID   --repo OWNER/REPO --body "<your-tenant-guid>"
gh variable set ENTRA_CLIENT_ID   --repo OWNER/REPO --body "$SPA_CLIENT_ID"
gh variable set API_SCOPE         --repo OWNER/REPO --body "api://$API_CLIENT_ID/access_as_user"
gh variable set AGENT_CLIENT_ID   --repo OWNER/REPO --body "$AGENT_CLIENT_ID"
```

> **`AGENT_CLIENT_ID` vs `ENTRA_CLIENT_ID`.** These are deliberately different
> registrations. The agent uses WAM (a public client) and `ENTRA_CLIENT_ID` is the
> dashboard's SPA — pointing the agent at the SPA id makes release sign-in fail
> with `WAM_provider_error_…` (`0xCAA2000x`) (#271). The agent shares
> `ENTRA_TENANT_ID` and `API_SCOPE`, only the client id differs.

> **`API_SCOPE` form.** With **two** app registrations the script sets
> `<entraAudience>/access_as_user` (i.e. `api://<api-client-id>/access_as_user`) —
> the scope you exposed in Step 3a. When the SPA and API **share** one
> registration, use `<client-id>/.default` (no `api://` prefix) instead, to avoid
> Entra's `AADSTS90009` "token for itself" error — this is the source default in
> [`dashboard/lib/auth/msal_config.dart`](../dashboard/lib/auth/msal_config.dart).
> Each dashboard variable falls back to that source default when unset, so a
> contributor building locally is unaffected.

---

## Step 9 — Confirm branch protection, then deploy

1. Confirm `CI Gate / gate` is the required status check on `main` (and `develop`);
   no other check should be required. See
   [`docs/RELEASE.md`](RELEASE.md#branch-protection--required-check-implications) —
   do **not** add `Backend Deploy`/`Backend CI` as required checks (the deploy runs
   post-merge on `main`).
2. Push a backend or dashboard change to `main`. The matching leg deploys
   automatically:
   - backend: **Backend CI** runs on `backend/**`, and on success
     `backend-deploy.yml` publishes to the App Service.
   - dashboard: a push under `dashboard/**` builds and uploads to the Static Web
     App.

EF Core migrations apply on app startup in non-Development environments, so there
is no separate migration step.

---

## Verification checklist

You have a working fork when, following only this doc:

- [ ] `az deployment group create` succeeds and outputs the resource names/URLs.
- [ ] All three Entra registrations exist, each with a service principal (Step 3d).
- [ ] Admin consent is granted for all three registrations (Step 7).
- [ ] The API client secret is set on the App Service (Step 6c).
- [ ] The two GitHub secrets and six GitHub variables are set (Step 8).
- [ ] A push to `main` triggers the matching deploy leg, and the deployed
      dashboard signs in and reaches the API.
