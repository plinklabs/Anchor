// The extension's side of contracts/witness.json (#573): it takes every message
// the agent and the native host send, as the file shapes them, with every field
// read, and what it sends the agent has exactly the fields the file lists. The
// agent's unit tests hold the agent to the same file, so a change to a message on
// either end fails until both agree.

import { readFileSync } from 'node:fs';
import { describe, it, expect } from 'vitest';
import { runSelfCheck } from './self-check';
import {
  WitnessClient,
  blockedUrlReport,
  parseHostMessage,
  selfCheckReport,
  type RuntimePort,
} from './witness';

type Shape = string | Shape[] | { [field: string]: Shape };
type Messages = Record<string, Record<string, Shape>>;

const contract = JSON.parse(
  readFileSync(new URL('../../../contracts/witness.json', import.meta.url), 'utf8'),
) as { toExtension: Messages; fromHost: Messages; toAgent: Messages };

const SESSION_ID = '3f2c8a8e-5b1d-4c4e-9a65-0d7b5c1f9e21';

/** A value of the shape with every field set, optional ones too, so a field the extension drops shows. */
function sample(shape: Shape, path = ''): unknown {
  if (Array.isArray(shape)) return [sample(shape[0], `${path}[]`)];
  if (typeof shape === 'object') {
    return Object.fromEntries(
      Object.entries(shape).map(([name, field]) => [name, name === 'sessionId' ? SESSION_ID : sample(field, name)]),
    );
  }
  switch (shape.replace(/\?$/, '')) {
    case 'string':
      return `${path || 'value'}-sample`;
    case 'date-time':
      return '2026-11-22T08:30:00.000Z';
    case 'number':
      return 42;
    case 'boolean':
      return true;
    default:
      throw new Error(`The contract has no type ${shape}.`);
  }
}

function message(type: string, fields: Record<string, Shape>): Record<string, unknown> {
  return { type, ...(sample(fields) as Record<string, unknown>) };
}

/**
 * Where a message the extension sent differs from the shape: a field it leaves
 * out that the shape requires (one whose type ends in ? may be left out), one the
 * shape doesn't list, a value of another type.
 */
function mismatches(shape: Shape, sent: unknown, path: string): string[] {
  if (Array.isArray(shape)) {
    return Array.isArray(sent)
      ? sent.flatMap((item, i) => mismatches(shape[0], item, `${path}[${i}]`))
      : [`${path}: sent ${JSON.stringify(sent)} where the contract has a list`];
  }
  if (typeof shape === 'object') {
    if (typeof sent !== 'object' || sent === null) return [`${path}: sent ${JSON.stringify(sent)} where the contract has an object`];
    const fields = sent as Record<string, unknown>;
    return [
      ...Object.entries(shape).flatMap(([name, field]) =>
        name in fields
          ? mismatches(field, fields[name], `${path}.${name}`)
          : typeof field === 'string' && field.endsWith('?')
            ? []
            : [`${path}.${name}: in the contract, but the extension left it out`],
      ),
      ...Object.keys(fields)
        .filter((name) => !(name in shape))
        .map((name) => `${path}.${name}: sent by the extension, but not in the contract`),
    ];
  }
  const type = shape.replace(/\?$/, '');
  const ok =
    type === 'string'
      ? typeof sent === 'string'
      : type === 'date-time'
        ? typeof sent === 'string' && !Number.isNaN(Date.parse(sent))
        : type === 'number'
          ? typeof sent === 'number'
          : type === 'boolean'
            ? typeof sent === 'boolean'
            : false;
  return ok ? [] : [`${path}: sent ${JSON.stringify(sent)}, which isn't a ${shape}`];
}

/** What the extension sent, checked against the message the contract lists under its type. */
function check(sent: unknown): string[] {
  const { type, ...fields } = sent as { type: string };
  const shape = contract.toAgent[type];
  return shape ? mismatches(shape, fields, type) : [`${type}: sent to the agent, but not in the contract`];
}

class FakePort implements RuntimePort {
  posted: unknown[] = [];
  private messageCbs: ((m: unknown) => void)[] = [];
  onMessage = { addListener: (cb: (m: unknown) => void) => void this.messageCbs.push(cb) };
  onDisconnect = { addListener: () => undefined };
  postMessage(m: unknown): void {
    this.posted.push(m);
  }
  disconnect(): void {}
  emit(m: unknown): void {
    this.messageCbs.forEach((cb) => cb(m));
  }
}

describe('the witness contract, extension side (#573)', () => {
  it.each(Object.entries(contract.toExtension))('takes %s from the agent with every field read', (type, fields) => {
    const sent = message(type, fields);
    const signal = parseHostMessage(sent);

    if (type === 'unblock_request_result') {
      // Matched to the request it answers; the rest is the answer.
      expect(signal.kind).toBe('unblock_result');
      if (signal.kind !== 'unblock_result') return;
      expect({ type, requestId: signal.requestId, ...signal.result }).toEqual(sent);
      return;
    }
    expect(signal).toEqual({ kind: 'agent', message: sent });
  });

  it.each(Object.entries(contract.fromHost))('takes %s from the native host', (type, fields) => {
    expect(Object.keys(fields)).toEqual([]);
    expect(parseHostMessage(message(type, fields))).toEqual({ kind: type });
  });

  it('sends the agent every message the contract lists, with exactly its fields', async () => {
    const timers: (() => void)[] = [];
    const port = new FakePort();
    const client = new WitnessClient({
      connect: () => port,
      onAgentMessage: () => undefined,
      setTimeoutFn: (cb) => timers.push(cb),
      clearTimeoutFn: () => undefined,
      newRequestId: () => 'request-1',
    });
    client.start();
    port.emit({ type: 'agent_available' });

    const sends: Record<string, () => Promise<void> | void> = {
      ping: () => {
        // The keepalive timer is the first one armed.
        timers.shift()?.();
      },
      blocked_url: () => {
        client.report(blockedUrlReport(SESSION_ID, 7, 'https://blocked.example/search?q=private'));
      },
      self_check: async () => {
        const result = await runSelfCheck({
          containsAllSites: async () => false,
          isAllowedInPrivate: async () => true,
        });
        client.report(selfCheckReport(SESSION_ID, result));
      },
      link_error: async () => {
        // #561: a link that failed before it came up tells the agent why, once up.
        let calls = 0;
        const recovering = new WitnessClient({
          connect: () => {
            if (calls++ === 0) throw new Error('Specified native messaging host not found.');
            return port;
          },
          onAgentMessage: () => undefined,
          setTimeoutFn: (cb) => timers.push(cb),
          clearTimeoutFn: () => undefined,
        });
        recovering.start();
        recovering.retryNow();
        port.emit({ type: 'agent_available' });
        await new Promise((resolve) => setTimeout(resolve, 0));
        recovering.stop();
      },
      unblock_request: () => {
        void client
          .requestUnblock({ sessionId: SESSION_ID, url: 'https://blocked.example/a', host: 'blocked.example', reason: 'For the assignment.' })
          .catch(() => undefined);
      },
    };
    expect(Object.keys(sends).sort()).toEqual(Object.keys(contract.toAgent).sort());

    for (const [type, send] of Object.entries(sends)) {
      port.posted = [];
      await send();
      const sent = port.posted.filter((m) => (m as { type: string }).type === type);
      expect(sent, `the extension sent no ${type}`).toHaveLength(1);
      expect(check(sent[0])).toEqual([]);
    }
  });

  it('sends a blocked page by its host only, as the contract has it', () => {
    const report = blockedUrlReport(SESSION_ID, 7, 'https://blocked.example/search?q=private', new Date('2026-11-22T08:30:00Z'));
    expect(report).toEqual({
      type: 'blocked_url',
      sessionId: SESSION_ID,
      host: 'blocked.example',
      tabId: 7,
      occurredAt: '2026-11-22T08:30:00.000Z',
    });
  });
});
