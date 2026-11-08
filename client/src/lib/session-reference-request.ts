/**
 * A picked `@` session as a request carries it. No Vue here: the phone app bundles `new-session-request.ts`, which
 * uses it.
 */

/** A reference as a request carries it: the machine's id only for a session on another machine. */
export function requestSessionReference(reference: { token: string; sessionId: string; machineId?: string }): {
  token: string;
  sessionId: string;
  machineId?: string;
} {
  const { token, sessionId, machineId } = reference;
  return machineId ? { token, sessionId, machineId } : { token, sessionId };
}
