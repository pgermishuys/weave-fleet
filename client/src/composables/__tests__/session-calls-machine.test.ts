import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h } from "vue";
import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { MACHINE_TARGET, targetFor } from "@/lib/machine-target";
import { setActiveMachine, type MachineConnection } from "@/lib/machines";
import { useMessagePagination } from "@/composables/use-message-pagination";
import { useSendPrompt } from "@/composables/use-send-prompt";
import { useSendCommand } from "@/composables/use-send-command";
import { useRunShellCommand } from "@/composables/use-run-shell-command";
import { useSessionQueue } from "@/composables/use-session-queue";
import { useSessionRetry } from "@/composables/use-session-retry";
import { useSessionPermissions } from "@/composables/use-session-permissions";
import { useQuestionAnswer } from "@/composables/use-question-answer";
import { useSideConversation, _resetSideConversationsForTesting } from "@/composables/use-side-conversation";
import { useRunningWork, _resetRunningWorkForTesting } from "@/composables/use-running-work";
import { useAgents } from "@/composables/use-agents";
import { useModels } from "@/composables/use-models";
import { useSessionContext } from "@/composables/use-session-context";
import { useSendToAgent } from "@/composables/use-send-to-agent";
import { useSessionTitle, _resetSessionLineageForTesting } from "@/composables/use-session-lineage";
import {
  useAbortSession,
  useArchiveSession,
  useDeleteSession,
  useForkSession,
  useMoveSession,
  useRenameSession,
} from "@/composables/use-session-actions";

vi.mock("@/composables/use-weave-socket", () => ({
  useWeaveSocket: () => ({ subscribeV2: () => () => {} }),
  onReconnect: () => () => {},
}));

vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: () => () => {},
  onReconnect: () => () => {},
}));

/**
 * Code working on a session asks the session's machine (`useMachineTarget`), not whichever machine is live. Nothing
 * provides another machine yet, so these mount each session composable under a provided machine and check every
 * request it makes goes there; and without one, to the live machine exactly as before. Telemetry is the interface's
 * own record of what the user did (its Analytics page reads it), so it stays on the live machine.
 */

const falcon: MachineConnection = {
  id: "f0a1c2d3e4f5a6b7c8d9e0f1a2b3c4d5",
  name: "falcon",
  baseUrl: "http://100.64.90.72:2113",
  token: "falcon-token_0123456789-abcdef",
  addedAt: "2026-09-26T00:00:00.000Z",
};

interface SessionCall {
  name: string;
  /** Called in `setup`; what it returns is done once mounted. */
  use: () => (() => unknown) | void;
}

const calls: SessionCall[] = [
  { name: "loads the conversation", use: () => { const { loadInitialMessages } = useMessagePagination(); return () => loadInitialMessages("s1", "i1"); } },
  { name: "sends a prompt", use: () => { const { sendPrompt } = useSendPrompt("s1"); return () => sendPrompt(undefined, "Tidy the README"); } },
  { name: "sends a command", use: () => { const { sendCommand } = useSendCommand("s1"); return () => sendCommand("compact"); } },
  { name: "runs a shell command", use: () => { const { runShellCommand } = useRunShellCommand("s1"); return () => runShellCommand("ls"); } },
  { name: "queues a message", use: () => { const { enqueue } = useSessionQueue("s1"); return () => enqueue("Then the tests", { kind: "prompt" }); } },
  { name: "reads a scheduled retry", use: () => { useSessionRetry("s1"); } },
  { name: "reads permission asks", use: () => { useSessionPermissions("s1"); } },
  { name: "answers a question", use: () => { const { answerQuestion } = useQuestionAnswer("s1"); return () => answerQuestion("q1", [["Yes"]]); } },
  { name: "reads the side conversation", use: () => { useSideConversation("s1"); } },
  { name: "reads and stops running work", use: () => { const work = useRunningWork("s1"); return () => work.stop("w1"); } },
  { name: "lists agents", use: () => { useAgents("s1"); } },
  { name: "lists models", use: () => { useModels("s1"); } },
  { name: "compacts the context", use: () => { const { compact } = useSessionContext("s1"); return () => compact(); } },
  { name: "sends a check to the agent", use: () => { const { send } = useSendToAgent("s1"); return () => send("check", "The build fails"); } },
  { name: "fetches a session title", use: () => { useSessionTitle("s-parent"); } },
  { name: "renames the session", use: () => { const { renameSession } = useRenameSession(); return () => renameSession("s1", "Harbor API docs"); } },
  { name: "stops the session's turn", use: () => { const { abortSession } = useAbortSession(); return () => abortSession("s1"); } },
  { name: "archives the session", use: () => { const { archiveSession } = useArchiveSession(); return () => archiveSession("s1"); } },
  { name: "moves the session", use: () => { const { moveSession } = useMoveSession(); return () => moveSession("s1", "p1"); } },
  { name: "forks the session", use: () => { const { forkSession } = useForkSession(); return () => forkSession("s1"); } },
  { name: "deletes the session", use: () => { const { deleteSession } = useDeleteSession(); return () => deleteSession("s1", "i1"); } },
];

interface SentRequest {
  url: string;
  method: string;
  headers: Headers;
  credentials: RequestCredentials | undefined;
}

let fetchMock: ReturnType<typeof vi.fn>;

function sent(): SentRequest[] {
  return fetchMock.mock.calls.map((call) => {
    const [input, init = {}] = call as [RequestInfo | URL, RequestInit | undefined];
    const headers = new Headers(input instanceof Request ? input.headers : undefined);
    new Headers(init.headers).forEach((value, key) => headers.set(key, value));
    return {
      url: input instanceof Request ? input.url : String(input),
      method: (init.method ?? (input instanceof Request ? input.method : "GET")).toUpperCase(),
      headers,
      credentials: init.credentials,
    };
  });
}

function isTelemetry(request: SentRequest): boolean {
  return new URL(request.url, window.location.href).pathname.startsWith("/api/telemetry/");
}

async function run(call: SessionCall, machine: MachineConnection | null | undefined): Promise<SentRequest[]> {
  let act = undefined as (() => unknown) | void;
  const Harness = defineComponent({
    setup() {
      act = call.use();
      return () => h("div");
    },
  });
  const provide = machine === undefined ? {} : { [MACHINE_TARGET]: () => targetFor(machine) };
  const wrapper = mount(Harness, { global: { provide } });
  await flushPromises();
  try {
    await act?.();
  } catch {
    // Only where the request went matters here.
  }
  await flushPromises();
  wrapper.unmount();
  return sent();
}

describe("session calls go to the session's machine", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    localStorage.clear();
    setActiveMachine(null);
    _resetSideConversationsForTesting();
    _resetRunningWorkForTesting();
    _resetSessionLineageForTesting();
    document.cookie = ".WeaveFleet.CSRF=csrf-1";
    fetchMock = vi.fn(async () => Response.json({}));
    vi.stubGlobal("fetch", fetchMock);
  });

  afterEach(() => {
    setActiveMachine(null);
    vi.unstubAllGlobals();
    document.cookie = ".WeaveFleet.CSRF=; expires=Thu, 01 Jan 1970 00:00:00 GMT";
  });

  it.each(calls)("$name on the machine provided, with its token, while home is live", async (call) => {
    const all = await run(call, falcon);
    const requests = all.filter((request) => !isTelemetry(request));

    expect(requests.length).toBeGreaterThan(0);
    for (const telemetry of all.filter(isTelemetry)) {
      expect(new URL(telemetry.url, window.location.href).origin).toBe(window.location.origin);
    }
    for (const request of requests) {
      expect(request.url.startsWith(`${falcon.baseUrl}/api/`), request.url).toBe(true);
      expect(request.headers.get("Authorization")).toBe(`Bearer ${falcon.token}`);
      expect(request.headers.has("X-CSRF-Token")).toBe(false);
      expect(request.credentials).toBe("omit");
    }
  });

  it.each(calls)("$name on the live machine when none is provided", async (call) => {
    setActiveMachine(falcon);

    const requests = await run(call, undefined);

    expect(requests.length).toBeGreaterThan(0);
    for (const request of requests) {
      expect(request.url.startsWith(`${falcon.baseUrl}/api/`), request.url).toBe(true);
      expect(request.headers.get("Authorization")).toBe(`Bearer ${falcon.token}`);
    }
  });

  it.each(calls)("$name at home as before: same origin, its cookie, and the CSRF token on changes", async (call) => {
    const requests = await run(call, undefined);

    expect(requests.length).toBeGreaterThan(0);
    for (const request of requests) {
      expect(new URL(request.url, window.location.href).origin, request.url).toBe(window.location.origin);
      expect(request.headers.has("Authorization")).toBe(false);
      expect(request.credentials).toBe("include");
      if (request.method !== "GET") expect(request.headers.get("X-CSRF-Token")).toBe("csrf-1");
    }
  });

  it("asks home when home is provided while another machine is live", async () => {
    setActiveMachine(falcon);

    const requests = await run(calls.find((call) => call.name === "sends a check to the agent")!, null);

    expect(requests).toHaveLength(1);
    expect(new URL(requests[0]!.url, window.location.href).origin).toBe(window.location.origin);
    expect(requests[0]!.method).toBe("POST");
    expect(requests[0]!.headers.get("X-CSRF-Token")).toBe("csrf-1");
  });
});
