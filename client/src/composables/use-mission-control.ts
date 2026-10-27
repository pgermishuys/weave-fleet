import { computed, onUnmounted, shallowRef, watch } from "vue";
import { feedHubFor } from "@/composables/use-signalr-socket";
import { targetFor } from "@/lib/machine-target";
import { buildTiles, groupTiles, needsYou, summarize, type Grouping, type MissionMachine, type Tile } from "@/lib/mission-control";
import { MissionFeed, type MissionSnapshot } from "@/lib/mission-control-feed";
import { nudgeRequest, readRequest } from "@/lib/mission-control-requests";
import { permissionAnswerRequest, questionAnswerRequest, sendAnswer, type AnswerOutcome, type AnswerTarget, type PermissionReply } from "@/lib/push/answer";
import { useMachinesStore, type MachineEntry } from "@/stores/machines";
import { useRelativeTime } from "@/composables/use-relative-time";

interface OpenFeed {
  entry: MachineEntry;
  feed: MissionFeed;
  snapshot: MissionSnapshot | null;
}

/**
 * Mission control across every machine: one live feed for each machine the page knows, open while the screen is on
 * screen. Answers and nudges go straight to the machine the session is on.
 */
export function useMissionControl() {
  const machines = useMachinesStore();
  const clock = useRelativeTime();
  const open = shallowRef<OpenFeed[]>([]);
  const grouping = shallowRef<Grouping>("folder");

  const states = computed<MissionMachine[]>(() => open.value.map(({ entry, snapshot }) => ({
    id: entry.key,
    name: entry.name,
    status: snapshot?.status ?? "connecting",
    sessions: snapshot?.sessions ?? [],
    asks: snapshot?.asks ?? {},
    now: snapshot?.now ?? {},
  })));
  const tiles = computed(() => buildTiles(states.value, clock.value));
  const groups = computed(() => groupTiles(tiles.value, grouping.value));
  const waiting = computed(() => needsYou(tiles.value));
  const summary = computed(() => summarize(tiles.value));

  function sameMachine(a: MachineEntry, b: MachineEntry): boolean {
    return a.key === b.key && a.baseUrl === b.baseUrl && (a.connection?.token ?? null) === (b.connection?.token ?? null);
  }

  function replace(feed: MissionFeed, snapshot: MissionSnapshot): void {
    open.value = open.value.map((candidate) => candidate.feed === feed ? { ...candidate, snapshot } : candidate);
  }

  function openFeed(entry: MachineEntry): OpenFeed {
    const feed: MissionFeed = new MissionFeed({
      target: { machineId: entry.key, baseUrl: entry.baseUrl, token: entry.connection?.token ?? null },
      onChange: (snapshot) => replace(feed, snapshot),
      // The machine's event hub, shared with its sessions open in this page.
      createHub: () => feedHubFor(targetFor(entry.connection)),
    });
    void feed.start();
    return { entry, feed, snapshot: null };
  }

  /** Opens a feed for each machine that has none, and closes those of machines that are gone or changed. */
  function sync(entries: readonly MachineEntry[]): void {
    const kept = open.value.filter((candidate) => entries.some((entry) => sameMachine(entry, candidate.entry)));
    for (const candidate of open.value) if (!kept.includes(candidate)) void candidate.feed.stop();
    open.value = [...kept, ...entries.filter((entry) => !kept.some((candidate) => sameMachine(entry, candidate.entry))).map(openFeed)];
  }

  watch(() => machines.entries, sync, { immediate: true });
  onUnmounted(() => {
    for (const candidate of open.value) void candidate.feed.stop();
    open.value = [];
  });

  function entryOf(machineId: string): MachineEntry | undefined {
    return open.value.find((candidate) => candidate.entry.key === machineId)?.entry;
  }

  /** Where a machine's requests go. */
  function targetOf(machineId: string): AnswerTarget | null {
    const entry = entryOf(machineId);
    return entry ? { baseUrl: entry.baseUrl, token: entry.connection?.token ?? null } : null;
  }

  async function send(tile: Tile, build: (target: AnswerTarget) => Parameters<typeof sendAnswer>[0]): Promise<AnswerOutcome> {
    const target = targetOf(tile.machineId);
    if (!target) return { ok: false, gone: false, error: "That machine isn't in the list any more." };
    const outcome = await sendAnswer(build(target));
    // Read it again at once: the card goes when the machine says the ask is gone.
    void open.value.find((candidate) => candidate.entry.key === tile.machineId)?.feed.refresh();
    return outcome;
  }

  const answerPermission = (tile: Tile, askSessionId: string, requestId: string, reply: PermissionReply) =>
    send(tile, (target) => permissionAnswerRequest(target, askSessionId, requestId, reply));
  const answerQuestion = (tile: Tile, requestId: string, answers: string[][]) =>
    send(tile, (target) => questionAnswerRequest(target, tile.sessionId, requestId, answers));

  /** Whether the harness takes a message into the running turn, as its machine's catalog says. */
  async function steers(target: AnswerTarget, harnessType: string): Promise<boolean> {
    const request = readRequest(target, "/api/harnesses");
    try {
      const response = await fetch(request.url, request.init);
      const harnesses = response.ok ? await response.json() as { type: string; capabilities?: { supportsSteering?: boolean } }[] : [];
      return harnesses.find((harness) => harness.type === harnessType)?.capabilities?.supportsSteering === true;
    } catch {
      return false;
    }
  }

  /** Sends a nudge: into the running turn where the harness steers, else queued for the turn's end. */
  async function nudge(tile: Tile, text: string): Promise<AnswerOutcome & { queued: boolean }> {
    const target = targetOf(tile.machineId);
    if (!target) return { ok: false, gone: false, error: "That machine isn't in the list any more.", queued: false };
    const working = tile.state === "working";
    const steer = working && await steers(target, tile.harnessType);
    const outcome = await send(tile, (to) => nudgeRequest(to, tile.sessionId, text, working, steer));
    return { ...outcome, queued: working && !steer };
  }

  return { grouping, tiles, groups, waiting, summary, states, targetOf, answerPermission, answerQuestion, nudge };
}
