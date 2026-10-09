import { onGlobalEvent } from "@/composables/use-signalr-socket";
import type { DomainEvent } from "@/lib/domain-events";
import type { MachineTarget } from "@/lib/machine-target";

/** The event of `T`, with its payload typed. */
export type DomainEventOf<T extends DomainEvent["type"]> = Extract<DomainEvent, { type: T }>;

/**
 * Calls `handler` for each `type` event `machine` pushes on `topic`, with the event narrowed to that type, until the
 * returned function is called. Like `onGlobalEvent`, it reads one machine's hub, so pass the machine the subscriber is
 * about (the live machine for the interface's own code, `useMachineTarget()` for a session's).
 */
export function onDomainEvent<T extends DomainEvent["type"]>(
  machine: MachineTarget,
  topic: string,
  type: T,
  handler: (event: DomainEventOf<T>) => void,
): () => void {
  return onGlobalEvent(machine, topic, (event) => {
    // `event.type === type` can't narrow a union by a generic, so the one cast lives here.
    if (event.type === type) handler(event as DomainEventOf<T>);
  });
}
