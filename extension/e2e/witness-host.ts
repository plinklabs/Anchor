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
import path from 'node:path';
import fs from 'node:fs';
import { REPO_ROOT, STABLE_EXTENSION_ID } from './config.ts';

const HOST_NAME = 'net.anchor.witness';
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
  /** Remove the HKCU key (leaves the built exe/manifest in place). */
  unregister(): void;
}

/** True only where the real host can be built + registered (Windows + .NET). */
export function witnessHostSupported(): boolean {
  return process.platform === 'win32';
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

  // HKCU\Software\Microsoft\Edge\NativeMessagingHosts\<name> (default) = manifest path.
  const regKey = `HKCU\\Software\\Microsoft\\Edge\\NativeMessagingHosts\\${HOST_NAME}`;
  execFileSync('reg', ['add', regKey, '/ve', '/t', 'REG_SZ', '/d', manifestPath, '/f'], {
    stdio: 'pipe',
  });

  return {
    manifestPath,
    unregister() {
      try {
        execFileSync('reg', ['delete', regKey, '/f'], { stdio: 'pipe' });
      } catch {
        // Best-effort cleanup — a leaked key only points at a stale manifest
        // path and is harmless to a later run that overwrites it.
      }
    },
  };
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
