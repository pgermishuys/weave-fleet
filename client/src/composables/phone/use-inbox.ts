import { computed, onMounted, onUnmounted, shallowRef } from "vue";
import { useMachineGrants } from "@/composables/phone/use-machine-grants";
import { useRelativeTime } from "@/composables/use-relative-time";
import { readCredentials, readCredentialsSync, type DeviceCredentials } from "@/lib/device-credentials";
import { fetchMachineList, unreachableReason, type ListedMachine } from "@/lib/phone/grants";
import { buildInbox, type InboxMachineState } from "@/lib/phone/inbox";
import { MachineFeed, type FeedSnapshot } from "@/lib/phone/machine-feed";
import {
  permissionAnswerRequest,
  questionAnswerRequest,
  sendAnswer,
  type AnswerOutcome,
  type AnswerTarget,
  type PermissionReply,
} from "@/lib/push/answer";

/** Feeds close after the app has been off screen this long, and open again when it comes back. */
export const HIDDEN_CLOSE_MS = 5 * 60_000;

interface MachineEntry {
  listed: ListedMachine;
  isHome: boolean;
  target: AnswerTarget | null;
  feed: MachineFeed | null;
  snapshot: FeedSnapshot | null;
  problem: string | null;
}

/**
 * The phone's inbox across every machine: home (same origin) and each machine in home's list the phone holds its own
 * key for. One live feed per machine while the inbox is on screen; answers go straight to the machine that asked.
 */
export function useInbox() {
  const now = useRelativeTime();
  const grants = useMachineGrants();
  const entries = shallowRef<MachineEntry[]>([]);
  const loading = shallowRef(true);
  let hiddenSince: number | null = null;
  let closeTimer: ReturnType<typeof setTimeout> | null = null;

  const machines = computed<InboxMachineState[]>(() => entries.value.map((entry) => ({
    id: entry.listed.id,
    name: entry.listed.name,
    isHome: entry.isHome,
    status: entry.snapshot?.status ?? (entry.problem ? "unreachable" : "connecting"),
    lastHeardAt: entry.snapshot?.lastHeardAt ?? (entry.listed.lastSeenAt ? Date.parse(entry.listed.lastSeenAt) : null),
    sessions: entry.snapshot?.sessions ?? [],
    asks: entry.snapshot?.asks ?? {},
    problem: entry.problem,
  })));

  const inbox = computed(() => buildInbox(machines.value, now.value));

  function homeEntry(credentials: DeviceCredentials | null): MachineEntry {
    return {
      listed: { id: credentials?.homeMachineId ?? "home", name: credentials?.homeMachineName ?? "This machine", baseUrl: "" },
      isHome: true,
      target: { baseUrl: "", token: null },
      feed: null,
      snapshot: null,
      problem: null,
    };
  }

  function replace(entry: MachineEntry, patch: Partial<MachineEntry>): void {
    entries.value = entries.value.map((candidate) => candidate === entry ? Object.assign(entry, patch) : candidate);
  }

  async function open(): Promise<void> {
    loading.value = true;
    const homeToken = readCredentialsSync()?.token ?? null;
    const listed = await fetchMachineList(homeToken).catch(() => [] as ListedMachine[]);
    const credentials = (await grants.ensureGrants(listed)) ?? await readCredentials();

    const others: MachineEntry[] = listed
      .filter((machine) => machine.id !== credentials?.homeMachineId)
      .map((machine) => {
        const grant = credentials?.grants.find((g) => g.machineId === machine.id);
        const blocked = unreachableReason(machine, window.location.protocol) ?? grants.problems.value[machine.id] ?? null;
        return {
          listed: machine,
          isHome: false,
          target: grant ? { baseUrl: grant.baseUrl, token: grant.token } : null,
          feed: null,
          snapshot: null,
          problem: grant ? null : blocked ?? "This phone has no key for it yet.",
        };
      });

    await close();
    entries.value = [homeEntry(credentials), ...others];
    loading.value = false;

    for (const entry of entries.value) {
      if (!entry.target) continue;
      const feed = new MachineFeed({
        target: { machineId: entry.listed.id, baseUrl: entry.target.baseUrl, token: entry.target.token },
        onChange: (snapshot) => replace(entry, { snapshot }),
        onUnauthorized: entry.isHome ? undefined : async () => {
          const token = await grants.renewGrant(entry.listed.id);
          if (token && entry.target) entry.target = { ...entry.target, token };
          return token;
        },
      });
      replace(entry, { feed });
      void feed.start();
    }
  }

  async function close(): Promise<void> {
    await Promise.all(entries.value.map((entry) => entry.feed?.stop()));
  }

  function targetFor(machineId: string): AnswerTarget | null {
    return entries.value.find((entry) => entry.listed.id === machineId)?.target ?? null;
  }

  async function refreshMachine(machineId: string): Promise<void> {
    await entries.value.find((entry) => entry.listed.id === machineId)?.feed?.refresh();
  }

  async function answerPermission(machineId: string, askSessionId: string, requestId: string, reply: PermissionReply, message?: string): Promise<AnswerOutcome> {
    const target = targetFor(machineId);
    if (!target) return { ok: false, gone: false, error: "This phone has no key for that machine." };
    const outcome = await sendAnswer(permissionAnswerRequest(target, askSessionId, requestId, reply, message));
    void refreshMachine(machineId);
    return outcome;
  }

  async function answerQuestion(machineId: string, sessionId: string, requestId: string, answers: string[][]): Promise<AnswerOutcome> {
    const target = targetFor(machineId);
    if (!target) return { ok: false, gone: false, error: "This phone has no key for that machine." };
    const outcome = await sendAnswer(questionAnswerRequest(target, sessionId, requestId, answers));
    void refreshMachine(machineId);
    return outcome;
  }

  function onVisibility(): void {
    if (document.visibilityState === "hidden") {
      hiddenSince = Date.now();
      closeTimer = setTimeout(() => void close(), HIDDEN_CLOSE_MS);
      return;
    }
    if (closeTimer) clearTimeout(closeTimer);
    closeTimer = null;
    const away = hiddenSince === null ? 0 : Date.now() - hiddenSince;
    hiddenSince = null;
    if (away >= HIDDEN_CLOSE_MS) void open();
    else for (const entry of entries.value) void entry.feed?.refresh();
  }

  onMounted(() => {
    void open();
    document.addEventListener("visibilitychange", onVisibility);
  });

  onUnmounted(() => {
    document.removeEventListener("visibilitychange", onVisibility);
    if (closeTimer) clearTimeout(closeTimer);
    void close();
  });

  return { inbox, machines, loading, now, open, answerPermission, answerQuestion, targetFor, refreshMachine };
}
