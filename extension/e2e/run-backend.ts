// Boots the backend for the e2e run. Used as Playwright's `webServer.command`
// (and reused by the dev launcher). Playwright waits for the configured port
// to start listening — Kestrel binds it only after EnsureCreated + the dev
// seeder finish, so "port open" already means "seeded and ready".
//
// Each boot starts from a deleted SQLite file so the schema is rebuilt from
// the current model every time (EnsureCreatedAsync does not migrate — a stale
// e2e DB would silently drift). Playwright tears down the whole process tree
// on exit, so the spawned dotnet host doesn't leak.

import { spawn } from 'node:child_process';
import fs from 'node:fs';
import { BACKEND_PORT, BACKEND_PROJECT, E2E_DB_PATH } from './config.ts';

for (const suffix of ['', '-wal', '-shm']) {
  fs.rmSync(`${E2E_DB_PATH}${suffix}`, { force: true });
}

const child = spawn(
  'dotnet',
  [
    'run',
    '--project',
    BACKEND_PROJECT,
    '--no-launch-profile',
    '--urls',
    `http://127.0.0.1:${BACKEND_PORT}`,
  ],
  {
    stdio: 'inherit',
    shell: process.platform === 'win32', // resolve dotnet.cmd/PATH on Windows
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Development',
      ConnectionStrings__DefaultConnection: `Data Source=${E2E_DB_PATH}`,
      // Keep the piped webServer / CI log readable — the EF command logger is
      // otherwise hundreds of SQL lines per run.
      'Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command': 'Warning',
    },
  },
);

const stop = () => {
  if (!child.killed) child.kill();
};
process.on('SIGINT', stop);
process.on('SIGTERM', stop);
process.on('exit', stop);

child.on('exit', (code) => process.exit(code ?? 0));
