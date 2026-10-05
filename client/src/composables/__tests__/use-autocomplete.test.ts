import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { shallowRef, type Ref } from "vue";
import type { SessionListItem } from "@/api/client";
import { useAutocomplete } from "@/composables/use-autocomplete";
import { resetSessionReferences, sessionReferencesIn } from "@/lib/session-references";
import { pickedFileTokens, resetFileReferences } from "@/lib/composer-references";
import { flushAll, mountComposable } from "./test-utils";

const { mockApi } = vi.hoisted(() => ({
  mockApi: {
    GET: vi.fn(),
    POST: vi.fn(),
    PUT: vi.fn(),
    DELETE: vi.fn(),
    PATCH: vi.fn(),
  },
}));

vi.mock("@/api/client", () => ({
  api: mockApi,
}));

const apiFetchMock = vi.fn();
vi.mock("@/lib/api-client", () => ({
  apiFetch: apiFetchMock,
}));

const { globalHandlers } = vi.hoisted(() => ({ globalHandlers: new Set<(event: unknown) => void>() }));
vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: (_topic: string, handler: (event: unknown) => void) => {
    globalHandlers.add(handler);
    return () => globalHandlers.delete(handler);
  },
}));

function pushCatalogChange(sessionIds: string[]): void {
  const event = {
    type: "harness.catalog_changed",
    payload: { harnessType: "opencode2", directory: "/work/rocket", profileIds: ["none"], sessionIds },
  };
  for (const handler of [...globalHandlers]) handler(event);
}

function createJsonResponse<T>(body: T, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function createKeyboardEvent(key: string): KeyboardEvent {
  return new KeyboardEvent("keydown", {
    key,
    bubbles: true,
    cancelable: true,
  });
}

function configureApiFetch(): void {
  // Mock api.GET for commands and agents
  mockApi.GET.mockImplementation(async (url: string) => {
    if (url === "/api/sessions/{id}/commands") {
      return {
        data: {
          commands: [
            { name: "help", description: "Show help" },
            { name: "hello", description: "Say hello" },
            { name: "status", description: "Show status" },
          ],
        },
        error: undefined,
        response: createJsonResponse({
          commands: [
            { name: "help", description: "Show help" },
            { name: "hello", description: "Say hello" },
            { name: "status", description: "Show status" },
          ],
        }),
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any;
    }

    if (url === "/api/sessions/{id}/agents") {
      return {
        data: [
          { name: "alpha", description: "Planner", mode: "primary", color: "#ff00aa" },
          { name: "beta", description: "Reviewer", mode: "secondary", color: "#00aaff" },
        ],
        error: undefined,
        response: createJsonResponse([
          { name: "alpha", description: "Planner", mode: "primary", color: "#ff00aa" },
          { name: "beta", description: "Reviewer", mode: "secondary", color: "#00aaff" },
        ]),
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any;
    }

    if (url === "/api/sessions/{id}/find/files") {
      return {
        data: {
          sessionId: "instance-1",
          files: ["src/alpha.ts", "src/components/"],
        },
        error: undefined,
        response: createJsonResponse({
          sessionId: "instance-1",
          files: ["src/alpha.ts", "src/components/"],
        }),
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any;
    }

    throw new Error(`Unhandled GET call: ${url}`);
  });

  // Mock apiFetch for file search
  apiFetchMock.mockImplementation(async (url: string) => {
    if (url.includes("/find/files?q=")) {
      return createJsonResponse({
        sessionId: "instance-1",
        files: ["src/alpha.ts", "src/components/"],
      });
    }

    throw new Error(`Unhandled apiFetch call: ${url}`);
  });
}

function findFilesQuery(q: string) {
  return expect.objectContaining({ params: expect.objectContaining({ query: { q } }) });
}

// Unmounted after each test: one left mounted would answer to the next test's changes (picked files, say).
const unmounts: (() => void)[] = [];

async function mountAutocomplete(
  initialValue: string,
  cursor: number,
  sessionId: Ref<string> | string = "instance-1",
  sessions: SessionListItem[] = [],
) {
  const value = shallowRef(initialValue);
  const cursorPosition = shallowRef(cursor);
  const input = document.createElement("textarea");
  document.body.appendChild(input);
  const inputRef = shallowRef<HTMLTextAreaElement | null>(input);

  const mounted = await mountComposable(() => useAutocomplete({
    value,
    setValue: (nextValue: string) => {
      value.value = nextValue;
      input.value = nextValue;
    },
    sessionId,
    inputRef,
    cursorPosition,
    sessions,
  }));
  unmounts.push(() => mounted.wrapper.unmount());

  return {
    ...mounted,
    value,
    cursorPosition,
    inputRef,
  };
}

describe("useAutocomplete", () => {
  afterEach(() => {
    unmounts.splice(0).forEach((unmount) => unmount());
  });

  beforeEach(() => {
    apiFetchMock.mockReset();
    mockApi.GET.mockReset();
    configureApiFetch();
    globalHandlers.clear();
    resetFileReferences();
    window.localStorage.clear();
  });

  it("shows slash commands, filters them, and replaces the input on Enter", async () => {
    vi.useFakeTimers();

    const { result, value, cursorPosition, inputRef } = await mountAutocomplete("/", 1);

    await flushAll();

    expect(result.isOpen.value).toBe(true);
    expect(result.items.value.map((item) => item.label)).toEqual(["/help", "/hello", "/status"]);

    value.value = "/he";
    cursorPosition.value = 3;
    await flushAll();

    expect(result.items.value.map((item) => item.label)).toEqual(["/help", "/hello"]);

    const enterEvent = createKeyboardEvent("Enter");
    result.onKeyDown(enterEvent);
    await vi.advanceTimersByTimeAsync(0);

    expect(enterEvent.defaultPrevented).toBe(true);
    expect(value.value).toBe("/help ");
    expect(inputRef.value?.selectionStart).toBe(6);
    expect(inputRef.value?.selectionEnd).toBe(6);
  });

  it("asks for the session's commands and agents again when its harness says they changed", async () => {
    const { result } = await mountAutocomplete("/", 1);
    const asked = (url: string) => mockApi.GET.mock.calls.filter(([called]) => called === url).length;
    expect(asked("/api/sessions/{id}/commands")).toBe(1);

    pushCatalogChange(["another-session"]);
    await flushAll();
    expect(asked("/api/sessions/{id}/commands")).toBe(1);

    pushCatalogChange(["instance-1"]);
    await flushAll();
    expect(asked("/api/sessions/{id}/commands")).toBe(2);
    expect(asked("/api/sessions/{id}/agents")).toBe(2);
    expect(result.items.value.map((item) => item.label)).toEqual(["/help", "/hello", "/status"]);
  });

  it("shows mention suggestions after whitespace and debounces server-side file search", async () => {
    vi.useFakeTimers();

    const { result, value, cursorPosition } = await mountAutocomplete("hello @", 7);

    await flushAll();

    expect(result.isOpen.value).toBe(true);
    expect(result.items.value.map((item) => item.label)).toContain("@alpha");
    expect(result.items.value.map((item) => item.label)).toContain("@beta");

    value.value = "hello @al";
    cursorPosition.value = value.value.length;
    await flushAll();

    expect(mockApi.GET).not.toHaveBeenCalledWith("/api/sessions/{id}/find/files", findFilesQuery("al"));

    await vi.advanceTimersByTimeAsync(79);
    expect(mockApi.GET).not.toHaveBeenCalledWith("/api/sessions/{id}/find/files", findFilesQuery("al"));

    await vi.advanceTimersByTimeAsync(1);
    await flushAll();

    expect(mockApi.GET).toHaveBeenCalledWith("/api/sessions/{id}/find/files", findFilesQuery("al"));
    expect(result.items.value.map((item) => item.label)).toEqual(["alpha.ts", "components/", "@alpha"]);
    expect(result.items.value.map((item) => item.description)).toEqual(["src/", "src/", "Planner"]);

    value.value = "hello@al";
    cursorPosition.value = value.value.length;
    await flushAll();

    expect(result.isOpen.value).toBe(false);
  });

  it("lists the session folder as soon as @ is typed, files and folders before agents", async () => {
    vi.useFakeTimers();

    const { result } = await mountAutocomplete("look at @", 9);
    await vi.advanceTimersByTimeAsync(0);
    await flushAll();

    expect(mockApi.GET).toHaveBeenCalledWith("/api/sessions/{id}/find/files", findFilesQuery(""));
    expect(result.items.value.map((item) => item.group)).toEqual(["file", "file", "agent", "agent"]);
  });

  it("opens a folder on Tab and references it on Enter", async () => {
    vi.useFakeTimers();

    const { result, value, cursorPosition } = await mountAutocomplete("look at @comp", 13);
    await vi.advanceTimersByTimeAsync(300);
    await flushAll();

    result.onKeyDown(createKeyboardEvent("ArrowDown"));
    expect(result.selectedValue.value).toBe("@src/components/ ");

    const tabEvent = createKeyboardEvent("Tab");
    result.onKeyDown(tabEvent);
    await vi.advanceTimersByTimeAsync(0);
    await flushAll();

    expect(tabEvent.defaultPrevented).toBe(true);
    expect(value.value).toBe("look at @src/components/");
    // Opened to look inside, not picked.
    expect(pickedFileTokens("instance-1")).toEqual(new Set());
    expect(cursorPosition.value).toBe(value.value.length);
    expect(result.isOpen.value).toBe(true);
    expect(mockApi.GET).toHaveBeenCalledWith("/api/sessions/{id}/find/files", findFilesQuery("src/components/"));

    result.onKeyDown(createKeyboardEvent("ArrowDown"));
    result.onKeyDown(createKeyboardEvent("Enter"));
    await vi.advanceTimersByTimeAsync(0);

    expect(value.value).toBe("look at @src/components/ ");
    expect(result.isOpen.value).toBe(false);
    expect(pickedFileTokens("instance-1")).toEqual(new Set(["@src/components/"]));

    // Backspace over the space after it: the reference is done, so the list stays shut.
    value.value = "look at @src/components/";
    cursorPosition.value = value.value.length;
    await flushAll();
    expect(result.isOpen.value).toBe(false);
  });

  it("holds Enter pressed before anything matches, and picks the first match when the answer comes", async () => {
    vi.useFakeTimers();

    const { result, value, cursorPosition } = await mountAutocomplete("look at @", 9);
    await vi.advanceTimersByTimeAsync(0);
    await flushAll();

    value.value = "look at @zeta";
    cursorPosition.value = value.value.length;
    await flushAll();
    expect(result.items.value).toEqual([]);

    const enter = createKeyboardEvent("Enter");
    result.onKeyDown(enter);
    expect(enter.defaultPrevented).toBe(true);
    expect(value.value).toBe("look at @zeta");

    await vi.advanceTimersByTimeAsync(80);
    await flushAll();
    await vi.advanceTimersByTimeAsync(0);

    expect(value.value).toBe("look at @src/alpha.ts ");
  });

  it("narrows the last answer as you type, so the list and Enter follow each key before the server answers", async () => {
    vi.useFakeTimers();

    const { result, value, cursorPosition } = await mountAutocomplete("look at @", 9);
    await vi.advanceTimersByTimeAsync(0);
    await flushAll();
    expect(result.items.value.filter((item) => item.group === "file").map((item) => item.label)).toEqual(["alpha.ts", "components/"]);

    value.value = "look at @comp";
    cursorPosition.value = value.value.length;
    await flushAll();

    expect(mockApi.GET).not.toHaveBeenCalledWith("/api/sessions/{id}/find/files", findFilesQuery("comp"));
    expect(result.isLoading.value).toBe(true);
    expect(result.items.value.map((item) => item.label)).toEqual(["components/"]);

    result.onKeyDown(createKeyboardEvent("Enter"));
    await vi.advanceTimersByTimeAsync(0);

    expect(value.value).toBe("look at @src/components/ ");
    expect(pickedFileTokens("instance-1")).toEqual(new Set(["@src/components/"]));
  });

  it("doesn't search files while typing a slash command", async () => {
    vi.useFakeTimers();

    await mountAutocomplete("/he", 3);
    await vi.advanceTimersByTimeAsync(300);
    await flushAll();

    expect(mockApi.GET).not.toHaveBeenCalledWith("/api/sessions/{id}/find/files", expect.anything());
  });

  it("wraps arrow navigation, selects on Tab, and reopens after Escape when typing resumes", async () => {
    vi.useFakeTimers();

    const { result, value, cursorPosition } = await mountAutocomplete("/", 1);

    await flushAll();

    result.onKeyDown(createKeyboardEvent("ArrowUp"));
    expect(result.selectedIndex.value).toBe(2);
    expect(result.selectedValue.value).toBe("/status ");

    result.onKeyDown(createKeyboardEvent("ArrowDown"));
    expect(result.selectedIndex.value).toBe(0);
    expect(result.selectedValue.value).toBe("/help ");

    result.onKeyDown(createKeyboardEvent("ArrowDown"));
    expect(result.selectedIndex.value).toBe(1);
    expect(result.selectedValue.value).toBe("/hello ");

    const escapeEvent = createKeyboardEvent("Escape");
    result.onKeyDown(escapeEvent);
    expect(escapeEvent.defaultPrevented).toBe(true);
    expect(result.isOpen.value).toBe(false);

    value.value = "/h";
    cursorPosition.value = 2;
    await flushAll();

    expect(result.isOpen.value).toBe(true);

    const tabEvent = createKeyboardEvent("Tab");
    result.onKeyDown(tabEvent);
    await vi.advanceTimersByTimeAsync(0);

    expect(tabEvent.defaultPrevented).toBe(true);
    expect(value.value).toBe("/help ");
  });

  it("reloads session-scoped suggestions when the session id changes", async () => {
    const sessionId = shallowRef("instance-1");

    await mountAutocomplete("/", 1, sessionId);
    await flushAll();

    expect(mockApi.GET).toHaveBeenCalledWith("/api/sessions/{id}/commands", expect.objectContaining({
      params: { path: { id: "instance-1" } },
    }));

    sessionId.value = "instance-2";
    await flushAll();

    expect(mockApi.GET).toHaveBeenCalledWith("/api/sessions/{id}/commands", expect.objectContaining({
      params: { path: { id: "instance-2" } },
    }));
    expect(mockApi.GET).toHaveBeenCalledWith("/api/sessions/{id}/agents", expect.objectContaining({
      params: { path: { id: "instance-2" } },
    }));
  });

  describe("sessions", () => {
    function session(id: string, title: string, hoursAgo: number, overrides: Partial<SessionListItem> = {}): SessionListItem {
      const updated = Date.now() - hoursAgo * 60 * 60_000;
      return {
        instanceId: "inst",
        workspaceId: "ws",
        workspaceDirectory: "/work/tidytempo",
        workspaceDisplayName: null,
        isolationStrategy: "existing",
        sessionStatus: "idle",
        session: { id, title, time: { created: updated, updated } },
        instanceStatus: "running",
        lifecycleStatus: "running",
        retentionStatus: "active",
        typedInstanceStatus: "running",
        isHidden: false,
        harnessType: "claude-code",
        tags: [],
        ...overrides,
      } as SessionListItem;
    }

    const sessions = [
      session("instance-1", "t3code in this session", 0),
      session("ses-a", "t3code: what can we learn?", 1),
      session("ses-b", "Survey t3code features", 2),
      session("ses-c", "Fleet MCP server spike", 3),
      session("ses-d", "Onboarding time zone fixes", 4),
    ];

    beforeEach(() => {
      localStorage.clear();
      resetSessionReferences();
    });

    it("offers matching sessions before files, never the session being written to", async () => {
      vi.useFakeTimers();

      const { result } = await mountAutocomplete("Use @t3c", 8, "instance-1", sessions);
      await vi.advanceTimersByTimeAsync(300);
      await flushAll();

      expect(result.items.value.map((item) => item.group)).toEqual(["session", "session", "file", "file"]);
      expect(result.items.value.slice(0, 2).map((item) => [item.label, item.description])).toEqual([
        ["t3code: what can we learn?", "tidytempo · Claude Code"],
        ["Survey t3code features", "tidytempo · Claude Code"],
      ]);
      expect(result.query.value).toBe("t3c");
      expect(result.isMentionOpen.value).toBe(true);
    });

    it("offers only the three newest for a bare @, which is mostly for files", async () => {
      vi.useFakeTimers();

      const { result } = await mountAutocomplete("look at @", 9, "instance-1", sessions);
      await vi.advanceTimersByTimeAsync(0);
      await flushAll();

      expect(result.items.value.filter((item) => item.group === "session").map((item) => item.id))
        .toEqual(["session:ses-a", "session:ses-b", "session:ses-c"]);
    });

    it("attaches a session on Tab as a token from its title, which goes with the message", async () => {
      vi.useFakeTimers();

      const { result, value } = await mountAutocomplete("Use the mapping from @t3c", 25, "instance-1", sessions);
      await vi.advanceTimersByTimeAsync(300);
      await flushAll();

      const tab = createKeyboardEvent("Tab");
      result.onKeyDown(tab);
      await vi.advanceTimersByTimeAsync(0);

      expect(tab.defaultPrevented).toBe(true);
      expect(value.value).toBe("Use the mapping from @t3code-what-can-we-learn ");
      expect(result.isOpen.value).toBe(false);
      expect(sessionReferencesIn("instance-1", value.value)).toEqual([
        { token: "@t3code-what-can-we-learn", sessionId: "ses-a", title: "t3code: what can we learn?" },
      ]);
    });
  });
});
