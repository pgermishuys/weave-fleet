import { beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { shallowRef, type ShallowRef } from "vue";
import type { AccumulatedMessage, DelegationDto } from "@/lib/client-types";
import type { RunningWorkItem } from "@/lib/running-work";
import type { SentPromptMessage } from "@/composables/use-send-prompt";
import { useThemeStore } from "@/stores/theme";

/**
 * Pins how ActivityStream turns the stream's messages into the views it draws: authors, clustering, optimistic
 * prompts, delegation rows, Improve, background notices and what keeps its identity from one update to the next.
 */
const { stream, sent, builtIn, apiGet } = vi.hoisted(() => ({
  stream: {
    messages: null as unknown as import("vue").ShallowRef<readonly AccumulatedMessage[]>,
    delegations: null as unknown as import("vue").ShallowRef<readonly DelegationDto[]>,
    runningWork: null as unknown as import("vue").ShallowRef<readonly RunningWorkItem[]>,
  },
  sent: { prompts: null as unknown as import("vue").ShallowRef<unknown[]> },
  builtIn: { names: new Set<string>() },
  apiGet: vi.fn(),
}));

vi.mock("@/api/client", () => ({ api: { GET: apiGet } }));

vi.mock("@/composables/use-session-stream", async () => {
  const { computed, shallowRef: ref } = await import("vue");
  return {
    useSessionStream: () => ({
      messages: computed(() => stream.messages.value),
      delegations: computed(() => stream.delegations.value),
      runningWork: computed(() => stream.runningWork.value),
      sessionStatus: computed(() => "idle"),
      isLoading: ref(false),
      hasMore: ref(false),
      isLoadingOlder: ref(false),
      isPartial: ref(false),
      loadOlder: () => undefined,
    }),
  };
});

vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }) }));
vi.mock("@/composables/use-models", async () => {
  const { shallowRef: ref } = await import("vue");
  return { useModels: () => ({ models: ref([{ id: "acme/swift", name: "Swift 2" }]) }) };
});
vi.mock("@/composables/use-send-prompt", () => ({
  useSentPrompts: () => ({ sentPrompts: sent.prompts }),
  useSendPrompt: () => ({ canSend: { value: true }, retryPrompt: vi.fn() }),
  reconcileSentPrompts: vi.fn(),
  clearSentPrompts: vi.fn(),
}));
vi.mock("@/composables/use-server-canvases", () => ({ focusServerCanvas: vi.fn() }));
vi.mock("@/composables/use-agent-browser", () => ({ useAgentBrowser: vi.fn() }));
vi.mock("@/composables/use-session-permissions", async () => {
  const { shallowRef: ref } = await import("vue");
  return { useSessionPermissions: () => ({ asks: ref([]), answer: vi.fn() }) };
});
vi.mock("@/stores/built-in-skills", () => ({
  useBuiltInSkillsStore: () => ({
    ensureLoaded: vi.fn(),
    skills: [],
    isBuiltIn: (name: string) => builtIn.names.has(name),
  }),
}));

function text(id: string, role: AccumulatedMessage["role"], body: string, extra: Partial<AccumulatedMessage> = {}): AccumulatedMessage {
  return {
    messageId: id,
    sessionId: "s1",
    role,
    createdAt: Number(id.slice(1)),
    agent: role === "assistant" ? "build" : undefined,
    parts: [{ partId: `${id}-p`, type: "text", text: body }],
    ...extra,
  };
}

function toolMessage(id: string, tool: string, callId: string, input: Record<string, unknown>, status = "completed"): AccumulatedMessage {
  return {
    messageId: id,
    sessionId: "s1",
    role: "assistant",
    createdAt: Number(id.slice(1)),
    agent: "build",
    parts: [{ partId: `${id}-t`, type: "tool", tool, callId, state: { status, input, output: "done" } }],
  };
}

async function open(after?: string) {
  const { default: ActivityStream } = await import("@/components/session/ActivityStream.vue");
  const wrapper = mount(ActivityStream, {
    props: { sessionId: "s1", after },
    global: { stubs: { ReasoningBlock: true, WorkingIndicator: true, MessageBubble: true, ShellCommandBlock: true, CompactionDivider: true } },
  });
  await flushPromises();
  return wrapper;
}

type Wrapper = Awaited<ReturnType<typeof open>>;

/** What the tests read of a bubble. */
interface Bubble {
  exists(): boolean;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  props(key?: string): any;
}

interface Row {
  id: string;
  classes: string[];
  bubble: Bubble;
}

function rows(wrapper: Wrapper): Row[] {
  return wrapper.findAll("[data-message-id]").map((node) => ({
    id: node.attributes("data-message-id")!,
    classes: node.classes(),
    bubble: node.findComponent({ name: "MessageBubble" }) as unknown as Bubble,
  }));
}

/** The bubble a message shows, by its message id. */
function bubbleOf(wrapper: Wrapper, id: string): Bubble {
  const row = rows(wrapper).find((candidate) => candidate.id === id);
  if (!row?.bubble.exists()) throw new Error(`No bubble for ${id}`);
  return row.bubble;
}

describe("ActivityStream message views", () => {
  beforeAll(async () => {
    await import("@/components/session/ActivityStream.vue");
  }, 30_000);

  beforeEach(() => {
    setActivePinia(createPinia());
    apiGet.mockReset();
    builtIn.names.clear();
    stream.messages = shallowRef([]);
    stream.delegations = shallowRef([]);
    stream.runningWork = shallowRef([]);
    sent.prompts = shallowRef([]);
  });

  it("names the author, the model and the role of each message", async () => {
    stream.messages = shallowRef([
      text("m1", "user", "Hello"),
      text("m2", "assistant", "Hi there", { agent: "code_reviewer", modelID: "acme/swift" }),
      text("m3", "user", "Thanks"),
    ]);
    const wrapper = await open();

    expect(bubbleOf(wrapper, "m1").props()).toMatchObject({ author: "You", role: "user", body: "Hello" });
    expect(bubbleOf(wrapper, "m2").props()).toMatchObject({ author: "Code Reviewer", role: "assistant", modelName: "Swift 2", body: "Hi there" });
    expect(bubbleOf(wrapper, "m3").props("author")).toBe("You");
  });

  it("clusters a run of messages from one sender and keeps another sender's apart", async () => {
    stream.messages = shallowRef([
      text("m1", "user", "Look at this"),
      text("m2", "assistant", "First"),
      text("m3", "assistant", "Second"),
      text("m4", "assistant", "Third"),
      text("m5", "assistant", "Other agent", { agent: "Explore" }),
    ]);
    const wrapper = await open();

    expect(rows(wrapper).map((row) => row.classes.find((name) => /^activity-message--(single|first|middle|last)$/.test(name)))).toEqual([
      "activity-message--single",
      "activity-message--first",
      "activity-message--middle",
      "activity-message--last",
      "activity-message--single",
    ]);
    expect(["m1", "m2", "m3", "m4", "m5"].map((id) => bubbleOf(wrapper, id).props("showIdentity"))).toEqual([true, true, false, false, true]);
    expect(bubbleOf(wrapper, "m3").props("clusterPosition")).toBe("middle");
  });

  it("shows a shell command the user ran as its own block, on the user's side", async () => {
    stream.messages = shallowRef([
      text("m1", "user", "Run it"),
      {
        messageId: "m2",
        sessionId: "s1",
        role: "shell",
        createdAt: 2,
        parts: [{ partId: "m2-t", type: "tool", tool: "shell", callId: "c", state: { status: "completed", input: { command: "ls src" }, output: "a.ts", metadata: { exit: 0 } } }],
      },
    ]);
    const wrapper = await open();

    const shell = rows(wrapper)[1];
    expect(shell.classes).toContain("activity-message--shell");
    expect(shell.bubble.exists()).toBe(false);
    expect(wrapper.findComponent({ name: "ShellCommandBlock" }).props("command")).toMatchObject({ id: "m2-t", command: "ls src", state: "done", exit: 0 });
  });

  it("puts a prompt that was sent but not echoed yet after the messages, and drops it once the stream has it", async () => {
    stream.messages = shallowRef([text("m1", "user", "Earlier"), text("m2", "assistant", "Answer")]);
    const pending = (id: string, body: string, status: SentPromptMessage["status"]) => ({
      id, correlationId: id, status, body, createdAt: 3, images: [], agentId: "build", agentName: "build", modelId: "", modelName: "", effort: "medium",
    });
    sent.prompts = shallowRef([pending("p1", "Do the next thing", "pending"), pending("p2", "Not sure it left", "needs_retry")]);
    const wrapper = await open();

    expect(rows(wrapper).map((row) => row.id)).toEqual(["m1", "m2", "optimistic-p1", "optimistic-p2"]);
    expect(bubbleOf(wrapper, "optimistic-p1").props()).toMatchObject({ author: "You", role: "user", body: "Do the next thing" });
    expect(wrapper.findAll(".optimistic-retry")).toHaveLength(1);

    // The echo of the first arrives under the id the prompt had.
    stream.messages.value = [...stream.messages.value, text("p1", "user", "Do the next thing")];
    await flushPromises();
    expect(rows(wrapper).map((row) => row.id)).toEqual(["m1", "m2", "p1", "optimistic-p2"]);
  });

  it("points a sub-agent call at the session it started, once there is one", async () => {
    stream.messages = shallowRef([toolMessage("m1", "task", "call_a", { subagent_type: "explore", description: "Find the router" })]);
    const wrapper = await open();
    expect(bubbleOf(wrapper, "m1").props("tools")[0].delegation).toBeUndefined();

    stream.delegations.value = [
      { delegationId: "d1", parentToolCallId: "call_a", childSessionId: "child1", title: "Explore", status: "running", childActivityStatus: "waiting_input", background: true },
    ];
    await flushPromises();

    expect(bubbleOf(wrapper, "m1").props("tools")[0].delegation).toEqual({
      href: "/sessions/child1?instanceId=child1&parentSessionId=s1",
      childSessionId: "child1",
      childInstanceId: "child1",
      parentSessionId: "s1",
      agent: "explore",
      task: "Find the router",
      status: "running",
      needsInput: true,
      background: true,
    });
  });

  it("offers Improve on a call that loaded a built-in skill only", async () => {
    builtIn.names.add("fleet-debug");
    stream.messages = shallowRef([
      toolMessage("m1", "skill", "c1", { name: "fleet-debug" }),
      toolMessage("m2", "skill", "c2", { name: "team-notes" }),
      toolMessage("m3", "read", "c3", { filePath: "/tmp/x.ts" }),
    ]);
    const wrapper = await open();

    expect(bubbleOf(wrapper, "m1").props("tools")[0].improvable).toBe(true);
    expect(bubbleOf(wrapper, "m2").props("tools")[0].improvable).toBeUndefined();
    expect(bubbleOf(wrapper, "m3").props("tools")[0].improvable).toBeUndefined();
  });

  it("keeps tool calls and questions apart, in order", async () => {
    stream.messages = shallowRef([{
      messageId: "m1",
      sessionId: "s1",
      role: "assistant" as const,
      createdAt: 1,
      agent: "build",
      parts: [
        { partId: "a", type: "tool" as const, tool: "read", callId: "c1", state: { status: "completed", input: { filePath: "/tmp/a.ts" }, output: "x" } },
        { partId: "b", type: "tool" as const, tool: "grep", callId: "c2", state: { status: "completed", input: { pattern: "foo" }, output: "y" } },
        { partId: "c", type: "tool" as const, tool: "question", callId: "c3", state: { status: "running", input: { questions: [{ question: "Which?", options: [] }] } } },
        { partId: "d", type: "text" as const, text: "Reading." },
      ],
    }]);
    const wrapper = await open();

    const bubble = bubbleOf(wrapper, "m1");
    expect(bubble.props("tools").map((tool: { callId: string }) => tool.callId)).toEqual(["c1", "c2"]);
    expect(bubble.props("questionParts").map((part: { callId: string }) => part.callId)).toEqual(["c3"]);
    expect(bubble.props("body")).toBe("Reading.");
  });

  it("draws a background notice with how the work ended and no bubble text of its own", async () => {
    stream.messages = shallowRef([
      toolMessage("m1", "shell", "call_dev", { command: "sh dev.sh" }, "running"),
      text("m2", "user", '<shell id="sh_dev" state="completed" command="sh dev.sh">\nall quiet\n</shell>'),
    ]);
    const wrapper = await open();

    const note = wrapper.get("[data-testid='background-note']");
    expect(note.classes()).toContain("background-note--completed");
    expect(note.text()).toContain("Background command");
    expect(note.text()).toContain("sh dev.sh");
    const row = rows(wrapper)[1];
    expect(row.classes).toContain("activity-message--user");
  });

  it("hides an assistant message with nothing to show, and keeps an empty user one", async () => {
    stream.messages = shallowRef([
      text("m1", "user", ""),
      text("m2", "assistant", "   "),
      text("m3", "assistant", "Real answer"),
    ]);
    const wrapper = await open();

    expect(rows(wrapper).map((row) => row.id)).toEqual(["m1", "m3"]);
  });

  const thinkingOnly: AccumulatedMessage = {
    messageId: "m2",
    sessionId: "s1",
    role: "assistant",
    createdAt: 2,
    agent: "build",
    parts: [{ partId: "r", type: "reasoning", text: "Weighing options" }],
  };

  it("shows thinking alone as a message", async () => {
    stream.messages = shallowRef([text("m1", "user", "Hmm"), thinkingOnly]);
    useThemeStore().setThinking("folded");
    const wrapper = await open();
    expect(rows(wrapper).map((row) => row.id)).toEqual(["m1", "m2"]);
  });

  it("leaves thinking alone out when thinking is hidden", async () => {
    stream.messages = shallowRef([text("m1", "user", "Hmm"), thinkingOnly]);
    useThemeStore().setThinking("hidden");
    const wrapper = await open();
    expect(rows(wrapper).map((row) => row.id)).toEqual(["m1"]);
  });

  it("shows only what came after the boundary of a side conversation", async () => {
    stream.messages = shallowRef([text("m1", "user", "Copied"), text("m2", "assistant", "Copied too"), text("m3", "user", "Side question"), text("m4", "assistant", "Side answer")]);
    const wrapper = await open("m2");

    expect(rows(wrapper).map((row) => row.id)).toEqual(["m3", "m4"]);
    expect(bubbleOf(wrapper, "m3").props("showIdentity")).toBe(true);
  });

  it("hands an unchanged message's bubble the very same tools and images when others change", async () => {
    const first = toolMessage("m1", "read", "c1", { filePath: "/tmp/a.ts" });
    const second = text("m2", "assistant", "Streaming");
    stream.messages = shallowRef([first, second]) as ShallowRef<readonly AccumulatedMessage[]>;
    const wrapper = await open();
    const toolsBefore = bubbleOf(wrapper, "m1").props("tools");
    const imagesBefore = bubbleOf(wrapper, "m1").props("images");

    stream.messages.value = [first, text("m2", "assistant", "Streaming on")];
    await flushPromises();

    expect(bubbleOf(wrapper, "m1").props("tools")).toBe(toolsBefore);
    expect(bubbleOf(wrapper, "m1").props("images")).toBe(imagesBefore);
    expect(bubbleOf(wrapper, "m2").props("body")).toBe("Streaming on");
  });

  it("builds a message's view again when something its own parts read changes", async () => {
    builtIn.names.add("fleet-debug");
    const skill = toolMessage("m1", "skill", "c1", { name: "fleet-debug" });
    const plain = toolMessage("m2", "read", "c2", { filePath: "/tmp/a.ts" });
    stream.messages = shallowRef([skill, plain]);
    const wrapper = await open();
    const plainTools = bubbleOf(wrapper, "m2").props("tools");

    // A sub-agent's session appears: only a message with a sub-agent call reads the delegations.
    stream.delegations.value = [{ delegationId: "d1", parentToolCallId: "other", childSessionId: "k", title: "T", status: "running" }];
    await flushPromises();

    expect(bubbleOf(wrapper, "m2").props("tools")).toBe(plainTools);
  });
});
