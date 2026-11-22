// Keeps the agent link trying while it is down, even when the MV3 service worker
// has been stopped (#572).
//
// WitnessClient retries a dropped link with a setTimeout backoff (1 s, 2 s, 4 s …
// up to 60 s). Edge stops a worker after about 30 s without events, and a pending
// setTimeout goes with it: once the backoff passed 30 s, the extension stopped
// trying until something else woke the worker (a navigation, a tab update, the
// browser starting). With no browsing, the link stayed down.
//
// An alarm wakes a stopped worker; a setTimeout doesn't. So while the link is
// down a repeating alarm runs next to the backoff: waking the worker runs
// background.js top level again, which opens the link, and an alarm that fires
// in a worker still awake makes it try at once instead of waiting out a long
// backoff. Short delays keep using setTimeout, so a quick reconnect stays quick.
// The alarm is cleared once the host relays a message.

import { logger } from './logger';

const log = logger('link-retry');

/** The alarm that keeps the link retrying while it is down. */
export const LINK_RETRY_ALARM = 'anchor-witness-retry';

/**
 * How often the alarm fires while the link is down: 30 s, the shortest period
 * Chromium honours for a packed extension, and the worker's idle timeout.
 */
export const LINK_RETRY_PERIOD_MINUTES = 0.5;

/** The structural subset of `chrome.alarms` this uses. */
export interface AlarmsApi {
  get(name: string): Promise<unknown>;
  create(name: string, info: { delayInMinutes?: number; periodInMinutes?: number }): Promise<void> | void;
  clear(name: string): Promise<boolean>;
}

/** What WitnessClient calls as the link goes down and comes back. */
export interface LinkRetryAlarm {
  /** The link is down: keep a wake-up alarm running until it is back. */
  arm(): void;
  /** The link is back (or stopped for good): no more wake-ups. */
  disarm(): void;
}

/**
 * Drives the retry alarm over `alarms`. `arm()` leaves a running alarm alone, so
 * a worker woken by it, and failing again, doesn't push the next wake-up back by
 * re-creating it. Calls apply in the order made, so a quick drop-and-reconnect
 * can't leave an alarm armed behind a link that is up.
 */
export function linkRetryAlarm(alarms: AlarmsApi): LinkRetryAlarm {
  let queue: Promise<void> = Promise.resolve();
  const enqueue = (what: string, op: () => Promise<void>): void => {
    queue = queue.then(op).catch((err) => log.warn(`could not ${what} the link retry alarm`, err));
  };
  return {
    arm() {
      enqueue('arm', async () => {
        if (await alarms.get(LINK_RETRY_ALARM)) return;
        await alarms.create(LINK_RETRY_ALARM, {
          delayInMinutes: LINK_RETRY_PERIOD_MINUTES,
          periodInMinutes: LINK_RETRY_PERIOD_MINUTES,
        });
        log.debug('link retry alarm armed');
      });
    },
    disarm() {
      enqueue('clear', async () => {
        if (await alarms.clear(LINK_RETRY_ALARM)) log.debug('link retry alarm cleared');
      });
    },
  };
}
