import { defineComponent, h } from "vue";
import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import Composer from "@/components/session/Composer.vue";
import type { ApiClient, SessionListItem } from "@/api/client";
import { useSessionsStore } from "@/stores/sessions";
import { forgetHarnessLists } from "@/composables/use-harnesses";
import { resetReferableSessions } from "@/composables/use-referable-sessions";
import { useDraftState } from "@/composables/use-draft-state";
import { provideMachineTarget, type MachineTarget } from "@/lib/machine-target";

// The open session's composer offers, in its `@` list, the sessions of the machine the session is on, since that's the
// machine that looks a picked one up when the prompt is sent.

vi.mock("@/api/client", () => ({
  api: { GET: vi.fn(), POST: vi.fn(), PUT: vi.fn(), DELETE: vi.fn(), PATCH: vi.fn() },
}));

vi.mock("@/composables/use-signalr-socket", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/composables/use-signalr-socket")>()),
  onGlobalEvent: () => () => {},
  useWeaveSocket: () => ({ subscribeV2: () => () => {} }),
}));

import { api } from "@/api/client";

const liveApi = vi.mocked(api);

type Call = [string, { params?: { path?: { id?: string }; query?: Record<string, unknown> }; body?: unknown }?];

function row(id: string, title: string): SessionListItem {
  return {
    harnessType: "opencode",
    instanceId: `instance-${id}`,
    workspaceId: "workspace-1",
    workspaceDirectory: "/srv/orbit",
    workspaceDisplayName: "orbit",
    isolationStrategy: "existing",
    sessionStatus: "idle",
    session: { id, title, time: { created: 1, updated: 2 }, tags: [] },
    instanceStatus: "running",
    parentSessionId: null,
    sourceDirectory: "/srv/orbit",
    branch: "main",
    activityStatus: null,
    lifecycleStatus: "running",
    retentionStatus: "active",
    archivedAt: null,
    typedInstanceStatus: "running",
    isHidden: false,
    projectId: "project-1",
    projectName: "orbit",
    capabilities: {
      canPrompt: true, canRestart: false, canAbort: false, canArchive: false, canUnarchive: false, canFork: true, canDelete: true,
      promptDisabledReason: null, restartDisabledReason: null, abortDisabledReason: null, archiveDisabledReason: null,
      unarchiveDisabledReason: null, forkDisabledReason: null, deleteDisabledReason: null,
    },
    tags: [],
  };
}

const ok = (data: unknown) => ({ data, error: undefined, response: new Response(null, { status: 200 }) });

/** A machine's API: `/api/sessions` answers `sessions`, a prompt is accepted, anything else isn't there. */
function fakeApi(sessions: SessionListItem[]) {
  const GET = vi.fn(async (url: string) => {
    if (url === "/api/sessions") return ok(sessions);
    if (url === "/api/sessions/{id}/commands") return ok({ commands: [] });
    if (url === "/api/sessions/{id}/models") return ok({ providers: [] });
    if (url === "/api/sessions/{id}/find/files") return ok({ files: [] });
    if (url === "/api/sessions/{id}/queue") return ok([]);
    return { data: undefined, error: undefined, response: new Response(null, { status: 404 }) };
  });
  const POST = vi.fn(async () => ({ data: {}, error: undefined, response: new Response(null, { status: 202 }) }));
  return { GET, POST, PUT: vi.fn(async () => ok({})), DELETE: vi.fn(async () => ok({})), PATCH: vi.fn(async () => ok({})) };
}

const liveSessions = [row("ses-home-docs", "Home docs rewrite"), row("ses-home-cli", "Home CLI flags")];
const falconSessions = [row("ses-falcon-open", "Falcon open session"), row("ses-falcon-cache", "Falcon cache warmup")];

let falconApi: ReturnType<typeof fakeApi>;

function configureLive(): void {
  const live = fakeApi(liveSessions);
  liveApi.GET.mockImplementation(live.GET as never);
  liveApi.POST.mockImplementation(live.POST as never);
  liveApi.PUT.mockImplementation(live.PUT as never);
  liveApi.DELETE.mockImplementation(live.DELETE as never);
  liveApi.PATCH.mockImplementation(live.PATCH as never);
}

function falcon(): MachineTarget {
  return {
    key: "m-falcon",
    connection: { id: "m-falcon", name: "falcon", baseUrl: "http://falcon.example:6262" } as MachineTarget["connection"],
    isLive: false,
    api: falconApi as unknown as ApiClient,
  };
}

/** The session page around the composer provides the open session's machine (`MachineScope` in AppShell). */
function mountComposer(sessionId: string, target: (() => MachineTarget) | null) {
  const Page = defineComponent({
    setup() {
      if (target) provideMachineTarget(target);
      return () => h(Composer, { sessionId, instanceId: `instance-${sessionId}` });
    },
  });
  return mount(Page, {
    attachTo: document.body,
    global: { stubs: { AgentSelector: true, ModelSelector: true } },
  });
}

async function typeMention(wrapper: ReturnType<typeof mountComposer>, text: string) {
  const textarea = wrapper.get("[data-testid='prompt-input']");
  await textarea.setValue(text);
  await new Promise((resolve) => setTimeout(resolve, 100));
  await flushPromises();
  return textarea;
}

function offeredSessions(): string[] {
  return [...document.querySelectorAll(".autocomplete-popup [role='option']")]
    .map((option) => option.textContent ?? "")
    .filter((text) => text.includes("Falcon") || text.includes("Home"));
}

function press(element: Element, key: string): void {
  element.dispatchEvent(new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true }));
}

function promptCalls(machine: { POST: ReturnType<typeof vi.fn> }): Call[] {
  return (machine.POST.mock.calls as unknown as Call[]).filter(([url]) => url === "/api/sessions/{id}/prompt");
}

function sessionListCalls(machine: { GET: ReturnType<typeof vi.fn> }): Call[] {
  return (machine.GET.mock.calls as unknown as Call[]).filter(([url]) => url === "/api/sessions");
}

enableAutoUnmount(afterEach);

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  forgetHarnessLists();
  resetReferableSessions();
  configureLive();
  falconApi = fakeApi(falconSessions);
  const sessions = useSessionsStore();
  sessions.setSessions(liveSessions);
  sessions.upsertElsewhere("m-falcon", falconSessions[0]);
});

describe("Composer @ sessions on another machine", () => {
  it("asks the session's machine for its sessions and offers them, not the live machine's", async () => {
    useDraftState("ses-falcon-open", { agentId: "", modelId: "" }).resetText();
    const wrapper = mountComposer("ses-falcon-open", falcon);
    await flushPromises();

    await typeMention(wrapper, "carry on from @Falcon");

    expect(sessionListCalls(falconApi)).not.toHaveLength(0);
    expect(sessionListCalls(liveApi)).toHaveLength(0);
    const offered = offeredSessions();
    expect(offered.some((text) => text.includes("Falcon cache warmup"))).toBe(true);
    expect(offered.some((text) => text.includes("Home"))).toBe(false);
  });

  it("sends a picked session's id to the session's machine", async () => {
    useDraftState("ses-falcon-open", { agentId: "", modelId: "" }).resetText();
    const wrapper = mountComposer("ses-falcon-open", falcon);
    await flushPromises();

    const textarea = await typeMention(wrapper, "carry on from @cache");
    press(textarea.element, "Enter");
    await flushPromises();
    const text = (textarea.element as HTMLTextAreaElement).value;
    expect(text).toMatch(/^carry on from @\S+ $/);
    press(textarea.element, "Enter");
    await flushPromises();

    expect(promptCalls(liveApi)).toHaveLength(0);
    const [, init] = promptCalls(falconApi)[0] ?? [];
    expect(init?.params?.path?.id).toBe("ses-falcon-open");
    expect((init?.body as { sessionReferences?: { sessionId: string }[] }).sessionReferences)
      .toEqual([expect.objectContaining({ sessionId: "ses-falcon-cache" })]);
  });

  it("offers the live machine's sessions for a session on the live machine, as before", async () => {
    useDraftState("ses-home-docs", { agentId: "", modelId: "" }).resetText();
    const wrapper = mountComposer("ses-home-docs", null);
    await flushPromises();

    await typeMention(wrapper, "see @Home");

    expect(sessionListCalls(falconApi)).toHaveLength(0);
    const offered = offeredSessions();
    expect(offered.some((text) => text.includes("Home CLI flags"))).toBe(true);
    expect(offered.some((text) => text.includes("Falcon"))).toBe(false);
  });
});
