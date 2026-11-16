# Intune rollout — the student agent as a Win32 app

This is the guide for **school IT**: how to push the Anchor agent to school
laptops with Microsoft Intune, and the Edge policy that goes with it. Since
September 2026 most students get the agent this way (about 95% at the school
Anchor was built for). Students on their own laptop with a personal account, the
other ~5%, still run `Setup.exe` themselves; that path is
[unchanged](#manual-install-own-laptops).

The app settings in this guide are the ones the Win32 app at that school uses, as
read from Intune on 2026-11-16.

Before you start you need a working backend ([`SETUP.md`](SETUP.md)) and an
agent release from your school's own repository
([`RELEASE.md`](RELEASE.md#agent-agent-v)). Upstream's release is no use to a
school: its `Setup.exe` has upstream's backend and Entra IDs baked in (#361).

A few things can only be settled on a real Intune-managed laptop. They are marked
**[TO CONFIRM IN THE PILOT]** and collected in the
[pilot checklist](#pilot-checklist) at the end. Until the first pilot has run,
treat them as expectations, not facts.

Related references:

- [`RELEASE.md`](RELEASE.md#desktop-agent--velopack--github-releases-agent-v) —
  how an agent release is cut and what it carries.
- [`agent/README.md`](../agent/README.md#packaging--distribution) — packaging
  and distribution from the agent's side.
- The install script
  [`agent/scripts/release-assets/Install-AnchorAgent.ps1`](../agent/scripts/release-assets/Install-AnchorAgent.ps1)
  (#457), an alternative install command that the app in this guide doesn't use:
  see [the install script](#alternative-the-install-script).
  `Get-Help .\Install-AnchorAgent.ps1 -Full` prints its help.

## How the pieces fit

| | Intune-managed laptop | Own laptop, personal account |
| --- | --- | --- |
| Installs the agent | Intune: a Win32 app in user context that runs `Setup.exe --silent` | The student runs `Setup.exe` |
| Starts it right after the install | The agent's install hook (#487), and `Update.exe start` in the install command | `Setup.exe` |
| Edge extension | An Intune configuration profile (`ExtensionInstallForcelist`) | The agent: one UAC prompt, or the guided install (#405) |
| Updates | The agent itself (Velopack); Intune only when you raise the [minimum version](#raising-the-minimum-version) | The agent itself (Velopack) |

Both paths end in the same place: a per-user install in
`%LOCALAPPDATA%\Anchor.Agent`, with no admin rights, started at every sign-in by
the `AnchorFocusAgent` value under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (#225), and kept up to date
from the GitHub Releases of your repository. Because the install is per user,
each student who signs in to a shared laptop has their own copy. An agent that a
student installed by hand is found by the [detection rule](#detection-rule) like
any other, so Intune leaves it alone as long as it has at least the rule's
minimum version.

## Step 1 — Package the `.intunewin`

1. Download both assets from the **same** release of your repository, into a
   folder that holds nothing else (the packaging tool takes the whole folder):

   ```powershell
   gh release download agent-v<version> --repo <owner>/<repo> `
       --pattern Anchor.Agent-win-Setup.exe --pattern Install-AnchorAgent.ps1 `
       --dir anchor-intune
   ```

   Or download the two files from the release page. The install command runs
   only `Setup.exe`; the script comes along so the same package also serves the
   [alternative install command](#alternative-the-install-script).
2. Package the folder with the
   [Microsoft Win32 Content Prep Tool](https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool),
   with `Setup.exe` as the setup file:

   ```powershell
   IntuneWinAppUtil.exe -c .\anchor-intune -s Anchor.Agent-win-Setup.exe -o .\intunewin -q
   ```

   `-c` is the source folder, `-s` the setup file, `-o` the output folder, and
   `-q` overwrites an earlier output without asking. The result is
   `.\intunewin\Anchor.Agent-win-Setup.intunewin`.

The first package creates the app ([Step 2](#step-2--create-the-win32-app)).
After that, each release's package becomes a new version of the same app: see
[A new package per release](#a-new-package-per-release).

## Step 2 — Create the Win32 app

In the Intune admin center: **Apps** → **Windows** → **Create** (**Add** in some
consoles) → app type **Windows app (Win32)**, and select the `.intunewin`.

### App information

Name and publisher are free text. **Anchor Focus Agent** matches the name Windows
shows under Installed apps. **App version** is only shown in the admin center;
Intune doesn't act on it. Setting it to the agent version in the package, each
time you upload one, shows which release a new laptop gets.

### Program

| Setting | Value |
| --- | --- |
| Install command | `cmd.exe /c ".\Anchor.Agent-win-Setup.exe --silent && "%LOCALAPPDATA%\Anchor.Agent\Update.exe" start"` |
| Uninstall command | `"%LOCALAPPDATA%\Anchor.Agent\Update.exe" --uninstall --silent` **[TO CONFIRM IN THE PILOT]**, see [Uninstall](#uninstall) |
| Installation time required (mins) | Leave the default (60) |
| Allow available uninstall | No |
| Install behavior | **User** (never System, see below) |
| Device restart behavior | No specific action (`suppress` in Microsoft Graph): the agent never needs a restart |
| Return codes | Keep the defaults, see [return codes](#return-codes) |

What the install command does:

- `Setup.exe --silent` installs the agent for the signed-in student, into
  `%LOCALAPPDATA%\Anchor.Agent`, without showing a page. A silent `Setup.exe`
  doesn't start the agent itself. Run over an installed agent, it stops that
  agent first, also in the middle of a lesson, and installs the package's version
  over it. Intune only runs the install command when the
  [detection rule](#detection-rule) doesn't find the agent at the minimum version
  or newer; when that happens on a laptop that has the agent, it is
  [on purpose](#raising-the-minimum-version).
- `&&` runs the second command only when `Setup.exe` succeeded.
- `"%LOCALAPPDATA%\Anchor.Agent\Update.exe" start` is Velopack's command to start
  the installed app: it starts the agent right away. The next sign-in, when the
  `AnchorFocusAgent` Run value would start it, can be weeks away on a laptop that
  sleeps instead of restarting. Since #487 (agent 0.5.0) the agent's install hook
  also starts it once `Setup.exe` has exited; whichever start comes second finds
  the agent running and quietly exits.
- `cmd.exe /c` runs the two as one command and expands `%LOCALAPPDATA%` to the
  student's profile.

The install behavior must be **User**. Velopack installs per user, so in system
context the agent would land in the SYSTEM profile, out of the student's reach.

### Requirements

- **Operating system architecture:** x64. The release is built for `win-x64`
  only.
- **Minimum operating system:** Windows 10 1809, Anchor's minimum.

### Detection rule

**Manually configure detection rules** → **Add** → rule type **File**:

| Setting | Value |
| --- | --- |
| Path | `%LOCALAPPDATA%\Anchor.Agent\current` |
| File or folder | `FocusAgent.App.exe` |
| Detection method | String (version) |
| Operator | Greater than or equal to |
| Value | `0.5.0.0` |
| Associated with a 32-bit app on 64-bit clients | No |

The rule reads the file version of the agent's exe in the student's profile.
That version is the release's `<VersionPrefix>` with `.0` added: `agent-v0.4.0`
has `0.4.0.0`, `agent-v0.5.0` has `0.5.0.0`. The value is the **minimum**
version a laptop needs, not the version in the package: an agent that has
updated itself past it still matches, and Intune leaves it alone. It was
`0.2.2.0` until 2026-11-16, when it was raised to `0.5.0.0` on purpose, to bring
every laptop up to 0.5.0. Raising it has a cost; read
[Raising the minimum version](#raising-the-minimum-version) before you change it.

Keep the operator at **Greater than or equal to**, never **Equals**. An "equals"
rule stops matching as soon as the agent has updated itself past that version.
Intune then treats the agent as missing and runs the install command again on
every cycle: `Setup.exe` puts the package's older version back over the updated
agent, stopping it on the way, the agent updates itself again, and the next cycle
starts over.

### Return codes

Keep Intune's default list (0 and 1707 Success, 3010 Soft reboot, 1641 Hard
reboot, 1618 Retry) and add none. The non-zero codes in it come from Windows
Installer; leave them as they are. Intune counts every code that isn't in the
list as **Failed**.

`cmd.exe` exits with the code of the last command it ran: `Update.exe start`'s
when `Setup.exe` succeeded, `Setup.exe`'s when it failed (the `&&` then skips the
start).

| Code | Intune result | Meaning | What to do |
| --- | --- | --- | --- |
| 0 | Success | `Setup.exe` installed the agent and `Update.exe` started it. Intune then checks the detection rule. | Nothing. |
| 1, or any other code not in the list | Failed | `Setup.exe` failed (Velopack's Setup exits with 1 on any failure), or `Update.exe` couldn't start the agent. | Read Setup's lines in `velopack.log` and the updater's in `velopack_Anchor.Agent.log` (see [logs](#logs)). |

An install that exits with 0 but ends as **not detected** (0x87D1041C) usually
means the detection rule's minimum is higher than the version in the package. See
[Raising the minimum version](#raising-the-minimum-version).

The install script's own codes (10 to 15) only apply when the script is the
install command: see [its exit codes](#exit-codes).

### Assignments

Assign the app as **Required** to a group of students, the ones who should get
the agent. Students on an unmanaged laptop of their own aren't enrolled, so the
assignment doesn't reach them; they install by hand. Under end user
notifications, **Hide all toast notifications** keeps Intune's own install
messages out of the student's way, if you prefer that.

## Step 3 — Force-install the Edge extension

**Devices** → **Configuration** → **Create** → **New policy** → platform
**Windows 10 and later**, profile type **Settings catalog**. Add the setting
**Microsoft Edge** → **Extensions** → **Control which extensions are installed
silently** (`ExtensionInstallForcelist`), enable it, and add this entry:

```
dnkimhodjfogjibnbbfdjdapgmmiojio;https://edge.microsoft.com/extensionwebstorebase/v1/crx
```

That is the entry the agent itself uses (`EdgeExtensionPolicy.ForcelistEntry` in
`FocusAgent.Core`): the extension's ID and the Edge Add-ons store's update URL.
The settings catalog has the setting both for the device (written under `HKLM`)
and for the user (marked "(User)", written under `HKCU`). The agent looks in
both, so either works; the device setting matches what the agent writes itself
on other laptops. Assign the profile to the same groups as the app, before the
app or together with it. If you run a [test ring](#test-ring-the-self-hosted-extension),
exclude its group from this profile.

Why it matters: at every start the agent reads `ExtensionInstallForcelist` under
`HKLM` and `HKCU`, without elevation. With Anchor's entry in either, it asks the
student nothing (#405). Without it, the agent explains the step in its own window
and asks for one UAC prompt to add the entry itself, which a student without admin
rights can't approve; they end up at the extension's Edge Add-ons page instead
(the guided install). So a laptop where the agent starts before the profile has
arrived shows that setup window once **[TO CONFIRM IN THE PILOT]**; at its next
start the agent finds the profile's entry.

Uninstalling the agent never removes an entry that Intune manages: the extension
stays force-installed until you unassign the profile.

## Step 4 — Let laptops reach the update feed

The agent updates itself from the GitHub Releases of your repository
(`Update:GithubRepoUrl`, baked in when the release is built, #360), without
signing in to GitHub. Besides your backend's App Service (by default
`anchor-api-<suffix>.azurewebsites.net`, with WebSocket upgrades allowed) and
Entra sign-in (`login.microsoftonline.com`), the school network must let laptops
reach, over HTTPS:

- `github.com`, which serves the newest release's files at
  `https://github.com/<owner>/<repo>/releases/latest/download/<file>`;
- `*.githubusercontent.com`, where GitHub redirects those downloads.

Edge also needs the Edge Add-ons store (`edge.microsoft.com`, the update URL in
the entry above), as for any store extension.

The agent doesn't use the GitHub API (`api.github.com`) for its updates (#465).
That API allows 60 requests an hour per public IP address for clients that don't
sign in, and every laptop behind the school's address would share that budget, so
a morning of laptops starting up would use it in minutes. Instead the agent reads
the update feed (`releases.win.json`) and the package it names straight from the
newest release's download address, which isn't held to that limit. "Newest" is the
release GitHub marks **Latest**; agent releases are the only full releases the
repository publishes, so that is always the newest agent release. (The
[test ring](#test-ring-the-self-hosted-extension)'s `extension-test-ring` release
is a pre-release, which GitHub never marks Latest.)

## Test ring: the self-hosted extension

Optional. A change to the extension reaches students only after the Edge Add-ons
review, which takes days. The test ring lets a few laptops get it right away: a
second configuration profile, assigned to a small test group, force-installs a
build of the extension that the repository signs and hosts itself, instead of the
store build (#497). Everyone else stays on the store build. If the test ring
proves reliable, moving the main group over later is a change to these profiles.

**Before you create the profile**, the test ring has to exist on the repository
side: the signing key, its ID in `extension/test-ring.json`, an **agent release**
built with that ID running on the test laptops, and a first published build
([`RELEASE.md`](RELEASE.md#extension-test-ring)). The agent goes first: an agent
that doesn't know the test-ring ID opens the guided install on those laptops, and
Edge doesn't let the extension connect to it.

1. **A test group**, with the laptops (or students) of the test ring. Keep it in
   the agent app's assignment as before.
2. **Exclude that group from the store profile** of
   [Step 3](#step-3--force-install-the-edge-extension) (Assignments → Excluded
   groups), so a laptop never gets both builds. Two Anchor extensions in one Edge
   would both filter.
3. **The test-ring profile.** Same as Step 3 (**Settings catalog** →
   **Microsoft Edge** → **Extensions** → **Control which extensions are installed
   silently**), with the entry `extension/scripts/test-ring-key.mjs` printed
   when the key was made:
   ```
   <test-ring-id>;https://github.com/<owner>/<repo>/releases/download/extension-test-ring/updates.xml
   ```
   `<test-ring-id>` is the `extensionId` in
   [`extension/test-ring.json`](../extension/test-ring.json). Assign it to the
   test group only. Edge uses the URL in this entry for the first install only;
   after that it follows the `update_url` inside the extension, which is the same
   address.

The agent takes either entry for Anchor's: with the test-ring entry under `HKLM`
or `HKCU`, it asks the student nothing, finds the test-ring build in the Edge
profile, and lets it connect to the witness host. The agent never writes the
test-ring entry itself; on a laptop without any entry it still offers the store
entry.

**[TO CONFIRM IN THE PILOT]** Microsoft documents off-store force-installs on
Windows only for devices joined to an Active Directory domain
([`ExtensionInstallForcelist`](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-policies/extensioninstallforcelist)).
It says nothing about Entra-joined, Intune-enrolled devices (Chrome documents
MDM-enrolled devices as qualifying, and Edge shares that code). Check it on one
test laptop before relying on it:

- `edge://policy` → **Reload policies**: `ExtensionInstallForcelist` lists the
  test-ring entry with status **OK**. An error or warning on it means Edge
  ignores the entry; the laptop then has no Anchor extension, and the agent opens
  the guided install (the store listing).
- `edge://extensions`: **Anchor** is there, installed by your organization, with
  the test-ring ID and a version that ends in `test ring <n>`. **Developer mode**
  → **Update** fetches a newer test-ring build right away instead of within a
  few hours.
- The laptop must reach `github.com` and `*.githubusercontent.com`
  ([Step 4](#step-4--let-laptops-reach-the-update-feed)); the build is served
  from there.

To take a laptop out of the test ring, move it out of the test group: the
test-ring profile no longer applies, the store profile does again, and Edge
swaps the builds.

## Updates

Installed agents update themselves through Velopack. The agent checks its feed at
every start, every `Update:CheckInterval` (6 hours of awake time), and on wake
and unlock once the last check is that old. It downloads a new release and
restarts into it as soon as no session is going on (#456); an agent is never
stopped mid-session for an update (#449). So for most releases Intune has nothing
to do on a laptop that has the agent.

Intune has two levers on top of that: a new package for laptops without the
agent, and the detection rule's minimum version for laptops whose agent is too
old.

### A new package per release

For each agent release, [package](#step-1--package-the-intunewin) its `Setup.exe`
and script, and upload the `.intunewin` as a new version of the **same** app: in
the app's **Properties** → **App information** → **Edit**, select the new file.
(Through Microsoft Graph, that is a new entry under the app's `contentVersions`,
then the app's `committedContentVersion` pointed at it.) Keep the install command
and the detection rule as they are.

A new package alone only reaches laptops **without** the agent: a new student's,
or one where the agent was uninstalled. Laptops whose agent matches the detection
rule run nothing; their agents keep updating themselves. Uploading each release
means a new laptop starts on a recent version, instead of installing an old one
and downloading the newest release at its first start.

### Raising the minimum version

The value in the [detection rule](#detection-rule) is a deliberate lever. Raise
it, and at its next check-in Intune runs the install command on every laptop
whose agent is older, for the student signed in there. `Setup.exe --silent` then
installs the package's version over that agent. That reaches laptops the agent's
own updates haven't, for instance one whose agent isn't running and so doesn't
check. An agent that has updated itself to the new minimum by the time Intune
checks is left alone.

The cost: `Setup.exe --silent` over an installed agent stops it, mid-lesson or
not, and Intune doesn't know when lessons are. Since #487 the new version's
install hook starts the agent again once `Setup.exe` has finished, and the install
command's `Update.exe start` starts it as well, so the agent is gone for the
length of the install, not until the next sign-in. On a laptop in class that is
still an agent restart during the lesson. So:

- **Only raise it to a version that has #487: 0.5.0 or later.** It is the install
  hook of the version being installed that starts the agent again; with an older
  version in the package, `Update.exe start` is the only start left.
- **Never raise it above the version in the uploaded package.** Upload the
  package first, then raise the minimum to that version or lower. Intune checks
  the detection rule right after the install; when the version it just installed
  is still below the minimum, it reports the app as not detected and runs the
  install command again at every check-in, stopping the agent each time.
- **Only when forcing laptops up is worth an agent restart during class.** Most
  releases aren't: the agents update themselves between sessions anyway.
- **Keep the operator at Greater than or equal to**, never **Equals** (see
  [why](#detection-rule)).

### No supersedence

**Don't use supersedence**, and never **Uninstall previous version**. That runs
the uninstall command, which stops the agent, mid-lesson or not, and removes it
before the new app installs it again. Without it, a superseding app with the same
detection rule finds the agent installed and has nothing to do, so supersedence
buys nothing over a new version of the same app.

## Uninstall

Velopack registers the agent under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Anchor.Agent`, with
`UninstallString` set to `"<install folder>\Update.exe" --uninstall` and
`QuietUninstallString` to the same command with `--silent`. The install folder is
`%LOCALAPPDATA%\Anchor.Agent`, so the app's uninstall command is:

```
"%LOCALAPPDATA%\Anchor.Agent\Update.exe" --uninstall --silent
```

**[TO CONFIRM IN THE PILOT]** Read the real values on a test laptop, as the
student, before relying on it:

```powershell
Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Anchor.Agent' |
    Select-Object UninstallString, QuietUninstallString
```

Then check that an uninstall from Intune runs it in the student's profile. If
Intune doesn't expand `%LOCALAPPDATA%` in the command, let `cmd.exe` do it, as
the install command does:
`cmd.exe /c ""%LOCALAPPDATA%\Anchor.Agent\Update.exe" --uninstall --silent"`.

What an uninstall does: it stops the agent and its witness host, also in the
middle of a lesson, so don't run it during school hours. It then runs the agent's
uninstall hook, which removes the `AnchorFocusAgent` Run value, the witness host's
registration and the Edge entries the agent wrote itself (never one Intune
manages, see [Step 3](#step-3--force-install-the-edge-extension)), and finally
removes the shortcuts, the install folder and the uninstall entry. The detection
rule no longer matches after that.

To remove the agent from a group's laptops, take the group out of the Required
assignment and assign the app to it as **Uninstall**.

## Manual install (own laptops)

Unchanged. The student runs `Anchor.Agent-win-Setup.exe` from your release. It
isn't code-signed yet, so Windows SmartScreen asks them to confirm once. Setup
installs the agent for that Windows user, shows a success page and starts the
agent. The agent then sets up the extension itself: one UAC prompt to add the
Edge policy, or the guided install for a student who can't or won't approve it
(#405). Updates work as above.

## Trying it on a test laptop

Before you assign the app widely, run the install command by hand. Run it as the
test student, not as an administrator: it installs the agent into the profile of
whoever runs it. Open a Command Prompt (not PowerShell, which passes the nested
quotes on differently) in a folder that holds `Anchor.Agent-win-Setup.exe`, and
run:

```bat
cmd.exe /c ".\Anchor.Agent-win-Setup.exe --silent && "%LOCALAPPDATA%\Anchor.Agent\Update.exe" start"
echo %ERRORLEVEL%
```

The agent's tray icon should appear and the exit code should be 0. Then check
what the detection rule will read, in PowerShell:

```powershell
(Get-Item "$env:LOCALAPPDATA\Anchor.Agent\current\FocusAgent.App.exe").VersionInfo.FileVersion
```

It prints the release's version with `.0` added, which must be at least the
rule's minimum.

Run the install command a second time, with the agent running: that is what a
raised minimum version does on a laptop in class. The tray icon goes away while
`Setup.exe` reinstalls, and comes back on its own once it has finished. When the
install hook's start was the first, the agent's log says it was "Started again
after an update or install that was applied without restarting the agent".

## Logs

| What | Where |
| --- | --- |
| `Setup.exe`, run by the install command | `%LOCALAPPDATA%\velopack\velopack.log` (the lines that start with `[setup:`) |
| Velopack's `Update.exe` (`start`, updates, uninstall) and the agent's install and update hooks | `%LOCALAPPDATA%\velopack\velopack_Anchor.Agent.log` |
| The agent | `%LOCALAPPDATA%\Anchor\FocusAgent\logs\focusagent-*.log` (one per day, kept 14 days) |
| Intune Management Extension | `C:\ProgramData\Microsoft\IntuneManagementExtension\Logs` (`IntuneManagementExtension.log`, `AppWorkload.log`) |
| The install script, only when it is the install command | `%LOCALAPPDATA%\Anchor\FocusAgent\logs\intune-install.log`, and `intune-setup.log` for the `Setup.exe` it runs (see [its logs](#its-logs)) |

## Alternative: the install script

`Install-AnchorAgent.ps1` (#457) is in every agent release next to `Setup.exe`
(releases up to `agent-v0.3.1` were cut before it existed), and in the package
from [Step 1](#step-1--package-the-intunewin). It was written as the Win32 app's
install command, but it is **not** the install command of the app in this guide.
This section documents it for a school that wants to use it anyway.

**Why the app doesn't use it.** The script never runs `Setup.exe` over an
installed agent (step 2 below), so it can't update one. With a
[raised minimum version](#raising-the-minimum-version), Intune runs the install
command on every laptop whose agent is older; the script finds the agent
installed, skips `Setup.exe`, makes sure the agent runs and exits with 0, and the
detection rule still finds the old version. Intune then reports the app as not
detected after every attempt, at every check-in, instead of updating the agent.
Running `Setup.exe` directly does update it, and since #487 the agent comes back
on its own afterwards.

**Using it.** The package from Step 1 already holds the script. Two settings
differ from [Step 2](#step-2--create-the-win32-app):

| Setting | Value |
| --- | --- |
| Install command | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-AnchorAgent.ps1` |
| Detection rule | As [above](#detection-rule), but don't raise the minimum version once laptops have the agent. Or **File or folder exists**, with no version check. |

The install behavior stays **User**: as SYSTEM the script exits with 13. The
script isn't signed. `-ExecutionPolicy Bypass` covers the usual case, but a
PowerShell execution policy that your tenant sets by policy (the MachinePolicy
or UserPolicy scope) wins over the command line.

### What the script does

Intune runs it in user context, from the folder it unpacked the `.intunewin`
into, with `Setup.exe` beside it. In order, it:

1. **Refuses to run as SYSTEM** (exit code 13). Velopack installs per user, so in
   system context the agent would land in the SYSTEM profile, out of the
   student's reach. The app's install behavior must be **User**.
2. **Leaves an installed agent alone.** When
   `%LOCALAPPDATA%\Anchor.Agent\current\FocusAgent.App.exe` exists, it never runs
   `Setup.exe`: a silent `Setup.exe` over an installed agent stops it, also in the
   middle of a lesson. (Since #487 the agent comes back once `Setup.exe` has
   finished; before that it stayed off until the next sign-in.) This is also why
   the script can't bring an agent up to a raised minimum version.
3. **Otherwise installs silently:** runs
   `Anchor.Agent-win-Setup.exe --silent --log "<log folder>\intune-setup.log"`
   and waits for it. When `Setup.exe` fails, the script copies the last 20 lines
   of Setup's log into its own log and exits with Setup's exit code.
4. **Starts the agent**, unless it is already running, with the command Windows
   runs at sign-in: the `AnchorFocusAgent` Run value. A silent `Setup.exe`
   doesn't start the agent itself, and the next sign-in can be weeks away on a
   laptop that sleeps instead of restarting. (Since #487 the agent's install
   hook also starts it once `Setup.exe` has exited; whichever start comes second
   finds the agent running and quietly exits.) The start goes through WMI
   (`Win32_Process.Create`), so the agent is not a child of the script: it shares
   no job object and no handles with whatever runs the install command. That is
   meant to keep the Intune Management Extension from waiting on the agent, or
   stopping it along with the install **[TO CONFIRM IN THE PILOT]**. Where WMI
   can't start processes, the script logs a warning and starts the agent as its
   own child instead.
5. **Checks** that the agent is still running 5 seconds later.

### Parameters

In Intune you pass none of them:

| Parameter | Default | Purpose |
| --- | --- | --- |
| `-SetupPath` | `Anchor.Agent-win-Setup.exe` in the script's folder | The installer to run when the agent isn't installed. |
| `-InstallRoot` | `%LOCALAPPDATA%\Anchor.Agent` | The agent's install folder. Only change it to test the script against a stand-in install. |
| `-LogPath` | `%LOCALAPPDATA%\Anchor\FocusAgent\logs\intune-install.log` | The script's log; `Setup.exe` logs to `intune-setup.log` in the same folder. Leave it at the default so the [logs](#logs) are where this guide looks. |
| `-RunKeyPath` | `HKCU:\Software\Microsoft\Windows\CurrentVersion\Run` | The key holding the `AnchorFocusAgent` value. Only change it to test the script against a stand-in install. |

### Exit codes

With the script as the install command, too, keep Intune's default list (0 and
1707 Success, 3010 Soft reboot, 1641 Hard reboot, 1618 Retry) and add none of the
script's codes. Intune counts every code that isn't in the list as **Failed**,
which is right for all of them; none should be **Retry**, because running the
script again doesn't change any of these causes.

| Code | Intune result | Meaning | What to do |
| --- | --- | --- | --- |
| 0 | Success | The agent is installed and running. | Nothing. |
| 1 | Failed | `Setup.exe` failed. Velopack's Setup exits with 1 on any failure; any other code it exits with is passed on unchanged, and Intune maps it by the same list. | Read `intune-setup.log`. Its last 20 lines are also in `intune-install.log`. |
| 10 | Failed | `Anchor.Agent-win-Setup.exe` isn't in the script's folder. | Package both files together ([Step 1](#step-1--package-the-intunewin)). |
| 11 | Failed | `Setup.exe` exited with 0, but `current\FocusAgent.App.exe` doesn't exist. | Read `intune-setup.log`. |
| 12 | Failed | The agent is installed, but there is no `AnchorFocusAgent` Run value to start it with, so it won't start at sign-in either. | The agent's install hook writes that value. Check `intune-setup.log` and the Velopack log, then reinstall: uninstall, and let Intune install it again. |
| 13 | Failed | The script runs as SYSTEM. | Set the app's install behavior to **User**. |
| 14 | Failed | The agent couldn't be started, or stopped within 5 seconds of its start. | Read the agent's own `focusagent-*.log`. The agent is installed, so the detection rule finds it from now on, and it starts at the student's next sign-in. |
| 15 | Failed | Unexpected error. | `intune-install.log` has the message and the stack trace. |

### Trying the script by hand

Run it from a folder holding both files, as the test student, not as an
administrator:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-AnchorAgent.ps1
$LASTEXITCODE
```

The agent's tray icon should appear and the exit code should be 0. Run it a
second time: the log then says the agent is already installed and running, and
the script exits with 0 without running `Setup.exe`.

### Its logs

The script appends to `intune-install.log`, one timestamped line per step. Each
run starts with a `=== Anchor agent install (Intune) ...` line naming the
PowerShell version and bitness, the session and the paths it uses. A `WARN` line
"Started the agent as a child of this script instead" means WMI couldn't start
the agent. The script's logs sit next to the agent's own, not under
`%LOCALAPPDATA%\Anchor.Agent`, on purpose: Velopack's Setup treats a folder there
that isn't empty as an existing install.

## Pilot checklist

To be confirmed on a real Intune-managed laptop in the first pilot. Record what
you find here, and turn the matching **[TO CONFIRM IN THE PILOT]** marks above
into facts.

- [ ] **Intune doesn't wait on the agent.** Intune reports the install as
      succeeded, and the install neither hangs nor runs into the installation
      time limit while the agent keeps running. The agent outlives the install
      command (`Update.exe start` and the install hook start it); whether the
      Intune Management Extension waits on a process left running like that
      isn't recorded here yet.
- [ ] **The agent runs in the student's session.** After the install its tray
      icon is in the student's notification area.
- [ ] **What the student sees at the first start.** Expected from the code: just
      the tray icon. The agent signs in through WAM with the Windows account
      (`WamTokenProvider`, `OperatingSystemAccount`), so an Entra-joined laptop
      should need no sign-in window; if silent sign-in fails, WAM shows its
      sign-in window, and the agent opens its main window on a failed sign-in.
      With the Edge profile in place the agent asks nothing about the extension,
      but it opens its guided-install window if the extension hasn't connected
      within 45 seconds of the start, policy or not, for instance when Edge isn't
      open. Write down what actually appears.
- [ ] **Profile and app order.** What the student sees when the agent starts
      before the Edge profile has arrived.
- [ ] **Detection.** Intune shows the app as installed on a laptop whose agent
      is at the minimum version or newer, including one where the student
      installed it by hand: the rule's `%LOCALAPPDATA%` resolves to the
      student's profile, and the version check reads `FocusAgent.App.exe`'s file
      version.
- [ ] **A raised minimum version.** On a laptop whose agent was older than the
      new minimum: Intune runs the install command at its next check-in, the
      agent is back on its own once `Setup.exe` has finished, and Intune then
      shows the app as installed. Note when the check-in came, and whether the
      student noticed.
- [ ] **Uninstall.** The real `UninstallString` and `QuietUninstallString`, and
      an uninstall from Intune that runs silently in the student's profile: no
      window, the agent stopped, the Run value gone.
- [ ] **Update feed.** A laptop on the school network reaches the feed
      (`github.com` and `*.githubusercontent.com`), and the agent log shows its
      update checks succeeding there, also in the morning when many laptops
      start at once.
- [ ] **Test ring, if you run one.** On an Entra-joined, Intune-enrolled test
      laptop, `edge://policy` shows the test-ring forcelist entry with status OK,
      and `edge://extensions` has the test-ring build
      ([Test ring](#test-ring-the-self-hosted-extension)).
- [ ] **The install script, only if you use it instead.** Intune waits for the
      script only. `intune-install.log` says "Started the agent through WMI",
      not the child-start warning: Defender's attack surface reduction rule
      *Block process creations originating from PSExec and WMI commands* is
      meant to block exactly that; if your tenant turns it on, check whether the
      child start that follows makes the install hang. And no PowerShell
      execution policy set by your tenant blocks the unsigned script.
