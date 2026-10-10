import { computed, shallowRef, watch, type Ref } from "vue";
import { backgroundWorkId, finishedBackgroundWork, type BackgroundState } from "@/lib/background-work";
import { toRunningWorkItems, type RunningWorkItem } from "@/lib/running-work";
import { messageBody, sameEntries } from "@/lib/activity-messages";
import type { AccumulatedMessage } from "@/lib/client-types";
import type { MachineTarget } from "@/lib/machine-target";

export interface BackgroundWorkOptions {
  sessionId: () => string;
  messages: Readonly<Ref<readonly AccumulatedMessage[]>>;
  /** What the stream says of the session's work: running, and what ended lately. */
  runningWork: Readonly<Ref<readonly RunningWorkItem[]>>;
  machine: MachineTarget;
}

/**
 * How the session's background work stands, for the cards of the calls that started it and for the notices that say it
 * ended. The stream's work is topped up with older work, loaded once when a notice needs it.
 */
export function useBackgroundWork({ sessionId, messages, runningWork, machine }: BackgroundWorkOptions) {
  /**
   * The session's work (`@/lib/running-work`) by the call that started it: what the stream says (running, and what
   * ended lately), and older work, loaded when a notice needs it. A call's card reads its state from here for every
   * harness.
   */
  const olderWork = shallowRef<readonly RunningWorkItem[]>([]);
  const workByCall = computed<ReadonlyMap<string, RunningWorkItem>>((previous) => {
    const byCall = new Map<string, RunningWorkItem>();
    for (const item of [...olderWork.value, ...runningWork.value]) {
      if (item.toolCallId) byCall.set(item.toolCallId, item);
    }
    return previous && sameEntries(previous, byCall) ? previous : byCall;
  });

  /** The call each piece of OpenCode 2's background work came from, by its handle (the shell id, the child session). */
  const backgroundCalls = computed<ReadonlyMap<string, string>>((previous) => {
    const calls = new Map<string, string>();
    for (const message of messages.value) {
      for (const part of message.parts) {
        const handle = part.type === "tool" ? backgroundWorkId(part.state) : null;
        if (handle && part.type === "tool" && part.callId) calls.set(handle, part.callId);
      }
    }
    return previous && sameEntries(previous, calls) ? previous : calls;
  });

  /** How the background work ended by its notices alone, by handle. */
  const backgroundNotices = computed<Map<string, BackgroundState>>((previous) => {
    const next = finishedBackgroundWork(messages.value.map(messageBody));
    return previous && sameEntries(previous, next) ? previous : next;
  });

  /**
   * How work an agent moved into the background ended, by handle. A backgrounded call's own card can't say: OpenCode 2
   * leaves the call finished and running, and only the notice later in the conversation says the work is done. Work
   * Fleet stopped ended stopped, whatever the notice says: OpenCode 2 reports a shell it was told to remove as an error
   * (`Shell.NotFoundError`).
   */
  const finishedBackground = computed<Map<string, BackgroundState>>((previous) => {
    const next = new Map(backgroundNotices.value);
    for (const [handle, callId] of backgroundCalls.value) {
      if (workByCall.value.get(callId)?.endedReason === "cancelled") next.set(handle, "cancelled");
    }
    return previous && sameEntries(previous, next) ? previous : next;
  });

  // Ended work drops out of the stream after a while. A notice that says its work failed is checked against all of the
  // session's work once, so work Fleet stopped still reads stopped when the conversation is opened later.
  let olderWorkLoadedFor: string | null = null;
  watch(
    () => [...backgroundNotices.value].some(([handle, state]) => {
      const callId = backgroundCalls.value.get(handle);
      return state === "error" && callId !== undefined && !workByCall.value.has(callId);
    }),
    async (needed) => {
      const id = sessionId();
      if (!needed || olderWorkLoadedFor === id) return;
      olderWorkLoadedFor = id;
      try {
        const { data, response } = await machine.api.GET("/api/sessions/{id}/work", {
          params: { path: { id }, query: { all: true } },
        });
        if (response.ok && id === sessionId()) olderWork.value = toRunningWorkItems(data);
      } catch (loadError) {
        console.warn(`Failed to load the work of session ${id}:`, loadError);
      }
    },
    { immediate: true },
  );

  return { workByCall, finishedBackground };
}
