import type { SessionListItem } from "@/api/client";
import type { MachineTarget } from "@/lib/machine-target";
import { useMachinesStore } from "@/stores/machines";
import { useSessionsStore } from "@/stores/sessions";

/**
 * The session list a machine's rows come from, for an action that changes a row at once and then tells that machine:
 * the live machine's is the sessions store; another machine's is the last list it returned (`useMachinesStore().others`),
 * read again from it once the action is done.
 */
export interface SessionListOn {
  readonly sessions: readonly SessionListItem[];
  find(sessionId: string): SessionListItem | undefined;
  patch(sessionId: string, patch: Partial<SessionListItem>): void;
  /** Puts the session (and the rest of its workflow run, on the live machine) in another project. */
  patchProject(sessionId: string, projectId: string | null, projectName: string | null): void;
  /** After an action that machine took: another machine's list is read again; the live one keeps its own feed. */
  settled(): void;
}

/** Answers the list of the machine passed to it. Call it in a store's or a component's setup. */
export function useSessionListOn(): (machine: MachineTarget) => SessionListOn {
  const sessionsStore = useSessionsStore();
  const machines = useMachinesStore();

  return (machine) => {
    if (machine.isLive) {
      return {
        get sessions() {
          return sessionsStore.sessions;
        },
        find: (sessionId) => sessionsStore.sessions.find((item) => item.session.id === sessionId),
        patch: (sessionId, patch) => sessionsStore.patchSession(sessionId, patch),
        patchProject: (sessionId, projectId, projectName) => sessionsStore.patchSessionProject(sessionId, projectId, projectName),
        settled: () => undefined,
      };
    }

    const { key } = machine;
    const sessions = () => machines.others[key]?.sessions ?? [];
    return {
      get sessions() {
        return sessions();
      },
      find: (sessionId) => sessions().find((item) => item.session.id === sessionId),
      patch: (sessionId, patch) => machines.patchSession(key, sessionId, patch),
      patchProject: (sessionId, projectId, projectName) => machines.patchSession(key, sessionId, { projectId, projectName }),
      settled: () => void machines.refreshMachine(key),
    };
  };
}
