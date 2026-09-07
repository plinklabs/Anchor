// Warn before the Edge Add-ons API key expires (#336).
//
// Microsoft shortened Edge Add-ons API key lifetime to 72 days (it used to be
// two years). The key does NOT renew on use, the lifetime cannot be changed,
// and there is no programmatic rotation — the upstream request for one
// (microsoft/MicrosoftEdge-Extensions#272) is still open. So every ~10 weeks a
// human must visit Partner Center, press "Create API credentials", and paste
// the new Client ID + API key into the repo secrets.
//
// Nothing in this repo can automate that. What it CAN do is stop the expiry
// from being discovered by a failed release: the rotation date is recorded in
// the EDGE_ADDONS_KEY_ROTATED repository variable, and this script reports how
// much life the key has left. It runs weekly on a schedule (so the warning
// lands in a stretch with no releases) and again inside the release job (so
// cutting a tag on a nearly-dead key says so up front).
//
// Deliberately advisory: an expired key never *blocks* a release run here. The
// build + ZIP artifact are still worth producing, and a hard failure at this
// step would hide them.

/** Edge Add-ons API keys expire this many days after they are created. */
export const KEY_LIFETIME_DAYS = 72;

/** Warn once the key has this many days or fewer left. */
export const DEFAULT_WARN_WITHIN_DAYS = 14;

const MS_PER_DAY = 24 * 60 * 60 * 1000;

/**
 * Work out how much life the key has left.
 *
 * `rotatedIso` is the YYYY-MM-DD the credentials were last created. Returns a
 * level of:
 *   `unknown` — the variable is unset or unparseable (can't warn, say so)
 *   `expired` — past the 72-day lifetime; a publish will 403
 *   `warn`    — inside the warning window
 *   `ok`      — plenty of life left
 */
export function keyStatus(rotatedIso, now = new Date(), warnWithinDays = DEFAULT_WARN_WITHIN_DAYS) {
  if (!rotatedIso || !/^\d{4}-\d{2}-\d{2}$/.test(rotatedIso.trim())) {
    return {
      level: 'unknown',
      daysLeft: null,
      expiresOn: null,
      message:
        'EDGE_ADDONS_KEY_ROTATED is unset or not a YYYY-MM-DD date, so the Edge ' +
        'Add-ons API key expiry cannot be tracked. Set it to the date the ' +
        'credentials were last created in Partner Center (Publish API page shows ' +
        'the expiry for each key).',
    };
  }

  const rotated = new Date(`${rotatedIso.trim()}T00:00:00Z`);
  if (Number.isNaN(rotated.getTime())) {
    return {
      level: 'unknown',
      daysLeft: null,
      expiresOn: null,
      message: `EDGE_ADDONS_KEY_ROTATED ("${rotatedIso}") is not a valid date.`,
    };
  }

  const expires = new Date(rotated.getTime() + KEY_LIFETIME_DAYS * MS_PER_DAY);
  const expiresOn = expires.toISOString().slice(0, 10);
  // Whole days remaining, rounded down: 0 means it lapses today.
  const daysLeft = Math.floor((expires.getTime() - now.getTime()) / MS_PER_DAY);

  if (daysLeft < 0) {
    return {
      level: 'expired',
      daysLeft,
      expiresOn,
      message:
        `The Edge Add-ons API key expired on ${expiresOn} (${-daysLeft} day(s) ago). ` +
        'A store publish will fail with 403 until it is rotated: Partner Center → ' +
        'Microsoft Edge → Publish API → Create API credentials, then update BOTH ' +
        'EDGE_ADDONS_CLIENT_ID and EDGE_ADDONS_API_KEY (renewing regenerates both) ' +
        'and set EDGE_ADDONS_KEY_ROTATED to today.',
    };
  }

  if (daysLeft <= warnWithinDays) {
    return {
      level: 'warn',
      daysLeft,
      expiresOn,
      message:
        `The Edge Add-ons API key expires in ${daysLeft} day(s), on ${expiresOn}. ` +
        'Rotate it in Partner Center (Publish API → Create API credentials), update ' +
        'BOTH EDGE_ADDONS_CLIENT_ID and EDGE_ADDONS_API_KEY, and set ' +
        'EDGE_ADDONS_KEY_ROTATED to the new date.',
    };
  }

  return {
    level: 'ok',
    daysLeft,
    expiresOn,
    message: `Edge Add-ons API key is good for ${daysLeft} more day(s) (expires ${expiresOn}).`,
  };
}

/** Render a status as the matching GitHub Actions annotation. */
export function formatKeyStatus(status) {
  const title = 'Edge Add-ons API key';
  if (status.level === 'expired') return `::error title=${title}::${status.message}`;
  if (status.level === 'warn' || status.level === 'unknown') {
    return `::warning title=${title}::${status.message}`;
  }
  return status.message;
}

// CLI entry point — used by the scheduled check and the release job. Exits 0
// even when the key is expired: this is a notification, not a gate (see above).
if (process.argv[1] && import.meta.url === `file://${process.argv[1].replace(/\\/g, '/')}`) {
  const status = keyStatus(process.env.KEY_ROTATED ?? '');
  console.log(formatKeyStatus(status));
  if (process.env.GITHUB_OUTPUT) {
    const { appendFileSync } = await import('node:fs');
    appendFileSync(process.env.GITHUB_OUTPUT, `level=${status.level}\n`);
    appendFileSync(process.env.GITHUB_OUTPUT, `days_left=${status.daysLeft ?? ''}\n`);
    appendFileSync(process.env.GITHUB_OUTPUT, `expires_on=${status.expiresOn ?? ''}\n`);
    appendFileSync(process.env.GITHUB_OUTPUT, `message=${status.message}\n`);
  }
}
