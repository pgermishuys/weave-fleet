import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));
vi.mock("@/components/ui/dialog", () => {
  const pass = (name: string) => defineComponent({ name, setup: (_p, { slots }) => () => h("div", slots.default?.()) });
  return {
    Dialog: defineComponent({ name: "DialogStub", props: { open: Boolean }, setup: (props, { slots }) => () => (props.open ? h("div", slots.default?.()) : null) }),
    DialogContent: pass("DialogContent"),
    DialogDescription: pass("DialogDescription"),
    DialogFooter: pass("DialogFooter"),
    DialogHeader: pass("DialogHeader"),
    DialogTitle: pass("DialogTitle"),
  };
});
// CodeMirror needs layout jsdom doesn't have; the editor is a plain textarea here.
vi.mock("@/components/settings/ProfileConfigEditor.vue", () => ({
  default: defineComponent({
    name: "EditorStub",
    props: { modelValue: { type: String, required: true } },
    emits: ["update:modelValue"],
    setup: (props, { emit }) => () => h("textarea", {
      "data-testid": "editor",
      value: props.modelValue,
      onInput: (event: Event) => emit("update:modelValue", (event.target as HTMLTextAreaElement).value),
    }),
  }),
}));

import ImproveSkillDialog from "@/components/skills/ImproveSkillDialog.vue";
import { useNoticesStore } from "@/stores/notices";

const fleet = "---\nname: fleet-code-review\ndescription: Reviews.\n---\n\nReport what would hurt someone.\n";
const better = "---\nname: fleet-code-review\ndescription: Reviews.\n---\n\nReport what would hurt someone. Style isn't a finding.\n";

function detail(overrides: Record<string, unknown> = {}) {
  return {
    name: "fleet-code-review",
    description: "Reviews.",
    enabled: true,
    fleetContent: fleet,
    yourContent: null,
    version: null,
    fleetChanged: false,
    fleetBefore: null,
    versions: [],
    ...overrides,
  };
}

function respond(body: unknown, status = 200): Promise<Response> {
  return Promise.resolve(new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } }));
}

function dialog() {
  return mount(ImproveSkillDialog, {
    props: {
      open: true,
      name: "fleet-code-review",
      sessionId: "s-1",
      turn: { exchange: "The user asked:\nreview this branch", toolCalls: "The tool calls in that turn:\n- bash: git diff" },
    },
  });
}

function bodyOf(path: string): Record<string, unknown> {
  const call = apiFetchMock.mock.calls.find(([called]) => called === path) as [string, RequestInit] | undefined;
  return JSON.parse(call![1].body as string) as Record<string, unknown>;
}

describe("ImproveSkillDialog", () => {
  beforeEach(() => { apiFetchMock.mockReset(); });

  it("sends the note and only the parts of the session that are ticked", async () => {
    apiFetchMock.mockImplementation((path: string) =>
      path.endsWith("/improve")
        ? respond({ name: "fleet-code-review", base: fleet, baseVersion: null, content: better, asks: 1, tokens: { total: 2400, fromCache: 0 } })
        : respond(detail()));
    const wrapper = dialog();
    await flushPromises();

    expect(wrapper.text()).toContain("Fleet's");
    await wrapper.get("[data-testid='improve-skill-note']").setValue("Don't report style.");
    await wrapper.get("form").trigger("submit");
    await flushPromises();

    expect(bodyOf("/api/skills/built-in/fleet-code-review/improve")).toEqual({
      sessionId: "s-1",
      note: "Don't report style.",
      context: "The user asked:\nreview this branch",
      wholeConversation: false,
    });
    expect(wrapper.get("[data-testid='skill-diff']").text()).toContain("Style isn't a finding.");
    expect(wrapper.text()).toContain("The question used 2,400 tokens");
  });

  it("sends no parts when it reads the whole conversation", async () => {
    apiFetchMock.mockImplementation((path: string) =>
      path.endsWith("/improve")
        ? respond({ name: "fleet-code-review", base: fleet, baseVersion: null, content: better, asks: 1, tokens: null })
        : respond(detail()));
    const wrapper = dialog();
    await flushPromises();

    await wrapper.get("[data-testid='improve-skill-include-tools']").setValue(true);
    await wrapper.get("[data-testid='improve-skill-whole-conversation']").setValue(true);
    expect(wrapper.get("[data-testid='improve-skill-include-exchange']").attributes("disabled")).toBeDefined();
    await wrapper.get("[data-testid='improve-skill-note']").setValue("No style.");
    await wrapper.get("form").trigger("submit");
    await flushPromises();

    const body = bodyOf("/api/skills/built-in/fleet-code-review/improve");
    expect(body.wholeConversation).toBe(true);
    expect(body.context).toBeUndefined();
  });

  it("keeps the change, edited by hand, as the next version and offers Undo", async () => {
    apiFetchMock.mockImplementation((path: string, init?: RequestInit) => {
      if (path.endsWith("/improve")) return respond({ name: "fleet-code-review", base: fleet, baseVersion: null, content: better, asks: 1, tokens: null });
      if (path.endsWith("/versions")) return respond(detail({ yourContent: JSON.parse(init!.body as string).content, version: 1 }));
      if (path.endsWith("/active")) return respond(detail());
      return respond(detail());
    });
    const notices = useNoticesStore();
    const wrapper = dialog();
    await flushPromises();
    await wrapper.get("[data-testid='improve-skill-note']").setValue("No style.");
    await wrapper.get("form").trigger("submit");
    await flushPromises();

    await wrapper.get("[data-testid='improve-skill-edit']").trigger("click");
    await wrapper.get("[data-testid='editor']").setValue(`${better}Mine too.\n`);
    await wrapper.get("[data-testid='improve-skill-keep']").trigger("click");
    await flushPromises();

    expect(bodyOf("/api/skills/built-in/fleet-code-review/versions")).toEqual({
      content: `${better}Mine too.\n`,
      note: "No style.",
      sessionId: "s-1",
    });
    expect(wrapper.find("[role='alert']").exists() ? wrapper.get("[role='alert']").text() : "").toBe("");
    expect(wrapper.emitted("update:open")?.at(-1)).toEqual([false]);
    const notice = notices.notices.find((candidate) => candidate.id === "skill-version-fleet-code-review-1");
    expect(notice?.title).toBe("fleet-code-review is now your version 1");

    await notice!.actions![0]!.run();
    expect(bodyOf("/api/skills/built-in/fleet-code-review/active")).toEqual({ version: null });
  });

  it("shows why the model couldn't propose a change and lets the note be changed", async () => {
    apiFetchMock.mockImplementation((path: string) =>
      path.endsWith("/improve")
        ? respond({ error: "The model didn't change anything. Say more about what should be different." }, 400)
        : respond(detail()));
    const wrapper = dialog();
    await flushPromises();
    await wrapper.get("[data-testid='improve-skill-note']").setValue("Better.");
    await wrapper.get("form").trigger("submit");
    await flushPromises();

    expect(wrapper.get("[data-testid='improve-skill-error']").text()).toBe("The model didn't change anything. Say more about what should be different.");
    expect(wrapper.get<HTMLTextAreaElement>("[data-testid='improve-skill-note']").element.disabled).toBe(false);
  });
});
