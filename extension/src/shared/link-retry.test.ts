import { describe, it, expect, vi } from 'vitest';
import { LINK_RETRY_ALARM, LINK_RETRY_PERIOD_MINUTES, linkRetryAlarm, type AlarmsApi } from './link-retry';

/** chrome.alarms, in memory. */
function fakeAlarms() {
  const alarms = new Map<string, { delayInMinutes?: number; periodInMinutes?: number }>();
  const api = {
    get: vi.fn(async (name: string) => (alarms.has(name) ? { name, ...alarms.get(name) } : undefined)),
    create: vi.fn(async (name: string, info: { delayInMinutes?: number; periodInMinutes?: number }) => {
      alarms.set(name, info);
    }),
    clear: vi.fn(async (name: string) => alarms.delete(name)),
  } satisfies AlarmsApi;
  return { alarms, api };
}

const settle = () => new Promise((r) => setTimeout(r, 0));

describe('linkRetryAlarm (#572)', () => {
  it('arms a repeating 30 s alarm', async () => {
    const { alarms, api } = fakeAlarms();
    linkRetryAlarm(api).arm();
    await settle();
    expect(LINK_RETRY_PERIOD_MINUTES).toBe(0.5);
    expect(alarms.get(LINK_RETRY_ALARM)).toEqual({ delayInMinutes: 0.5, periodInMinutes: 0.5 });
  });

  it('leaves a running alarm alone, so failing again does not push the next wake-up back', async () => {
    const { api } = fakeAlarms();
    // A worker the alarm woke builds a new client, which arms again.
    linkRetryAlarm(api).arm();
    await settle();
    linkRetryAlarm(api).arm();
    await settle();
    expect(api.create).toHaveBeenCalledTimes(1);
  });

  it('clears the alarm on disarm', async () => {
    const { alarms, api } = fakeAlarms();
    const retry = linkRetryAlarm(api);
    retry.arm();
    await settle();
    retry.disarm();
    await settle();
    expect(alarms.has(LINK_RETRY_ALARM)).toBe(false);
  });

  it('applies calls in order: a quick drop and reconnect leaves no alarm behind', async () => {
    const { alarms, api } = fakeAlarms();
    const retry = linkRetryAlarm(api);
    retry.arm();
    retry.disarm();
    await settle();
    expect(alarms.has(LINK_RETRY_ALARM)).toBe(false);
  });

  it('keeps working after a call to chrome.alarms fails', async () => {
    const { alarms, api } = fakeAlarms();
    api.get.mockRejectedValueOnce(new Error('boom'));
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const retry = linkRetryAlarm(api);
    retry.arm();
    retry.arm();
    await settle();
    expect(alarms.has(LINK_RETRY_ALARM)).toBe(true);
    warn.mockRestore();
  });
});
