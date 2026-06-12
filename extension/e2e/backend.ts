// REST client the harness uses to drive session lifecycle against the real
// backend, exactly the way the dashboard would — except auth is the dev
// impersonation header (X-Dev-Impersonate-Oid) the backend honours in a
// Development build (Anchor.Api.Auth.DevImpersonationAuthHandler). No token
// acquisition, no UI: the wire contract is the backend, so we talk to it
// directly and let SignalR carry the push to the extension.

import { BACKEND_URL, CLASS_NAME, MS365_BUNDLE_NAME, TEACHER_OID } from './config.ts';

export interface StartedSession {
  id: string;
  joinCode: string;
  classId: string;
}

export class BackendClient {
  // Plain field (not a TS parameter property): the harness's node-run scripts
  // execute this under Node's strip-only TypeScript mode, which rejects
  // parameter properties.
  private readonly baseUrl: string;

  constructor(baseUrl: string = BACKEND_URL) {
    this.baseUrl = baseUrl;
  }

  /** Resolve the seeded class ("3A") to its id, as the seeded teacher. */
  async findClassId(name: string = CLASS_NAME, oid: string = TEACHER_OID): Promise<string> {
    const classes = await this.json<Array<{ id: string; name: string }>>('GET', '/classes', oid);
    const match = classes.find((c) => c.name === name);
    if (!match) {
      throw new Error(
        `Class '${name}' not found via /classes (got: ${classes.map((c) => c.name).join(', ') || '<none>'}). Did the dev seeder run?`,
      );
    }
    return match.id;
  }

  /** Resolve a bundle ("Microsoft 365") to its id. */
  async findBundleId(name: string = MS365_BUNDLE_NAME, oid: string = TEACHER_OID): Promise<string> {
    const bundles = await this.json<Array<{ id: string; name: string }>>('GET', '/bundles', oid);
    const match = bundles.find((b) => b.name === name);
    if (!match) {
      throw new Error(
        `Bundle '${name}' not found via /bundles (got: ${bundles.map((b) => b.name).join(', ') || '<none>'}).`,
      );
    }
    return match.id;
  }

  /** POST /sessions — start a session for a class with the given bundles. */
  async startSession(
    classId: string,
    bundleIds: string[] = [],
    oid: string = TEACHER_OID,
  ): Promise<StartedSession> {
    return this.json<StartedSession>('POST', '/sessions', oid, { classId, bundleIds });
  }

  /** POST /sessions/{id}/end — end a running session. */
  async endSession(sessionId: string, oid: string = TEACHER_OID): Promise<void> {
    await this.send('POST', `/sessions/${sessionId}/end`, oid);
  }

  /**
   * PUT /sessions/{id}/bundles — replace the session's bundle set mid-session.
   * The backend pushes SessionBundlesUpdated to each actively-joined student,
   * which is what the extension re-scans against (#93).
   */
  async updateBundles(
    sessionId: string,
    bundleIds: string[],
    oid: string = TEACHER_OID,
  ): Promise<void> {
    await this.send('PUT', `/sessions/${sessionId}/bundles`, oid, { bundleIds });
  }

  private async json<T>(method: string, path: string, oid: string, body?: unknown): Promise<T> {
    const res = await this.send(method, path, oid, body);
    return (await res.json()) as T;
  }

  private async send(method: string, path: string, oid: string, body?: unknown): Promise<Response> {
    const headers: Record<string, string> = { 'X-Dev-Impersonate-Oid': oid };
    if (body !== undefined) headers['Content-Type'] = 'application/json';

    const res = await fetch(`${this.baseUrl}${path}`, {
      method,
      headers,
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });

    if (res.ok) return res;

    const text = await res.text().catch(() => '');
    const detail = text ? `: ${text}` : '';
    throw new Error(`${method} ${path} → ${res.status} ${res.statusText}${detail}`);
  }
}
