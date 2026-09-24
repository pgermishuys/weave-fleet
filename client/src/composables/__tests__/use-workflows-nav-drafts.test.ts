import { flushPromises } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: () => () => {} }));

import { DRAFT_ID, useWorkflowsNav } from "@/composables/use-workflows-nav";
import type { DraftedWorkflow } from "@/lib/workflow-draft";

const drafted: DraftedWorkflow = {
  repository: "/work/repo",
  repositoryName: "repo",
  sessionTitle: "Fix the login bug",
  check: { text: "name: Fix a bug\n", errors: [], draft: { name: "Fix a bug", description: null, placeholder: null, steps: [] }, comments: [] },
  asks: 1,
  tokens: null,
};

function respond(body: unknown, status = 200): Promise<Response> {
  return Promise.resolve(new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } }));
}

/** Answers the draft request when told to, and aborts it like fetch does. */
function pending() {
  let answer: (response: Response) => void = () => {};
  apiFetchMock.mockImplementation((path: string, init?: RequestInit) => {
    if (!path.startsWith("/api/workflows/drafts")) return respond({ repository: "/work/repo", repositoryName: "repo", workflows: [] });
    return new Promise<Response>((resolve, reject) => {
      answer = resolve;
      init?.signal?.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError")));
    });
  });
  return { answer: (body: unknown, status = 200) => answer(new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } })) };
}

describe("useWorkflowsNav: drafting a workflow", () => {
  beforeEach(() => {
    apiFetchMock.mockReset();
    const nav = useWorkflowsNav();
    nav.setLeaveGuard(null);
    nav.cancelDraft();
  });

  it("says it's drafting from the session, then hands the draft over and picks its repository", async () => {
    const request = pending();
    const nav = useWorkflowsNav();

    const drafting = nav.draftFromSession("session-1", "Fix the login bug");
    expect(nav.activeWorkflowId.value).toBe(DRAFT_ID);
    expect(nav.drafting.value).toMatchObject({ sessionTitle: "Fix the login bug", error: null });
    const [path, init] = apiFetchMock.mock.calls.find(([p]) => p === "/api/workflows/drafts/from-session") as [string, RequestInit];
    expect(JSON.parse(init.body as string)).toEqual({ sessionId: "session-1" });
    expect(path).toBe("/api/workflows/drafts/from-session");

    request.answer(drafted);
    await drafting;

    expect(nav.drafting.value).toBeNull();
    expect(nav.drafted.value).toEqual(drafted);
    expect(nav.folder.value).toEqual({ kind: "repository", path: "/work/repo" });
  });

  it("asks from a description with the chosen harness", async () => {
    const request = pending();
    const nav = useWorkflowsNav();

    const drafting = nav.draftFromDescription("/work/repo", "Bump the dependencies.", "opencode2");
    const [, init] = apiFetchMock.mock.calls.find(([p]) => p === "/api/workflows/drafts/from-description") as [string, RequestInit];
    expect(JSON.parse(init.body as string)).toEqual({ directory: "/work/repo", description: "Bump the dependencies.", harnessType: "opencode2" });
    expect(nav.drafting.value?.sessionTitle).toBeNull();
    request.answer({ ...drafted, sessionTitle: null });
    await drafting;

    expect(nav.drafted.value?.sessionTitle).toBeNull();
  });

  it("Cancel aborts the request and goes back to the library", async () => {
    pending();
    const nav = useWorkflowsNav();

    const drafting = nav.draftFromSession("session-1", "Fix the login bug");
    const [, init] = apiFetchMock.mock.calls.find(([p]) => p === "/api/workflows/drafts/from-session") as [string, RequestInit];
    nav.cancelDraft();
    await drafting;

    expect(init.signal?.aborted).toBe(true);
    expect(nav.drafting.value).toBeNull();
    expect(nav.drafted.value).toBeNull();
    expect(nav.activeWorkflowId.value).not.toBe(DRAFT_ID);
  });

  it("a failure says why and can be tried again", async () => {
    const request = pending();
    const nav = useWorkflowsNav();

    const drafting = nav.draftFromSession("session-1", "Fix the login bug");
    request.answer({ error: "The model took too long to answer. Try again." }, 400);
    await drafting;
    await flushPromises();

    expect(nav.drafting.value?.error).toBe("The model took too long to answer. Try again.");
    const retried = pending();
    nav.drafting.value!.retry();
    await flushPromises();
    expect(nav.drafting.value?.error).toBeNull();
    retried.answer(drafted);
    await flushPromises();
    expect(nav.drafted.value).toEqual(drafted);
  });

  it("saving the draft makes it the repository's workflow", () => {
    const nav = useWorkflowsNav();
    nav.draftSaved("repo:fix-a-bug");

    expect(nav.activeWorkflowId.value).toBe("repo:fix-a-bug");
    expect(nav.drafted.value).toBeNull();
  });
});
