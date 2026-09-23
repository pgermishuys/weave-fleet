import { beforeEach, describe, expect, it, vi } from "vitest";
import { shallowRef, type Ref } from "vue";
import { useAutocomplete } from "@/composables/use-autocomplete";
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

async function mountAutocomplete(initialValue: string, cursor: number, sessionId: Ref<string> | string = "instance-1") {
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
  }));

  return {
    ...mounted,
    value,
    cursorPosition,
    inputRef,
  };
}

describe("useAutocomplete", () => {
  beforeEach(() => {
    apiFetchMock.mockReset();
    mockApi.GET.mockReset();
    configureApiFetch();
    globalHandlers.clear();
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

    await vi.advanceTimersByTimeAsync(299);
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
    expect(cursorPosition.value).toBe(value.value.length);
    expect(result.isOpen.value).toBe(true);
    expect(mockApi.GET).toHaveBeenCalledWith("/api/sessions/{id}/find/files", findFilesQuery("src/components/"));

    result.onKeyDown(createKeyboardEvent("ArrowDown"));
    result.onKeyDown(createKeyboardEvent("Enter"));
    await vi.advanceTimersByTimeAsync(0);

    expect(value.value).toBe("look at @src/components/ ");
    expect(result.isOpen.value).toBe(false);
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
});
