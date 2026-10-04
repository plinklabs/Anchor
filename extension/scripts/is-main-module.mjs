// Is this module the one Node was told to run? (#339)
//
// The obvious spelling — comparing `import.meta.url` against
// `` `file://${process.argv[1]}` `` — is wrong on Windows: an absolute path
// there starts with a drive letter, so the hand-built URL has two slashes
// (`file://D:/…`) where Node's own `import.meta.url` has three
// (`file:///D:/…`). The comparison silently fails and the CLI block never runs.
// That's how #336's diagnostics shipped inert on Windows while passing on the
// Linux runners, where the hand-built form happens to match.
//
// `pathToFileURL` is Node's own path→URL conversion, so it agrees with
// `import.meta.url` on every platform, and it handles the cases the naive
// version also gets wrong (spaces, `#`, non-ASCII).

import { pathToFileURL } from 'node:url';

/**
 * True when `metaUrl` (a module's `import.meta.url`) refers to the script Node
 * was invoked with (`process.argv[1]`). Both are passed in so this is testable
 * without spawning a process.
 */
export function isMainModule(metaUrl, entryPath) {
  if (!entryPath) return false;
  try {
    return metaUrl === pathToFileURL(entryPath).href;
  } catch {
    // pathToFileURL throws on input that isn't a usable path — that is simply
    // "not the entry point", never a reason to take down the caller.
    return false;
  }
}
