import { beforeEach, describe, expect, it, vi } from "vitest";
import { shallowRef, type Ref } from "vue";
import { useAutocomplete } from "@/composables/use-autocomplete";
import { flushAll, mountComposable } from "./test-utils";

const { apiFetchMock } = vi.hoisted(() => ({
  apiFetchMock: vi.fn(),
}));

vi.mock("@/lib/api-client", () => ({
  apiFetch: apiFetchMock,
}));

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
  apiFetchMock.mockImplementation(async (url: string) => {
    if (url.endsWith("/commands")) {
      return createJsonResponse({
        commands: [
          { name: "help", description: "Show help" },
          { name: "hello", description: "Say hello" },
          { name: "status", description: "Show status" },
        ],
      });
    }

    if (url.endsWith("/agents")) {
      return createJsonResponse([
        { name: "alpha", description: "Planner", mode: "primary", color: "#ff00aa" },
        { name: "beta", description: "Reviewer", mode: "secondary", color: "#00aaff" },
      ]);
    }

    if (url.includes("/find/files?q=")) {
      return createJsonResponse({
        instanceId: "instance-1",
        files: ["src/alpha.ts", "src/components/"],
      });
    }

    throw new Error(`Unhandled apiFetch call: ${url}`);
  });
}

async function mountAutocomplete(initialValue: string, cursor: number, instanceId: Ref<string> | string = "instance-1") {
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
    instanceId,
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
    configureApiFetch();
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

    expect(apiFetchMock.mock.calls.some(([url]) => String(url).includes("/find/files?q="))).toBe(false);

    await vi.advanceTimersByTimeAsync(299);
    expect(apiFetchMock.mock.calls.some(([url]) => String(url).includes("/find/files?q="))).toBe(false);

    await vi.advanceTimersByTimeAsync(1);
    await flushAll();

    expect(apiFetchMock.mock.calls.some(([url]) => String(url).includes("/find/files?q=al"))).toBe(true);
    expect(result.items.value.map((item) => item.label)).toEqual(["@alpha", "src/alpha.ts", "src/components/"]);

    value.value = "hello@al";
    cursorPosition.value = value.value.length;
    await flushAll();

    expect(result.isOpen.value).toBe(false);
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

  it("reloads instance-scoped suggestions when the instance id changes", async () => {
    const instanceId = shallowRef("instance-1");

    await mountAutocomplete("/", 1, instanceId);
    await flushAll();

    expect(apiFetchMock.mock.calls.some(([url]) => String(url).includes("/api/instances/instance-1/commands"))).toBe(true);

    instanceId.value = "instance-2";
    await flushAll();

    expect(apiFetchMock.mock.calls.some(([url]) => String(url).includes("/api/instances/instance-2/commands"))).toBe(true);
    expect(apiFetchMock.mock.calls.some(([url]) => String(url).includes("/api/instances/instance-2/agents"))).toBe(true);
  });
});
