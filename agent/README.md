# FocusAgent (student agent)

Windows-only WinUI 3 desktop app that runs on each student's laptop during a
focus session.

Design rationale lives in [focus-system-design.md](../focus-system-design.md)
§5 (student agent behaviour) and §5.3 (native interop layer).

## Layout

```
agent/
├── FocusAgent.sln
├── src/
│   ├── FocusAgent.App      — WinUI 3 desktop app, tray + hidden main window. Builds unpackaged for dev or MSIX for distribution.
│   ├── FocusAgent.Core     — DTOs (mirroring backend SignalR payloads), settings, log paths
│   └── FocusAgent.Native   — Win32 P/Invoke surface (foreground watcher, focus enforcer, app identifier)
└── tests/
    ├── FocusAgent.Core.Tests   — xUnit tests for Core
    └── FocusAgent.Native.Tests — xUnit tests for Native
```

The Native project is intentionally isolated so all Win32 interop has a single
boundary to audit and iterate against real device behaviour.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows 10 1809 (build 17763) or newer for runtime; build host should be Windows 11
- Visual Studio 2022 17.10+ is convenient for XAML designer/debugging but not required

The first `dotnet restore` pulls Windows App SDK 1.7 and H.NotifyIcon.WinUI as
NuGet packages — no `dotnet workload` install is required for unpackaged WinUI 3
apps on .NET 10.

## Build

```powershell
cd agent
dotnet restore
dotnet build -c Debug -p:Platform=x64
```

## Run

```powershell
cd agent
dotnet run --project src/FocusAgent.App -c Debug -p:Platform=x64
```

The app starts hidden and places a tray icon. Right-click it for **Open**
(brings up the main window) and **Quit**. Only one instance can run at a time;
launching a second copy exits immediately.

`dotnet run` stays attached to the running process. Ctrl+C in the terminal
does **not** stop the app — WinUI 3 apps are WinExe with no console attached,
so the signal never reaches them. Use the tray Quit, or
`Stop-Process -Name FocusAgent.App` from another shell.

## Test

```powershell
cd agent
dotnet test
```

## Configuration

`src/FocusAgent.App/appsettings.json` carries:

| Key | Default | Notes |
| --- | --- | --- |
| `Backend:BaseUrl` | `http://localhost:5000` | Dev backend URL. Override per deploy. |
| `Backend:HubPath` | `/hubs/session` | Path of the backend SignalR hub. |
| `Auth:TenantId` | _empty_ | Entra tenant ID the agent signs in against. |
| `Auth:ClientId` | _empty_ | App registration (public client) ID. |
| `Auth:Scope` | `<backend-client-id>/.default` | Backend API scope requested for the access token. Uses the bare-GUID form (no `api://` prefix), matching the dashboard — Entra rejects `api://`-form requests when the agent and API share a tenant via `AADSTS90009`. Backend accepts both audience forms. |
| `Auth:LoginHint` | _empty_ | Optional UPN used as a hint when WAM has to prompt interactively. Useful on machines whose Windows account is not on the school tenant (e.g. dev laptops) — set it to the school UPN to pre-fill the WAM picker. Has no effect once a school-tenant account is cached. |
| `Dev:ImpersonateOid` | _empty_ | **Dev-only.** GUID of a seeded user OID to impersonate on the hub connection. Sends `X-Dev-Impersonate-Oid` on the SignalR negotiate request; the backend honors it only when running with `ASPNETCORE_ENVIRONMENT=Development`. See [Single-machine dev verification](#single-machine-dev-verification) below. |

`Auth:TenantId`, `Auth:ClientId` and `Auth:Scope` are required. The agent fails
fast at startup with a clear error message if any are empty.

### Single-machine dev verification

Verifying student-agent behaviour (`SessionStarted`, the join-confirmation
toast, decline, focus enforcement) historically required a second machine
signed in with a second school Entra account. To unblock single-machine
verification ([issue #38](https://github.com/yvanvds/Anchor/issues/38)),
the backend accepts a dev-only impersonation header on the hub:

Set `Dev:ImpersonateOid` in `appsettings.Development.json` to the OID of a
seeded user (e.g. the dev `Dev Student` at
`22222222-2222-2222-2222-222222222222`):

```json
{
  "Dev": {
    "ImpersonateOid": "22222222-2222-2222-2222-222222222222"
  }
}
```

The agent sends `X-Dev-Impersonate-Oid: <oid>` on the SignalR negotiate
request, and the backend resolves the hub's current user from that OID
instead of the token's `oid` claim. With this set you can:

- Run the dashboard signed in as your real teacher account and run the
  agent on the same machine acting as a seeded student — start a session
  from the dashboard, the agent receives `SessionStarted` and exercises
  the full join-confirmation / decline / focus flow under the seeded
  student identity.
- Switch identity by changing the OID — useful for testing "two students"
  scenarios from one laptop by relaunching the agent with a different
  seeded OID.

The override is honored **only** when the backend is running with
`ASPNETCORE_ENVIRONMENT=Development`; production rejects the header.

### Local overrides

`appsettings.Development.json` (gitignored, sits next to `appsettings.json`) is
loaded after the base config so it can override individual keys without
touching the committed defaults. Use it on dev machines to supply the agent's
tenant/client IDs (and optionally a `LoginHint`) without committing them:

```json
{
  "Auth": {
    "TenantId": "<school-tenant-guid>",
    "ClientId": "<agent-public-client-guid>",
    "LoginHint": "you@school.example"
  }
}
```

The file is wired into the build with `<CopyToOutputDirectory>PreserveNewest`,
so creating it in `src/FocusAgent.App/` is enough — no extra MSBuild glue
needed.

Logs roll daily into `%LOCALAPPDATA%\Anchor\FocusAgent\logs\focusagent-*.log`
(14-day retention) via Serilog, plus debug output during development. When
running packaged (MSIX), `LocalApplicationData` resolves to
`%LOCALAPPDATA%\Packages\net.arcadia.anchor.focusagent_<hash>\LocalCache\Local`
— same code path, sandboxed location.

## MSIX packaging

`FocusAgent.App` is set up as a single-project MSIX. The default build is
unpackaged (so `dotnet run` works for dev); passing
`/p:WindowsPackageType=MSIX` flips it to a packaged build that emits an
`.msix` under `agent/artifacts/` (gitignored).

### Build the MSIX

```powershell
cd agent
msbuild src/FocusAgent.App/FocusAgent.App.csproj `
  /restore `
  /p:Configuration=Release `
  /p:Platform=x64 `
  /p:WindowsPackageType=MSIX `
  /p:AppxPackageSigningEnabled=false
```

Output: `agent/artifacts/FocusAgent.App_0.1.0.0_x64_Test/FocusAgent.App_0.1.0.0_x64.msix`
(unsigned, ~40 MB — the Windows App SDK runtime is bundled because
`WindowsAppSDKSelfContained=true`). The `_Test` folder also contains
`Add-AppDevPackage.ps1` / `Install.ps1` helpers generated by the MSIX
tooling.

`AppxPackageSigningEnabled=false` is deliberate — the build leaves the
package unsigned so the cert never has to live in the repo or in CI secrets.
You sign it manually with the dev cert described below before installing.

### Sign with a self-signed dev cert (one-off, per dev machine)

```powershell
# Create a self-signed code-signing cert in your CurrentUser\My store.
# Subject MUST match Package.appxmanifest <Identity Publisher="..."> exactly.
$cert = New-SelfSignedCertificate `
  -Type CodeSigningCert `
  -Subject 'CN=Arcadia' `
  -KeyUsage DigitalSignature `
  -FriendlyName 'Anchor FocusAgent Dev Signing' `
  -CertStoreLocation 'Cert:\CurrentUser\My' `
  -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')

# Export the public cert so it can be trusted on the dev/test machine.
Export-Certificate -Cert $cert -FilePath agent/artifacts/anchor-dev.cer | Out-Null

# Trust the cert (one-off, requires admin). Installs into LocalMachine\TrustedPeople.
Import-Certificate -FilePath agent/artifacts/anchor-dev.cer `
  -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

Then sign each freshly built MSIX with `signtool` from the Windows SDK
(`Set-AuthenticodeSignature` does **not** support .msix — it returns
"SIP_SUBJECTINFO structure didn't contain the required data"):

```powershell
$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe' `
  | Sort-Object -Descending FullName | Select-Object -First 1
$msix = Get-ChildItem agent/artifacts -Recurse -Filter '*.msix' | Select-Object -First 1
$cert = Get-ChildItem Cert:\CurrentUser\My |
  Where-Object Subject -eq 'CN=Arcadia' | Select-Object -First 1
& $signtool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint $msix.FullName
# Optional once the cert is trusted (see above): confirm the chain validates
& $signtool.FullName verify /pa $msix.FullName
```

`.pfx`/`.cer` files are gitignored at the repo root; never commit them.

### Install (sideload)

```powershell
Add-AppxPackage -Path agent/artifacts/FocusAgent.App_0.1.0.0_x64_Test/FocusAgent.App_0.1.0.0_x64.msix
```

(or run the generated `Install.ps1` from the same folder, which validates
the cert is trusted and installs in one step)

First launch prompts the user to allow the StartupTask. Declining means the
agent will not auto-start at login (the user can flip this later from Windows
Settings → Apps → Startup). The MSIX declares a single startup task,
`AnchorFocusAgentStartup`, which launches `FocusAgent.App` at user login.

### Tenant distribution via Intune

For pilot or school-wide rollout we do **not** buy a paid code-signing
certificate. Instead:

1. Sign the MSIX with the self-signed cert above (the production cert can be
   the same one — keep it in a secure store, not the repo).
2. Upload the `.msix` to Intune as a "Line-of-business app" (or Win32 app).
3. Push the **signing cert's public key** (`anchor-dev.cer`) to managed
   devices via an Intune **Trusted certificate** profile, targeted at the
   `Trusted Root Certification Authorities` or `Trusted People` store
   (LocalMachine).
4. Assign the app to a user / device group. Intune installs it silently.

Once the cert is in the device's trust store, the MSIX installs cleanly with
no SmartScreen prompt and no paid CA involvement. See issue #1 (closed) for
the decision rationale.
