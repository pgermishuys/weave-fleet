/** What each event an automation can wait for means, in the words the form and page use. */
const EVENT_LABELS: Record<string, string> = {
  session_created: "A session starts",
  session_archived: "A session is archived",
  session_deleted: "A session is deleted",
  "delegation.created": "A sub agent starts",
  "delegation.updated": "A sub agent's status changes",
};

/** An event type in words; one the server no longer sends is marked, since that trigger can't fire. */
export function describeEventType(eventType: string): string {
  return EVENT_LABELS[eventType] ?? `${eventType} (never fires)`;
}

/** The event type an event trigger waits for, from its stored config ({"eventType": …}). */
export function eventTypeOf(triggerConfig: string): string {
  try {
    const parsed = JSON.parse(triggerConfig) as { eventType?: unknown };
    return typeof parsed.eventType === "string" ? parsed.eventType : triggerConfig;
  } catch {
    return triggerConfig;
  }
}

/** The browser's IANA time zone, which new and edited schedules are saved in. */
export function browserTimeZone(): string | null {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || null;
  } catch {
    return null;
  }
}

/** The zone a saved schedule runs in, as the page says it: automations saved without one run in UTC. */
export function scheduleTimeZone(timeZone: string | null | undefined): string {
  return timeZone || "UTC";
}
