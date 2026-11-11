// The student's FocusAgent, as far as the hub can tell (#462). The harness runs
// no agent, so this makes the agent's hub calls itself: a SignalR connection as
// the seeded student, through the same dev impersonation the extension uses,
// that joins the session the way the agent's join prompt does, and takes the
// student out of it the three ways the agent can. What those calls do to the
// extension is what the specs check.

import * as signalR from '@microsoft/signalr';
import { BACKEND_URL, STUDENT_OID } from './config.ts';

/**
 * How the agent takes the student out of a running session:
 * - `leave`: "Leave session" in its main window;
 * - `decline`: the student cancels its join prompt;
 * - `quit`: an older agent quitting from the tray, which reports AgentKilled
 *   (a current one can't quit during a session, #445).
 */
export type AgentExit = 'leave' | 'decline' | 'quit';

export class StudentAgent {
  // Plain field (not a TS parameter property), like BackendClient.
  private readonly connection: signalR.HubConnection;

  private constructor(connection: signalR.HubConnection) {
    this.connection = connection;
  }

  static async connect(oid: string = STUDENT_OID, baseUrl: string = BACKEND_URL): Promise<StudentAgent> {
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`${baseUrl}/hubs/session?dev_impersonate_oid=${encodeURIComponent(oid)}`)
      // It is in the student's user group, so it hears what the extension
      // hears; with no handlers registered the client would warn about each.
      .configureLogging(signalR.LogLevel.None)
      .build();
    await connection.start();
    return new StudentAgent(connection);
  }

  /** The student confirms the agent's join prompt (SessionCoordinator). */
  async join(sessionId: string): Promise<void> {
    await this.connection.invoke('JoinSession', { sessionId, joinCode: null });
  }

  /** Takes the student out of the session, joining it first where the agent
   *  would have: a student can only leave, or quit out of, a session they
   *  joined. */
  async exit(sessionId: string, how: AgentExit): Promise<void> {
    switch (how) {
      case 'leave':
        await this.join(sessionId);
        // SessionCoordinator.LeaveSessionManuallyAsync: report, then leave.
        await this.reportEvent(sessionId, 'ManualLeave');
        await this.connection.invoke('LeaveSession', sessionId);
        return;
      case 'decline':
        await this.connection.invoke('DeclineSession', { sessionId, reason: 'user_cancelled' });
        return;
      case 'quit':
        await this.join(sessionId);
        // SessionCoordinator.ReportAgentKilledAsync, on the way out.
        await this.reportEvent(sessionId, 'AgentKilled');
        return;
    }
  }

  async stop(): Promise<void> {
    await this.connection.stop();
  }

  private async reportEvent(sessionId: string, kind: string): Promise<void> {
    await this.connection.invoke('ReportEvent', {
      sessionId,
      kind,
      payloadJson: '{}',
      occurredAt: new Date().toISOString(),
    });
  }
}
