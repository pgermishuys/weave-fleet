// React side of the fleet modules: the app-wide manager, foreground/background, and the session stream.
import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import { AppState } from "react-native";
import { applyDomainEvent, createSessionStreamState, type SessionStreamState } from "@fleet/lib/domain-event-reducer";
import type { DomainEvent } from "@fleet/lib/domain-events";
import type { SessionSnapshot } from "@fleet/lib/session-snapshot";
import { FleetManager } from "~/fleet/manager";
import { machines } from "~/fleet/machines";

export const manager = new FleetManager(machines);

/** Stops the sockets while the app is in the background and starts them again when it comes back. */
export function useForegroundConnections() {
  useEffect(() => {
    const subscription = AppState.addEventListener("change", (next) => {
      // "inactive" is iOS's Control Center, app switcher or a permission alert: the app is still in front.
      if (next === "background") manager.setForeground(false);
      else if (next === "active") manager.setForeground(true);
    });
    return () => subscription.remove();
  }, []);
}

export const useFleet = () => useSyncExternalStore(manager.subscribe, manager.snapshot);

/**
 * A session's live conversation: the web client's own reducer (client/src/lib/domain-event-reducer.ts), fed by the
 * machine's hub, with events applied once per frame like the web client does.
 */
export function useSessionStream(machineId: string, sessionId: string, onEvent?: (event: DomainEvent) => void) {
  const [state, setState] = useState<SessionStreamState | null>(null);
  const [title, setTitle] = useState("");
  const pending = useRef<DomainEvent[]>([]);
  const frame = useRef<number | null>(null);
  const onEventRef = useRef(onEvent);
  onEventRef.current = onEvent;

  useEffect(() => {
    const hub = manager.hubFor(machineId);
    if (!hub) return;
    const flush = () => {
      frame.current = null;
      const events = pending.current.splice(0);
      setState((current) => {
        if (!current) return current;
        let next = current;
        for (const event of events) {
          try {
            next = applyDomainEvent(next, event);
          } catch (error) {
            console.warn(`Skipped a ${event.type} event:`, error);
          }
        }
        return next;
      });
    };
    const onSnapshot = (snapshot: SessionSnapshot) => {
      pending.current.length = 0;
      setTitle(snapshot.session.title);
      setState(createSessionStreamState(snapshot));
    };
    const onLive = (event: DomainEvent) => {
      onEventRef.current?.(event);
      pending.current.push(event);
      if (frame.current === null) frame.current = requestAnimationFrame(flush);
    };
    const unsubscribe = hub.subscribeSession(sessionId, onSnapshot, onLive);
    return () => {
      unsubscribe();
      if (frame.current !== null) cancelAnimationFrame(frame.current);
      frame.current = null;
    };
  }, [machineId, sessionId]);

  return { state, title };
}
