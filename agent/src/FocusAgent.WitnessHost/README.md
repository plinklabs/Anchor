# FocusAgent.WitnessHost

`anchor-witness-host.exe` — the Edge **native-messaging host** for Anchor's
agent-as-witness tamper detection (#146 part 1).

## Why it exists

Soft enforcement (design §5.4) makes tampering *visible* rather than preventing
it. #105 shipped the in-browser half: the extension reports what it can witness
while running (InPrivate, site-access downgrade). But the extension **cannot
witness its own disablement or removal** — once it's off, it can't send anything.

The on-box FocusAgent is the witness for that gap. The two components watch each
other over a native-messaging link, with **no enterprise/Edge policy** (BYOD):

```
extension  --connectNative("net.anchor.witness")-->  Edge
   Edge  --launches-->  anchor-witness-host.exe  (this project)
   host  --NamedPipeClient("anchor-witness")-->  FocusAgent  (pipe server)
```

Every Edge profile runs its own copy of the extension, and each copy launches
its own host, so the agent serves **several hosts at once** (#517): one pipe
connection per profile that has Edge open with the extension on. Each connection
has an identity in the agent (`WitnessConnection`), which the transport, the
monitor and the relay use to tell them apart.

- **Extension disabled/removed** → Edge tears down the host process → its stdin
  hits EOF → the host exits → the agent's pipe read returns EOF. That is all the
  pipe says, and it says the same when the extension restarts (an option changed
  on its page, an update, a reload), when Edge is closed and when one profile's
  last window is closed. So a drop alone reports nothing (#518). During a joined
  session the agent (`ExtensionGuard`) reports
  `TamperDetected{kind:"extension_disabled"}`, and pauses Edge (#427), when:
  - **no extension is connected any more** and that lasts past the grace period
    (10 s) with an Edge window on screen. A restart reconnects within it; a
    closed Edge has no window.
  - **another profile's extension is still connected, but a profile that is
    open has the extension switched off or removed.** The pipe can't show which
    profile a dropped connection belonged to, or whether that profile is still
    open, so the agent reads both from Edge (`EdgeOpenProfileExtensionProbe`,
    polled every 5 s during a session):
    - each Edge browser window carries its profile's AppUserModelID
      (`MSEdge.UserData.Profile1`; plain `MSEdge` for the Default profile), so
      the agent knows which profiles have a window open;
    - each profile's `Secure Preferences` (or `Preferences`) has the extension
      under `extensions.settings.<id>`, with a non-empty `disable_reasons` while
      it is switched off and no entry once it is removed.

    A profile that was merely closed has no window left, so it reports nothing.
    Edge writes the preferences 10 s after a change (measured on Edge 157), which
    is this case's grace period: a restart never reaches the file.

  One outage is reported once, however often the student opens Edge to repair it.

  Checked on Edge 157 (2026-11): closing a profile's last window while another
  profile stays open ends that profile's extension within about 2 s; a profile
  can also keep its extension (and host) running with no window at all, which is
  why counting connections against windows is not enough.
- **Agent died** → the host's pipe drops → the host sends `agent_unavailable`
  to the extension, which keeps enforcing the session it has until the agent is
  back and hands it the current one. The teacher sees the agent go through its
  own heartbeat.

Since #489 the link also carries the **session**: the agent is the extension's
only identity. It hands the joined session down (and every allowlist change, and
the session's end), and the extension hands back what it reports — blocked URLs,
unblock requests, its in-browser tamper signals — which the agent sends with its
own backend connection. The extension has no sign-in and no hub connection.

The host itself is a thin bridge: browser **stdio** ↔ agent **named pipe**. The
decision logic lives in the unit-tested `ExtensionWitnessMonitor` and
`ExtensionSessionRelay` (FocusAgent.Core) and `witness.ts` (extension); only the
framing (`NativeMessaging`) and bridge wiring (`WitnessBridge`) live here, both
unit-tested with `MemoryStream`s.

## Wire protocol

- **Browser ↔ host (stdio):** Chrome/Edge native messaging — a 4-byte
  little-endian length prefix + UTF-8 JSON per message. The host forwards every
  extension message to the agent, forwards every agent message to the extension,
  and emits `{"type":"agent_available"}` / `{"type":"agent_unavailable"}` itself.
  It also hands down `backend_url` / `auth_config` on start, which only an
  extension from before #489 reads.
- **Host ↔ agent (named pipe `anchor-witness`):** newline-delimited JSON, both
  ways. The host adds its own `hello` and `ping` keepalives; the pipe's liveness
  is still the witness (connect = up, EOF = the extension is gone).
- **The messages** (`FocusAgent.Core.Extension.ExtensionMessages`, mirrored in
  `witness.ts`): agent → extension `session_started`, `no_session`,
  `allowlist_replaced`, `allowlist_amended`, `session_ended`,
  `unblock_request_result`; extension → agent `ping`, `blocked_url`,
  `unblock_request`, `tamper`. The agent sends the current session (or
  `no_session`) to each connection when it connects, sends every change to all
  of them, and answers an `unblock_request` on the connection that asked.

Names are shared via `FocusAgent.Core.Tamper.WitnessLink`; the extension repeats
the host name in `witness.ts`, and the host manifest's `allowed_origins` pins the
extension ID (`dnkimhodjfogjibnbbfdjdapgmmiojio`, see `extension/README.md`), plus
the self-hosted test-ring build's once `extension/test-ring.json` names it (#497,
`AnchorExtensionIds`).

## Dev registration (no admin)

```powershell
pwsh scripts/dev/register-witness-host.ps1      # build + write HKCU key + manifest
pwsh scripts/dev/register-witness-host.ps1 -Unregister
```

This writes `HKCU\Software\Microsoft\Edge\NativeMessagingHosts\net.anchor.witness`
pointing at a manifest (generated from `net.anchor.witness.template.json` with the
absolute exe path filled in). Restart Edge afterwards. Production installs do the
equivalent from the agent installer.
