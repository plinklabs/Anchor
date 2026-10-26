# Intune rollout — the student agent as a Win32 app

This is the guide for **school IT**: how to push the Anchor agent to school
laptops with Microsoft Intune, and the Edge policy that goes with it. Since
September 2026 most students get the agent this way (about 95% at the school
Anchor was built for). Students on their own laptop with a personal account, the
other ~5%, still run `Setup.exe` themselves; that path is
[unchanged](#manual-install-own-laptops).

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
- The install script itself,
  [`agent/scripts/release-assets/Install-AnchorAgent.ps1`](../agent/scripts/release-assets/Install-AnchorAgent.ps1)
  (#457). `Get-Help .\Install-AnchorAgent.ps1 -Full` prints its help.

## How the pieces fit

| | Intune-managed laptop | Own laptop, personal account |
| --- | --- | --- |
| Installs the agent | Intune: a Win32 app in user context that runs `Install-AnchorAgent.ps1` | The student runs `Setup.exe` |
| Starts it right after the install | The script | `Setup.exe` |
| Edge extension | An Intune configuration profile (`ExtensionInstallForcelist`) | The agent: one UAC prompt, or the guided install (#405) |
| Updates | The agent itself (Velopack) | The agent itself (Velopack) |

Both paths end in the same place: a per-user install in
`%LOCALAPPDATA%\Anchor.Agent`, with no admin rights, started at every sign-in by
the `AnchorFocusAgent` value under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (#225), and kept up to date
from the GitHub Releases of your repository. Because the install is per user,
each student who signs in to a shared laptop has their own copy. An agent that a
student installed by hand is found by the [detection rule](#detection-rule) like
any other, so Intune leaves it alone.

## What the install script does

`Install-AnchorAgent.ps1` is the Win32 app's install command. Every agent release
carries it next to `Anchor.Agent-win-Setup.exe` (releases up to `agent-v0.3.1`
were cut before it existed). Intune runs it in user context, from the folder it
unpacked the `.intunewin` into, with `Setup.exe` beside it. In order, it:

1. **Refuses to run as SYSTEM** (exit code 13). Velopack installs per user, so in
   system context the agent would land in the SYSTEM profile, out of the
   student's reach. The app's install behavior must be **User**.
2. **Leaves an installed agent alone.** When
   `%LOCALAPPDATA%\Anchor.Agent\current\FocusAgent.App.exe` exists, it never runs
   `Setup.exe`: a silent `Setup.exe` over an installed agent stops it, also in the
   middle of a lesson, and doesn't start it again. With the detection rule below
   Intune doesn't run the script in that case anyway; this is the safety net.
3. **Otherwise installs silently:** runs
   `Anchor.Agent-win-Setup.exe --silent --log "<log folder>\intune-setup.log"`
   and waits for it. When `Setup.exe` fails, the script copies the last 20 lines
   of Setup's log into its own log and exits with Setup's exit code.
4. **Starts the agent**, unless it is already running, with the command Windows
   runs at sign-in: the `AnchorFocusAgent` Run value. A silent `Setup.exe`
   doesn't start the agent, and the next sign-in can be weeks away on a laptop
   that sleeps instead of restarting. The start goes through WMI
   (`Win32_Process.Create`), so the agent is not a child of the script: it shares
   no job object and no handles with whatever runs the install command. That is
   meant to keep the Intune Management Extension from waiting on the agent, or
   stopping it along with the install **[TO CONFIRM IN THE PILOT]**. Where WMI
   can't start processes, the script logs a warning and starts the agent as its
   own child instead.
5. **Checks** that the agent is still running 5 seconds later.

Its parameters; in Intune you pass none of them:

| Parameter | Default | Purpose |
| --- | --- | --- |
| `-SetupPath` | `Anchor.Agent-win-Setup.exe` in the script's folder | The installer to run when the agent isn't installed. |
| `-InstallRoot` | `%LOCALAPPDATA%\Anchor.Agent` | The agent's install folder. Only change it to test the script against a stand-in install. |
| `-LogPath` | `%LOCALAPPDATA%\Anchor\FocusAgent\logs\intune-install.log` | The script's log; `Setup.exe` logs to `intune-setup.log` in the same folder. Leave it at the default so the [logs](#logs) are where this guide looks. |
| `-RunKeyPath` | `HKCU:\Software\Microsoft\Windows\CurrentVersion\Run` | The key holding the `AnchorFocusAgent` value. Only change it to test the script against a stand-in install. |

The exit codes and what Intune makes of them are under
[return codes](#return-codes).

## Step 1 — Package the `.intunewin`

1. Download both assets from the **same** release of your repository, into a
   folder that holds nothing else (the packaging tool takes the whole folder):

   ```powershell
   gh release download agent-v<version> --repo <owner>/<repo> `
       --pattern Anchor.Agent-win-Setup.exe --pattern Install-AnchorAgent.ps1 `
       --dir anchor-intune
   ```

   Or download the two files from the release page.
2. Package the folder with the
   [Microsoft Win32 Content Prep Tool](https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool),
   with the script as the setup file:

   ```powershell
   IntuneWinAppUtil.exe -c .\anchor-intune -s Install-AnchorAgent.ps1 -o .\intunewin -q
   ```

   `-c` is the source folder, `-s` the setup file, `-o` the output folder, and
   `-q` overwrites an earlier output without asking. The result is
   `.\intunewin\Install-AnchorAgent.intunewin`.

## Step 2 — Create the Win32 app

In the Intune admin center: **Apps** → **Windows** → **Create** (**Add** in some
consoles) → app type **Windows app (Win32)**, and select the `.intunewin`.

### App information

Name and publisher are free text. **Anchor Focus Agent** matches the name Windows
shows under Installed apps. Don't keep the app's version in step with agent
releases (see [Updates](#updates-leave-them-to-the-agent)).

### Program

| Setting | Value |
| --- | --- |
| Install command | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-AnchorAgent.ps1` |
| Uninstall command | `"%LOCALAPPDATA%\Anchor.Agent\Update.exe" --uninstall --silent` **[TO CONFIRM IN THE PILOT]**, see [Uninstall](#uninstall) |
| Installation time required (mins) | Leave the default (60) |
| Allow available uninstall | No |
| Install behavior | **User** (never System: the script then exits with 13) |
| Device restart behavior | No specific action |
| Return codes | Keep the defaults, see [return codes](#return-codes) |

The script isn't signed. `-ExecutionPolicy Bypass` covers the usual case, but a
PowerShell execution policy that your tenant sets by policy (the MachinePolicy
or UserPolicy scope) wins over the command line.

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
| Detection method | File or folder exists |
| Associated with a 32-bit app on 64-bit clients | No |

No version check. The agent updates itself, so the version on a laptop moves on
without Intune. If a version check is ever added, it must be **Greater than or
equal to**, never **Equals**. An "equals" rule stops matching as soon as the agent
has updated itself, and Intune then treats the agent as missing and runs the
install command again on every cycle. With `Setup.exe` as the install command
that puts the old version back over the updated agent, stopping it on the way;
with the script it skips `Setup.exe`, and Intune reports the app as not detected
after the install, every cycle.

### Return codes

Keep Intune's default list (0 and 1707 Success, 3010 Soft reboot, 1641 Hard
reboot, 1618 Retry) and add none of the script's codes. Intune counts every code
that isn't in the list as **Failed**, which is right for all of them; none should
be **Retry**, because running the script again doesn't change any of these
causes.

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

### Assignments

Assign the app as **Required** to the groups whose laptops should get the agent.
Students on an unmanaged laptop of their own aren't enrolled, so the assignment
doesn't reach them; they install by hand. Under end user notifications, **Hide
all toast notifications** keeps Intune's own install messages out of the
student's way, if you prefer that.

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
app or together with it.

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

- `api.github.com`, where the agent lists the releases;
- `github.com`, which serves the release downloads;
- `*.githubusercontent.com`, where GitHub redirects those downloads.

Edge also needs the Edge Add-ons store (`edge.microsoft.com`, the update URL in
the entry above), as for any store extension.

GitHub allows 60 API requests an hour per public IP address for clients that
don't sign in, and every laptop behind the school's address shares that budget.
Each update check lists the releases through the API, so once more than 60 checks
an hour leave the school network, the ones over the limit fail. A failed check
does no harm: the agent logs "Auto-update check failed; staying on the current
version." and tries again at its next check, which may be at home. How much this
delays a release in practice is **[TO CONFIRM IN THE PILOT]**; #465 tracks
measuring it and moving the check off the API if needed.

## Updates: leave them to the agent

Once the agent is installed, Intune has nothing more to do. The agent checks its
feed at every start, every `Update:CheckInterval` (6 hours of awake time), and
on wake and unlock once the last check is that old. It downloads a new release
and restarts into it as soon as no session is going on (#456); an agent is never
stopped mid-session for an update (#449). So:

- **Don't raise the Intune app's version** for each agent release.
- **Don't use supersedence**, and never **Uninstall previous version**. That runs
  the uninstall command, which stops the agent, mid-lesson or not, and removes it
  before the new app installs it again. Without it, the new app finds the agent
  installed and has nothing to do, so supersedence buys nothing.
- **Don't trigger reinstalls.** The script skips `Setup.exe` over an installed
  agent, but `Setup.exe` itself, run silently over an installed agent, stops it
  and doesn't start it again.

What you can do is upload a newer `.intunewin` (`Setup.exe` and script from a
newer release) to the **same** app, with the same detection rule: in the app's
**Properties** → **App information** → **Edit**, select the new file. Only
laptops without the agent run it; installed agents keep updating themselves.
Doing that now and then means a new laptop starts on a recent version, instead of
installing an old one and downloading the newest release at its first start.

## Uninstall

Velopack registers the agent under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Anchor.Agent`, with
`UninstallString` set to `"<install folder>\Update.exe" --uninstall` and
`QuietUninstallString` to the same command with `--silent`. The install folder is
`%LOCALAPPDATA%\Anchor.Agent`, so the expected uninstall command is:

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
Intune doesn't expand `%LOCALAPPDATA%` in the command, let `cmd.exe` do it:
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

Before you assign the app widely, run the script by hand from a folder holding
both files. Run it as the test student, not as an administrator: it installs the
agent into the profile of whoever runs it.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-AnchorAgent.ps1
$LASTEXITCODE
```

The agent's tray icon should appear and the exit code should be 0. Run it a
second time: the log then says the agent is already installed and running, and
the script exits with 0 without running `Setup.exe`.

## Logs

| What | Where |
| --- | --- |
| The install script | `%LOCALAPPDATA%\Anchor\FocusAgent\logs\intune-install.log` |
| `Setup.exe`, run by the script | `%LOCALAPPDATA%\Anchor\FocusAgent\logs\intune-setup.log` |
| The agent | `%LOCALAPPDATA%\Anchor\FocusAgent\logs\focusagent-*.log` (one per day, kept 14 days) |
| Velopack (installs, updates, uninstall) | `%LOCALAPPDATA%\velopack\velopack_Anchor.Agent.log` |
| Intune Management Extension | `C:\ProgramData\Microsoft\IntuneManagementExtension\Logs` (`IntuneManagementExtension.log`, `AppWorkload.log`) |

The script appends to its log, one timestamped line per step. Each run starts with
a `=== Anchor agent install (Intune) ...` line naming the PowerShell version and
bitness, the session and the paths it uses. A `WARN` line "Started the agent as a
child of this script instead" means WMI couldn't start the agent. The script's
logs sit next to the agent's own, not under `%LOCALAPPDATA%\Anchor.Agent`, on
purpose: Velopack's Setup treats a folder there that isn't empty as an existing
install.

## Pilot checklist

To be confirmed on a real Intune-managed laptop in the first pilot. Record what
you find here, and turn the matching **[TO CONFIRM IN THE PILOT]** marks above
into facts.

- [ ] **Intune waits for the script only.** Intune reports the install as
      succeeded, and the install neither hangs nor runs into the installation
      time limit while the agent keeps running. The WMI start is there for
      this; whether the Intune Management Extension would wait on an agent left
      running by the install command hasn't been checked on a real laptop.
- [ ] **WMI may start processes.** `intune-install.log` says "Started the agent
      through WMI", not the child-start warning. Defender's attack surface
      reduction rule *Block process creations originating from PSExec and WMI
      commands* is meant to block exactly that; if your tenant turns it on,
      check whether the child start that follows makes the install hang.
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
- [ ] **Detection.** Intune shows the app as installed on a laptop with the
      agent, including one where the student installed it by hand: the rule's
      `%LOCALAPPDATA%` resolves to the student's profile.
- [ ] **Uninstall.** The real `UninstallString` and `QuietUninstallString`, and
      an uninstall from Intune that runs silently in the student's profile: no
      window, the agent stopped, the Run value gone.
- [ ] **Update feed.** A laptop on the school network reaches the feed, and the
      agent log shows how often update checks fail there on GitHub's rate limit.
- [ ] **Execution policy.** No PowerShell execution policy set by your tenant
      blocks the unsigned script.
