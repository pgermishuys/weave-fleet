import { effectScope, nextTick, ref } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { FIND_TEXT_DEBOUNCE_MS, useFindText } from "@/composables/use-find-text";

const get = vi.fn();
vi.mock("@/lib/machine-target", () => ({ useMachineTarget: () => ({ api: { GET: get } }) }));

const answer = {
  files: [{ path: "src/a.ts", total: 1, matches: [{ line: 3, column: 5, text: "needle", ranges: [{ start: 0, end: 6 }] }] }],
  matches: 1,
  stopped: null,
  engine: "git",
};

function respond(status: number, data?: unknown, error?: unknown) {
  return { data, error, response: { status, ok: status >= 200 && status < 300 } };
}

async function settle() {
  await vi.advanceTimersByTimeAsync(FIND_TEXT_DEBOUNCE_MS + 1);
}

describe("useFindText", () => {
  const query = ref("");
  const regex = ref(false);
  const matchCase = ref(false);
  let scope: ReturnType<typeof effectScope>;

  function setup() {
    scope = effectScope();
    return scope.run(() => useFindText("session-1", query, { regex, matchCase }))!;
  }

  beforeEach(() => {
    vi.useFakeTimers();
    get.mockReset();
    query.value = "";
    regex.value = false;
    matchCase.value = false;
  });

  afterEach(() => {
    scope.stop();
    vi.useRealTimers();
  });

  it("asks_nothing_for_an_empty_or_one_letter_query", async () => {
    const find = setup();
    query.value = "n";
    await settle();

    expect(get).not.toHaveBeenCalled();
    expect(find.result.value).toBeNull();
    expect(find.isActive.value).toBe(false);
  });

  it("waits_for_typing_to_pause_then_asks_once_with_the_switches", async () => {
    get.mockResolvedValue(respond(200, answer));
    const find = setup();
    regex.value = true;
    matchCase.value = true;
    query.value = "ne";
    await nextTick();
    query.value = "need";
    await nextTick();
    await vi.advanceTimersByTimeAsync(FIND_TEXT_DEBOUNCE_MS - 50);
    expect(get).not.toHaveBeenCalled();
    await settle();

    expect(get).toHaveBeenCalledTimes(1);
    expect(get.mock.calls[0][0]).toBe("/api/sessions/{id}/find/text");
    expect(get.mock.calls[0][1].params).toEqual({ path: { id: "session-1" }, query: { q: "need", regex: true, matchCase: true } });
    expect(find.result.value).toEqual(answer);
    expect(find.failure.value).toBeNull();
    expect(find.isLoading.value).toBe(false);
  });

  it("reports_a_bad_pattern_with_the_servers_reason_and_no_result", async () => {
    get.mockResolvedValue(respond(400, undefined, { error: "Unmatched [ (git extended regex)" }));
    const find = setup();
    query.value = "a[";
    await settle();

    expect(find.failure.value).toEqual({ kind: "pattern", message: "Unmatched [ (git extended regex)" });
    expect(find.result.value).toBeNull();
  });

  it("says_an_older_machine_cant_search_instead_of_showing_no_matches", async () => {
    get.mockResolvedValue(respond(404, undefined, "Not found"));
    const find = setup();
    query.value = "needle";
    await settle();

    expect(find.failure.value?.kind).toBe("unsupported");
    expect(find.result.value).toBeNull();
  });

  it("reports_any_other_failure_and_a_thrown_request", async () => {
    get.mockResolvedValueOnce(respond(500, undefined, { error: "boom" })).mockRejectedValueOnce(new Error("offline"));
    const find = setup();
    query.value = "needle";
    await settle();
    expect(find.failure.value).toEqual({ kind: "failed", message: "boom" });

    query.value = "needles";
    await settle();
    expect(find.failure.value).toEqual({ kind: "failed", message: "offline" });
  });

  it("clears_the_answer_when_the_query_gets_short_again", async () => {
    get.mockResolvedValue(respond(200, answer));
    const find = setup();
    query.value = "needle";
    await settle();
    expect(find.result.value).not.toBeNull();

    query.value = "";
    await nextTick();

    expect(find.result.value).toBeNull();
    expect(find.isLoading.value).toBe(false);
  });

  it("aborts_a_request_a_newer_query_replaces", async () => {
    let first: AbortSignal | undefined;
    get.mockImplementationOnce((_path: string, init: { signal: AbortSignal }) => {
      first = init.signal;
      return new Promise(() => {});
    });
    get.mockResolvedValueOnce(respond(200, answer));
    setup();
    query.value = "needle";
    await settle();
    query.value = "needles";
    await nextTick();

    expect(first?.aborted).toBe(true);
  });
});
