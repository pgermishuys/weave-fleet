// A session's live conversation: the web client's own reducer (client/src/lib/domain-event-reducer.ts), fed by the
// hub, with events applied once per frame like the web client does.
import { useEffect, useRef, useState } from "react";
import { applyDomainEvent, createSessionStreamState, type SessionStreamState } from "@fleet/lib/domain-event-reducer";
import type { DomainEvent } from "@fleet/lib/domain-events";
import type { SessionSnapshot } from "@fleet/lib/session-snapshot";
import { hub } from "~/fleet/hub";

export function useSessionStream(sessionId: string, onEvent?: (event: DomainEvent) => void) {
  const [state, setState] = useState<SessionStreamState | null>(null);
  const [title, setTitle] = useState<string>("");
  const pending = useRef<DomainEvent[]>([]);
  const frame = useRef<number | null>(null);
  const onEventRef = useRef(onEvent);
  onEventRef.current = onEvent;

  useEffect(() => {
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
    };
  }, [sessionId]);

  return { state, title };
}
