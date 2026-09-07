// Builds and registers the REAL Anchor witness native-messaging host
// (anchor-witness-host.exe) so the e2e can prove the extension learns its
// backend URL from the on-box agent at runtime (#204) — the same path a real
// deployment uses, not a stub.
//
// Edge finds a native-messaging host via an HKCU registry key pointing at a
// manifest JSON whose `allowed_origins` names the extension id. We build the
// host from the agent solution, write a manifest with the absolute exe path and
// the pinned e2e extension id, set the registry key, and inject the backend URL
// the host should hand down via the host's ANCHOR_WITNESS_BACKEND_URL env var
// (the per-deployment source). connectNative inherits the launching browser's
// environment, so the env var the harness sets is what the host reads.
//
// Windows-only (native messaging + HKCU): callers gate on process.platform.

import { execFileSync } from 'node:child_process';
import os from 'node:os';
import path from 'node:path';
import fs from 'node:fs';
import { REPO_ROOT, STABLE_EXTENSION_ID } from './config.ts';

const HOST_NAME = 'net.anchor.witness';
/** Where Edge looks up the witness host's manifest. */
const HOST_REG_KEY = `HKCU\\Software\\Microsoft\\Edge\\NativeMessagingHosts\\${HOST_NAME}`;
const HOST_PROJECT = path.join(
  REPO_ROOT,
  'agent',
  'src',
  'FocusAgent.WitnessHost',
  'FocusAgent.WitnessHost.csproj',
);

export interface RegisteredWitnessHost {
  /** Absolute path to the manifest written next to the built exe. */
  readonly manifestPath: string;
  /**
   * Put the HKCU key back the way it was found — the *installed* agent's own
   * manifest on a developer box, or no key at all on a machine that had none
   * (#332). Leaves the built exe/manifest in place.
   */
  unregister(): void;
}

/** True only where the real host can be built + registered (Windows + .NET). */
export function witnessHostSupported(): boolean {
  return process.platform === 'win32';
}

/**
 * The manifest path the witness-host key currently points at, or null when no
 * host is registered. On a developer box with the agent installed this is the
 * agent's own manifest under %LOCALAPPDATA%\Anchor.Agent — the value every
 * harness override has to hand back (#332).
 */
export function readWitnessHostRegistration(): string | null {
  if (!witnessHostSupported()) return null;
  try {
    const out = execFileSync('reg', ['query', HOST_REG_KEY, '/ve'], { stdio: 'pipe' }).toString();
    return /\bREG_SZ\s+(.+)/.exec(out)?.[1]?.trim() ?? null;
  } catch {
    // Key absent — nothing registered.
    return null;
  }
}

/**
 * Build the witness host (Debug), write its manifest pinned to the e2e
 * extension id, and register it in HKCU. The browser must be launched AFTER
 * this so Edge picks up the host, and with ANCHOR_WITNESS_BACKEND_URL in its
 * environment so the host hands that URL to the extension.
 */
export function registerWitnessHost(): RegisteredWitnessHost {
  const exePath = buildHost();

  const manifest = {
    name: HOST_NAME,
    description: 'Anchor witness host (e2e #204)',
    path: exePath,
    type: 'stdio',
    allowed_origins: [`chrome-extension://${STABLE_EXTENSION_ID}/`],
  };
  const manifestPath = path.join(path.dirname(exePath), `${HOST_NAME}.json`);
  fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 2), 'utf8');

  const override = overrideWitnessHostKey(manifestPath);
  return { manifestPath, unregister: override.restore };
}

export interface SuppressedWitnessHost {
  /** Put the registry key back the way it was found. */
  restore(): void;
}

/**
 * Temporarily point the witness-host key at a manifest that isn't there, so
 * chrome.runtime.connectNative fails and *no* on-box agent can talk to the
 * extension under test.
 *
 * This matters on a developer machine, where the installed Anchor agent
 * registers this key permanently: its host hands the extension that box's
 * *production* backend URL and auth config, which silently overwrites whatever
 * settings a spec just seeded (and, since the config *changed*, restarts the
 * hub). loadExtension() therefore suppresses the link for every spec that
 * doesn't explicitly ask for a witness host (#332), so each one tests what it
 * thinks it is testing.
 *
 * No-op off Windows, where there is no HKCU to begin with.
 */
export function suppressWitnessHost(): SuppressedWitnessHost {
  if (!witnessHostSupported()) return { restore() {} };

  // A path that deliberately does not exist: Edge fails to open the manifest,
  // connectNative rejects, and WitnessClient takes its usual "no host" path.
  const missing = path.join(os.tmpdir(), 'anchor-e2e-suppressed-witness-host.json');
  return overrideWitnessHostKey(missing);
}

/**
 * Point the witness-host key at `manifestPath` and hand back a restore that puts
 * whatever was there back: the installed agent's own manifest on a developer
 * box, or no key at all on a machine (CI runner) that had none. Every harness
 * path that touches the key goes through here — a run must leave the box the way
 * it found it, or it unregisters the developer's agent from Edge (#332).
 *
 * The restore is idempotent and also runs on process exit, so a run killed
 * mid-spec (a Playwright timeout tearing the worker down before close()) still
 * gives the agent link back.
 */
function overrideWitnessHostKey(manifestPath: string): SuppressedWitnessHost {
  const previous = readWitnessHostRegistration();

  // HKCU\Software\Microsoft\Edge\NativeMessagingHosts\<name> (default) = manifest path.
  execFileSync('reg', ['add', HOST_REG_KEY, '/ve', '/t', 'REG_SZ', '/d', manifestPath, '/f'], {
    stdio: 'pipe',
  });

  let restored = false;
  const restore = (): void => {
    if (restored) return;
    restored = true;
    process.off('exit', restore);
    try {
      if (previous) {
        execFileSync('reg', ['add', HOST_REG_KEY, '/ve', '/t', 'REG_SZ', '/d', previous, '/f'], {
          stdio: 'pipe',
        });
      } else {
        execFileSync('reg', ['delete', HOST_REG_KEY, '/f'], { stdio: 'pipe' });
      }
    } catch {
      // Best-effort: leaving the key pointing at a missing manifest only
      // disables the agent link until the next run (or agent install) sets it.
    }
  };
  process.on('exit', restore);

  return { restore };
}

function buildHost(): string {
  execFileSync(
    'dotnet',
    ['build', HOST_PROJECT, '-c', 'Debug', '--nologo', '-v', 'q'],
    { stdio: 'pipe', shell: process.platform === 'win32' },
  );
  const exePath = path.join(
    path.dirname(HOST_PROJECT),
    'bin',
    'Debug',
    'net10.0',
    'anchor-witness-host.exe',
  );
  if (!fs.existsSync(exePath)) {
    throw new Error(`Witness host exe not found at ${exePath} after build.`);
  }
  return exePath;
}
