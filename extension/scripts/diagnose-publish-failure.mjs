// Classify a failed Edge Add-ons publish (#336).
//
// The store rejects a release for two completely different reasons that look
// identical in the log — an opaque HTTP error from wdzeng/edge-addon:
//
//   1. A prior submission is still in review. The store allows ONE in-review
//      submission per product; reviews have taken 2+ weeks. Recovery: wait,
//      then re-run the failed job (the ZIP is already built).
//   2. The credentials are rejected. `EDGE_ADDONS_API_KEY` expires every 72
//      days and Partner Center's "Create API credentials" button regenerates
//      the Client ID *and* the key — updating only the key leaves a mismatched
//      pair that 403s. Recovery: update BOTH secrets, then re-run.
//
// Telling them apart by hand costs a Partner Center login. This does it with
// one safe read: a GET for a well-formed but non-existent operation id. The
// request authenticates before it resolves the operation, so the status code
// separates the cases cleanly:
//
//   401 / 403  → credentials rejected (the store never got as far as our data)
//   404        → credentials accepted, the operation simply doesn't exist
//   2xx        → credentials accepted (shouldn't happen for a bogus id, but a
//                success is still proof that auth worked)
//
// The probe is read-only and idempotent: it creates no submission, uploads
// nothing, and cannot affect the listing.
//
// Run by the release workflow's "Diagnose publish failure" step, which fires
// only when the publish step already failed — this never masks a success.

import { isMainModule } from './is-main-module.mjs';

/** Endpoint root of the Edge Add-ons (Partner Center) REST API. */
export const API_ROOT = 'https://api.addons.microsoftedge.microsoft.com';

/** A well-formed GUID that will never be a real operation id. */
export const PROBE_OPERATION_ID = '00000000-0000-0000-0000-000000000000';

/**
 * Map the probe's HTTP status onto a cause.
 * `credentials` and `store` are definite; `unknown` means the probe itself
 * didn't answer the question, so the caller prints both recoveries rather than
 * guessing (a wrong diagnosis here costs more than an honest "check both").
 */
export function classifyProbeStatus(status) {
  if (status === 401 || status === 403) return 'credentials';
  if (status === 404 || (status >= 200 && status < 300)) return 'store';
  return 'unknown';
}

/** Human-facing diagnosis for a cause, including the exact recovery steps. */
export function describeCause(cause, { version, runId } = {}) {
  const tag = version ? `extension-v${version}` : 'the release tag';
  const rerun = runId ? `gh run rerun ${runId} --failed` : 'gh run rerun <run-id> --failed';

  if (cause === 'credentials') {
    return {
      cause,
      title: 'Edge Add-ons rejected the credentials (not an in-review block).',
      detail:
        'A read-only probe with the configured Client ID + API key was refused, ' +
        'so the publish never reached the submission stage. The API key expires ' +
        'every 72 days, and Partner Center regenerates the Client ID together ' +
        'with the key.',
      recovery: [
        'Partner Center → Microsoft Edge → Publish API → Create API credentials.',
        'Copy BOTH the Client ID and the new API key — renewing regenerates both.',
        'Update the EDGE_ADDONS_CLIENT_ID and EDGE_ADDONS_API_KEY secrets (updating only one leaves a mismatched pair that fails exactly like this).',
        'Set the EDGE_ADDONS_KEY_ROTATED repository variable to today (YYYY-MM-DD) so the expiry warning tracks the new key.',
        `Re-run the failed job — the ${tag} package is already built: ${rerun}`,
      ],
    };
  }

  if (cause === 'store') {
    return {
      cause,
      title: 'Credentials are valid — the store refused the submission itself.',
      detail:
        'The probe authenticated successfully, so this is almost certainly the ' +
        'in-review block: the Edge store allows only one in-review submission ' +
        'per product, and a previous version is still being reviewed. Reviews ' +
        'have taken over two weeks.',
      recovery: [
        'Check the submission status in Partner Center → Microsoft Edge → the Anchor listing.',
        `Once the previous submission clears review, re-run the failed job: ${rerun}`,
        'If newer work has landed in the meantime, bump the version and cut a fresh tag instead — republishing a stale build you would immediately supersede helps nobody.',
      ],
    };
  }

  return {
    cause,
    title: 'Could not determine why the publish failed.',
    detail:
      'The diagnostic probe returned an unexpected status, so both known causes ' +
      'are still on the table. Check the publish step\'s own error above.',
    recovery: [
      'If the log shows 401/403: rotate BOTH EDGE_ADDONS_CLIENT_ID and EDGE_ADDONS_API_KEY in Partner Center → Publish API.',
      'If the log mentions an in-progress submission: wait for review to clear.',
      `Then re-run the failed job: ${rerun}`,
    ],
  };
}

/**
 * Probe the API and return the diagnosis. `fetchImpl` is injected so tests can
 * drive every branch without touching the network.
 */
export async function diagnose({
  productId,
  clientId,
  apiKey,
  version,
  runId,
  fetchImpl = globalThis.fetch,
}) {
  const url = `${API_ROOT}/v1/products/${productId}/submissions/draft/package/operations/${PROBE_OPERATION_ID}`;

  let status;
  try {
    const res = await fetchImpl(url, {
      method: 'GET',
      headers: { Authorization: `ApiKey ${apiKey}`, 'X-ClientID': clientId },
    });
    status = res.status;
  } catch (err) {
    // A transport failure tells us nothing about the credentials — say so
    // rather than blaming a cause we haven't established.
    return {
      ...describeCause('unknown', { version, runId }),
      probeStatus: null,
      probeError: err instanceof Error ? err.message : String(err),
    };
  }

  return { ...describeCause(classifyProbeStatus(status), { version, runId }), probeStatus: status };
}

/** Render a diagnosis as GitHub Actions annotations + a readable log block. */
export function formatDiagnosis(d) {
  const lines = [
    `::error title=Edge Add-ons publish failed::${d.title}`,
    '',
    `Cause: ${d.cause}`,
    `Probe status: ${d.probeStatus ?? `n/a (${d.probeError})`}`,
    '',
    d.detail,
    '',
    'Recovery:',
    ...d.recovery.map((step, i) => `  ${i + 1}. ${step}`),
  ];
  return lines.join('\n');
}

// CLI entry point — used by the release workflow. Always exits 0: the job has
// already failed on the publish step, and a non-zero exit here would replace
// that failure with a confusing second one.
if (isMainModule(import.meta.url, process.argv[1])) {
  const diagnosis = await diagnose({
    productId: process.env.PRODUCT_ID ?? '',
    clientId: process.env.CLIENT_ID ?? '',
    apiKey: process.env.API_KEY ?? '',
    version: process.env.RELEASE_VERSION,
    runId: process.env.RUN_ID,
  });
  console.log(formatDiagnosis(diagnosis));
  if (process.env.GITHUB_OUTPUT) {
    const { appendFileSync } = await import('node:fs');
    appendFileSync(process.env.GITHUB_OUTPUT, `cause=${diagnosis.cause}\n`);
    appendFileSync(process.env.GITHUB_OUTPUT, `title=${diagnosis.title}\n`);
  }
}
