import { storeToRefs } from "pinia";
import { computed, shallowRef, toValue, watch, type ComputedRef, type MaybeRefOrGetter } from "vue";
import type { SessionListItem } from "@/api/client";
import { apiFetchOn } from "@/lib/api-client";
import { liveTarget, type MachineTarget } from "@/lib/machine-target";
import { HOME_MACHINE_KEY, machineUrl } from "@/lib/machines";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { useMachinesStore, type MachineEntry } from "@/stores/machines";
import { useSessionsStore } from "@/stores/sessions";

/** How long the archived and hidden sessions fetched for the `@` list are reused before they're asked for again. */
const REFRESH_MS = 30_000;
const FETCH_LIMIT = 200;

/** What each machine's `@` lists fetched, by its key. */
const fetchedOn = new Map<string, { sessions: readonly SessionListItem[]; at: number; pending: Promise<void> | null }>();

/** What each session's machine said about the machines it can reach, by its key. */
const reachOf = new Map<string, { view: MachineReach | null; at: number; pending: Promise<void> | null }>();

/** For tests: forgets the sessions fetched for the `@` list. */
export function resetReferableSessions(): void {
  fetchedOn.clear();
  reachOf.clear();
}

/** The machine's sessions for the `@` list, fetched once per {@link REFRESH_MS}; the last list when it doesn't answer. */
async function fetchSessionsOn(target: MachineTarget): Promise<readonly SessionListItem[]> {
  let entry = fetchedOn.get(target.key);
  if (!entry) {
    entry = { sessions: [], at: 0, pending: null };
    fetchedOn.set(target.key, entry);
  }
  if (Date.now() - entry.at < REFRESH_MS) return entry.sessions;

  const fetched = entry;
  fetched.pending ??= (async () => {
    try {
      const { data, response } = await target.api.GET("/api/sessions", {
        params: { query: { retentionStatus: "all", limit: FETCH_LIMIT } },
      });
      if (response.ok && Array.isArray(data)) {
        fetched.sessions = data as unknown as SessionListItem[];
        fetched.at = Date.now();
      }
    } catch {
      // The session list's own sessions are still offered.
    } finally {
      fetched.pending = null;
    }
  })();
  await fetched.pending;
  return fetched.sessions;
}

/**
 * The sessions the composer's `@` list can offer: the ones the session list holds, and, fetched when the list first
 * opens, the archived ones it doesn't. What the session list holds wins, since its status is live. Another machine's
 * (the New Session page's Machine chip, or an open session on another machine) are all fetched from it, since the
 * session list holds only the live machine's. `target` is the machine that looks a picked session up on send.
 */
export function useReferableSessions(
  active: MaybeRefOrGetter<boolean>,
  target: MachineTarget = liveTarget(),
): ComputedRef<readonly SessionListItem[]> {
  const { sessions } = storeToRefs(useSessionsStore());
  const fetched = () => {
    let entry = fetchedOn.get(target.key);
    if (!entry) {
      entry = { sessions: [], at: 0, pending: null };
      fetchedOn.set(target.key, entry);
    }
    return entry;
  };
  const more = shallowRef<readonly SessionListItem[]>(fetched().sessions);

  async function load(): Promise<void> {
    more.value = await fetchSessionsOn(target);
  }

  watch(() => toValue(active), (isActive) => {
    if (isActive) void load();
  }, { immediate: true });

  return computed(() => {
    const listed = target.isLive ? sessions.value : [];
    const known = new Set(listed.map((item) => item.session.id));
    return [...listed, ...more.value.filter((item) => !known.has(item.session.id))];
  });
}

// ── Other machines ────────────────────────────────────────────────────────

/** What a session's machine says about reaching others: who it is, whether it takes such references, and its list. */
interface MachineReach {
  /** Its own id; null from a Fleet too old to say. */
  id: string | null;
  /** It takes a reference to another machine's session (`capabilities.machineReferences`). */
  machineReferences: boolean;
  /** The machines in its list, by id; null when it wouldn't say. */
  listed: ReadonlyMap<string, { status: string; agentWork: string }> | null;
}

/** Asks the session's machine who it is and which machines it lists, once per {@link REFRESH_MS}. */
async function fetchReach(target: MachineTarget): Promise<MachineReach | null> {
  let entry = reachOf.get(target.key);
  if (!entry) {
    entry = { view: null, at: 0, pending: null };
    reachOf.set(target.key, entry);
  }
  if (Date.now() - entry.at < REFRESH_MS) return entry.view;

  const asked = entry;
  asked.pending ??= (async () => {
    try {
      const [identity, list] = await Promise.all([
        apiFetchOn(target.connection, "/api/machine"),
        apiFetchOn(target.connection, "/api/machines").catch(() => null),
      ]);
      const info = identity.ok ? await identity.json() as { id?: string; capabilities?: { machineReferences?: boolean } | null } : null;
      const machines = list?.ok ? (await list.json() as { machines?: { id: string; status?: string; agentWork?: string }[] }).machines : null;
      asked.view = {
        id: info?.id ?? null,
        machineReferences: info?.capabilities?.machineReferences === true,
        listed: Array.isArray(machines)
          ? new Map(machines.map((machine) => [machine.id, { status: machine.status ?? "unknown", agentWork: machine.agentWork ?? "ask" }]))
          : null,
      };
      asked.at = Date.now();
    } catch {
      asked.view = null;
    } finally {
      asked.pending = null;
    }
  })();
  await asked.pending;
  return asked.view;
}

/** A machine besides the session's own, in the `@` list. */
export interface ReferableMachine {
  key: string;
  /** Its own id, which a reference to one of its sessions carries; null when it hasn't said. */
  id: string | null;
  name: string;
  sessions: readonly SessionListItem[];
  /** Why the session's machine can't reach it, so its agent couldn't read these: they're listed, dimmed. Null when it can. */
  unreachable: string | null;
  /** Where to let it: Settings › Machines on the session's machine. */
  fix: { label: string; open: () => void } | null;
}

export interface ReferableMachines {
  /** The session's own machine, whose sessions come first. */
  own: { key: string; name: string };
  /** Every other machine this page lists, in its order; empty with none, and then the `@` list is as it always was. */
  others: readonly ReferableMachine[];
}

/**
 * The other machines' sessions the composer's `@` list can offer, by machine, fetched when it opens; and, asked of
 * the session's machine (`target`), whether it can reach each, since its agent reads a session there through it.
 * With no other machine listed nothing is fetched.
 */
export function useReferableMachines(
  active: MaybeRefOrGetter<boolean>,
  target: MachineTarget = liveTarget(),
): ComputedRef<ReferableMachines> {
  // Asked for only once the list opens and reads it: a composer that never shows the @ list sets up nothing more.
  const store = () => useMachinesStore();
  const { sessions: liveSessions } = storeToRefs(useSessionsStore());
  const { setActiveSection } = useSettingsNav();
  const fetched = shallowRef<Readonly<Record<string, readonly SessionListItem[]>>>({});
  const reach = shallowRef<MachineReach | null | undefined>(reachOf.get(target.key)?.view ?? undefined);

  const others = () => store().entries.filter((entry) => entry.key !== target.key);

  async function load(): Promise<void> {
    const elsewhere = others();
    if (elsewhere.length === 0) return;

    const asked = fetchReach(target).then((view) => {
      reach.value = view;
    });
    await Promise.all([
      asked,
      ...elsewhere.map(async (entry) => {
        const sessions = await fetchSessionsOn(store().targetOfKey(entry.key));
        fetched.value = { ...fetched.value, [entry.key]: sessions };
      }),
    ]);
  }

  watch(() => toValue(active), (isActive) => {
    if (isActive) void load();
  }, { immediate: true });

  const ownEntry = (): MachineEntry | undefined => store().entries.find((entry) => entry.key === target.key);

  function fixFor(ownName: string, name: string): ReferableMachine["fix"] {
    const label = `Let ${ownName} reach ${name}`;
    // Settings › Machines here shows the list of the Fleet that served the page; another machine's is in its own Fleet.
    if (target.key === HOME_MACHINE_KEY) {
      return {
        label,
        open: () => {
          setActiveSection("machines");
          void import("@/router").then(({ router }) => router.navigate({ to: "/settings" }));
        },
      };
    }
    const connection = ownEntry()?.connection ?? target.connection;
    return connection
      ? { label, open: () => window.open(machineUrl(connection, "/settings"), "_blank", "noopener") }
      : null;
  }

  function unreachable(entry: MachineEntry, id: string | null, ownName: string): string | null {
    const view = reach.value;
    if (view === undefined) return `Checking whether ${ownName} can reach ${entry.name}…`;
    if (view === null) return `Fleet couldn't ask ${ownName} which machines it can reach.`;
    if (!view.machineReferences) return `Fleet on ${ownName} is too old to reference another machine's sessions. Update it there.`;
    if (!id) return `Fleet can't tell which machine ${entry.name} is.`;
    if (!view.listed) return `Fleet couldn't ask ${ownName} which machines it can reach.`;
    const listed = view.listed.get(id);
    if (!listed) return `${ownName} can't reach ${entry.name}, so its agent couldn't read these.`;
    if (listed.agentWork === "never") return `Agents on ${ownName} can't use ${entry.name} (it's set to Never there), so they couldn't read these.`;
    if (listed.status === "unreachable" || listed.status === "unauthorized") return `${ownName} can't reach ${entry.name} right now, so its agent couldn't read these.`;
    return null;
  }

  return computed(() => {
    const machines = store();
    const ownName = ownEntry()?.name ?? "This machine";
    const list = others().map((entry): ReferableMachine => {
      const id = entry.isHome ? machines.home?.id ?? null : entry.key;
      // What the page already holds for it wins: its status is live.
      const held = entry.key === machines.liveKey ? liveSessions.value : machines.others[entry.key]?.sessions ?? [];
      const known = new Set(held.map((item) => item.session.id));
      const sessions = [...held, ...(fetched.value[entry.key] ?? []).filter((item) => !known.has(item.session.id))];
      const why = unreachable(entry, id, ownName);
      return {
        key: entry.key,
        id,
        name: entry.name,
        sessions,
        unreachable: why,
        fix: why && reach.value?.machineReferences && id ? fixFor(ownName, entry.name) : null,
      };
    });
    return { own: { key: target.key, name: ownName }, others: list };
  });
}
