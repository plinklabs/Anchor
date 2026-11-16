// The on-box FocusAgent, as far as the extension can tell (#489).
//
// The extension has no identity of its own: the agent hands it the session over
// the witness link and reports what it sends back. The harness runs no agent,
// so this stands in for the agent's end of that link — the named pipe the REAL
// native-messaging host (anchor-witness-host.exe) connects to. Edge launches the
// host with ANCHOR_WITNESS_PIPE in its environment, pointed at this pipe, so the
// whole browser side runs as in a deployment: extension → native messaging →
// host → pipe → here.
//
// Like the agent (ExtensionSessionRelay), it hands the current session (or
// `no_session`) to every connection as it comes up, so a spec can change the
// session while the extension is away and see it caught up on reconnect. The
// agent's own side of the protocol — what it hands down when the backend says
// so, and what it reports — is covered by the agent e2e
// (agent/tests/FocusAgent.IntegrationTests/ExtensionSessionHandDownTests.cs).
//
// Windows-only, like the host: callers gate on witnessHostSupported().

import net from 'node:net';
import { randomUUID } from 'node:crypto';

/** A domain entry as the agent hands it down (the backend's AllowedDomainDto). */
export interface Domain {
  matchType: 'Exact' | 'Wildcard' | 'Suffix';
  value: string;
}

/** The session the fake agent is in, as it hands it down. */
export interface FakeSession {
  sessionId: string;
  classId: string;
  joinCode: string;
  startedAt: string;
  domains: Domain[];
}

/** What the extension sent the agent (its pings and the host's hello included). */
export type ReceivedMessage = { type?: string } & Record<string, unknown>;

/** How the fake agent answers the block page's "Request access". */
export type UnblockAnswer = { ok: true } | { ok: false; error: string } | 'silent';

export class FakeAgent {
  readonly pipeName: string;
  readonly pipePath: string;
  /** Every message the extension sent, in order, across connections. */
  readonly received: ReceivedMessage[] = [];
  /** How many times the host has connected. */
  connections = 0;
  /** The answer to the next unblock requests; ok unless a spec says otherwise. */
  unblockAnswer: UnblockAnswer = { ok: true };

  private readonly server: net.Server;
  private socket: net.Socket | null = null;
  private session: FakeSession | null = null;

  private constructor(pipeName: string) {
    this.pipeName = pipeName;
    this.pipePath = `\\\\.\\pipe\\${pipeName}`;
    this.server = net.createServer((socket) => this.accept(socket));
  }

  /** Listen on a fresh, per-run pipe. */
  static async start(): Promise<FakeAgent> {
    const agent = new FakeAgent(`anchor-witness-e2e-${randomUUID()}`);
    await new Promise<void>((resolve, reject) => {
      agent.server.once('error', reject);
      agent.server.listen(agent.pipePath, () => resolve());
    });
    return agent;
  }

  /** The session the agent is in now, if any. */
  get currentSession(): FakeSession | null {
    return this.session;
  }

  /** True while the host is connected. */
  get connected(): boolean {
    return this.socket !== null;
  }

  /**
   * The student joined a session: hand it down (and to every later connection).
   * Returns its id.
   */
  startSession(domains: Domain[], options: { joinCode?: string; send?: boolean } = {}): string {
    this.session = {
      sessionId: randomUUID(),
      classId: randomUUID(),
      joinCode: options.joinCode ?? 'E2E123',
      startedAt: new Date().toISOString(),
      domains: [...domains],
    };
    if (options.send !== false) this.send({ type: 'session_started', ...this.session });
    return this.session.sessionId;
  }

  /**
   * The session ended or the student left it. With `send: false` the end happens
   * while the extension is away: it only finds out from the next connection's
   * `no_session`.
   */
  endSession(options: { send?: boolean } = {}): void {
    const session = this.session;
    if (!session) throw new Error('The fake agent is in no session.');
    this.session = null;
    if (options.send !== false) this.send({ type: 'session_ended', sessionId: session.sessionId });
  }

  /** The teacher changed the session's bundles: the full new list. */
  replaceAllowlist(domains: Domain[]): void {
    const session = this.requireSession();
    session.domains = [...domains];
    this.send({ type: 'allowlist_replaced', sessionId: session.sessionId, domains });
  }

  /** The teacher approved an unblock request: the domains it adds. */
  amendAllowlist(addedDomains: Domain[]): void {
    const session = this.requireSession();
    session.domains = [...session.domains, ...addedDomains];
    this.send({ type: 'allowlist_amended', sessionId: session.sessionId, addedDomains });
  }

  /** The agent went away (it crashed, or was killed): close the host's pipe. */
  dropConnection(): void {
    this.socket?.destroy();
    this.socket = null;
  }

  /**
   * The agent went away and stays away: stop listening, so the host's retries
   * find nobody, then close the host's pipe.
   */
  async goAway(): Promise<void> {
    // close() stops accepting at once, but calls back only once the open
    // connection is gone too.
    const closed = this.stopListening();
    this.dropConnection();
    await closed;
  }

  /** Send a raw message to the extension. Throws when no host is connected. */
  send(message: Record<string, unknown>): void {
    if (!this.socket) throw new Error('No extension is connected to the fake agent.');
    this.socket.write(JSON.stringify(message) + '\n');
  }

  /** Resolve once the host has connected at least `count` times in all. */
  async waitForConnection(count = 1, timeout = 20_000): Promise<void> {
    await this.poll(() => this.connections >= count && this.socket !== null, timeout, () =>
      `the extension's witness host to connect (connection #${count}; saw ${this.connections})`,
    );
  }

  /** Resolve with the first message (after `from`) the predicate accepts. */
  async waitForMessage(
    predicate: (message: ReceivedMessage) => boolean,
    timeout = 15_000,
    from = 0,
  ): Promise<ReceivedMessage> {
    let found: ReceivedMessage | undefined;
    await this.poll(
      () => (found = this.received.slice(from).find(predicate)) !== undefined,
      timeout,
      () => `a matching message from the extension; got ${JSON.stringify(this.received)}`,
    );
    return found!;
  }

  async close(): Promise<void> {
    this.socket?.destroy();
    this.socket = null;
    await this.stopListening();
  }

  private stopListening(): Promise<void> {
    if (!this.server.listening) return Promise.resolve();
    return new Promise<void>((resolve) => this.server.close(() => resolve()));
  }

  private accept(socket: net.Socket): void {
    // One host at a time, as the agent's pipe allows: a new one replaces the old.
    this.socket?.destroy();
    this.socket = socket;
    this.connections += 1;

    let buffered = '';
    socket.setEncoding('utf8');
    socket.on('data', (chunk: string) => {
      buffered += chunk;
      let newline: number;
      while ((newline = buffered.indexOf('\n')) >= 0) {
        const line = buffered.slice(0, newline).trim();
        buffered = buffered.slice(newline + 1);
        if (line) this.handleLine(line);
      }
    });
    socket.on('close', () => {
      if (this.socket === socket) this.socket = null;
    });
    socket.on('error', () => {
      // The host went away; 'close' follows.
    });

    // What the agent hands down on every connect.
    this.send(this.session ? { type: 'session_started', ...this.session } : { type: 'no_session' });
  }

  private handleLine(line: string): void {
    let message: ReceivedMessage;
    try {
      message = JSON.parse(line) as ReceivedMessage;
    } catch {
      return;
    }
    this.received.push(message);
    if (message.type === 'unblock_request' && typeof message.requestId === 'string') {
      const answer = this.unblockAnswer;
      if (answer === 'silent') return;
      this.send({ type: 'unblock_request_result', requestId: message.requestId, ...answer });
    }
  }

  private requireSession(): FakeSession {
    if (!this.session) throw new Error('The fake agent is in no session.');
    return this.session;
  }

  private async poll(done: () => boolean, timeout: number, what: () => string): Promise<void> {
    const deadline = Date.now() + timeout;
    while (!done()) {
      if (Date.now() >= deadline) throw new Error(`Timed out after ${timeout}ms waiting for ${what()}`);
      await new Promise((r) => setTimeout(r, 50));
    }
  }
}
