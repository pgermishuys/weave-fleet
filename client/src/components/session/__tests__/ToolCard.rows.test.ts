import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import ToolCard from "@/components/session/ToolCard.vue";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import { toToolCardItem } from "@/components/session/activity-stream-tool-card";
import type { AccumulatedToolPart } from "@/lib/client-types";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";

/** Characterizes the tool row as ToolCard draws it today: glyph per status, open/closed rules, label and detail. */
function card(props: Record<string, unknown>, inlineDiffs?: boolean) {
  const pinia = createPinia();
  setActivePinia(pinia);
  if (inlineDiffs !== undefined) useWorkspaceUiStore(pinia).setInlineToolDiffs(inlineDiffs);
  return mount(ToolCard, { global: { plugins: [pinia] }, props: { id: "c1", title: "src/a.ts", ...props } });
}

const header = (w: ReturnType<typeof card>) => w.get("[data-testid='tool-card-header']");
const isOpen = (w: ReturnType<typeof card>) => w.get("[data-testid='tool-card']").attributes("open") !== undefined;

describe("ToolCard status indicator", () => {
  it.each([
    ["Running", "running"],
    ["Error", "error"],
    ["Background", "running"],
  ])("shows a %s call with a %s glyph", (status, glyph) => {
    const w = card({ kind: "bash", status });

    expect(w.getComponent(StatusGlyph).props("status")).toBe(glyph);
    expect(header(w).find(".tool-header__status").exists()).toBe(true);
    expect(header(w).find("[aria-label='Completed']").exists()).toBe(false);
  });

  it("labels a Background call with the word as well as the glyph", () => {
    const w = card({ kind: "bash", status: "Background" });

    expect(w.get("[data-testid='tool-card-background']").text()).toBe("Background");
  });

  it("shows a Completed call with a check and no glyph", () => {
    const w = card({ kind: "bash" });

    expect(header(w).find("[aria-label='Completed']").exists()).toBe(true);
    expect(w.findComponent(StatusGlyph).exists()).toBe(false);
  });

  it("shows a Stopped call as the word Stopped, with no glyph and no check", () => {
    const w = card({ kind: "bash", status: "Stopped" });

    expect(w.get("[data-testid='tool-card-stopped']").text()).toBe("Stopped");
    expect(w.findComponent(StatusGlyph).exists()).toBe(false);
    expect(header(w).find("[aria-label='Completed']").exists()).toBe(false);
  });

  it.each(["Pending", "Cancelled", "SomethingElse"])("shows a %s call with no indicator at all", (status) => {
    const w = card({ kind: "bash", status });

    expect(w.findComponent(StatusGlyph).exists()).toBe(false);
    expect(header(w).find("[aria-label='Completed']").exists()).toBe(false);
    expect(header(w).find(".tool-header__status").exists()).toBe(false);
  });

  it("defaults to Completed when no status is given", () => {
    expect(header(card({ kind: "bash", status: undefined })).find("[aria-label='Completed']").exists()).toBe(true);
  });

  it("shows +N and -N instead of the check when a completed call changed a file", () => {
    const w = card({
      kind: "edit",
      diffLines: [
        { type: "remove", content: "-a" },
        { type: "add", content: "+b" },
        { type: "add", content: "+c" },
        { type: "context", content: " d" },
      ],
    });

    expect(header(w).get(".tool-header__adds").text()).toBe("+2");
    expect(header(w).get(".tool-header__removes").text()).toBe("−1");
    expect(header(w).find("[aria-label='Completed']").exists()).toBe(false);
  });

  it("shows the glyph, not the counts, while an edit is still running", () => {
    const w = card({ kind: "edit", status: "Running", diffLines: [{ type: "add", content: "+b" }] });

    expect(w.findComponent(StatusGlyph).exists()).toBe(true);
    expect(header(w).find(".tool-header__adds").exists()).toBe(false);
  });

  it("shows the check when the diff has only context lines", () => {
    const w = card({ kind: "edit", diffLines: [{ type: "context", content: " same" }] });

    expect(header(w).find(".tool-header__adds").exists()).toBe(false);
    expect(header(w).find("[aria-label='Completed']").exists()).toBe(true);
  });
});

describe("ToolCard open and closed", () => {
  it("starts open unless told it starts collapsed", () => {
    expect(isOpen(card({ kind: "bash" }))).toBe(true);
    expect(isOpen(card({ kind: "bash", initiallyCollapsed: true }))).toBe(false);
  });

  it("keeps the header, and the preview line, when collapsed", () => {
    const w = card({ kind: "bash", title: "ls", initiallyCollapsed: true, preview: "└ a.ts (3 lines)", output: "a.ts\nb.ts\nc.ts" });

    expect(header(w).text()).toContain("Bash");
    expect(w.get(".tool-preview").text()).toBe("└ a.ts (3 lines)");
    expect(isOpen(w)).toBe(false);
  });

  it("follows the native toggle: opening and closing the details flips it", async () => {
    const w = card({ kind: "bash", initiallyCollapsed: true });
    const details = w.get("[data-testid='tool-card']");

    (details.element as HTMLDetailsElement).open = true;
    await details.trigger("toggle");
    expect(isOpen(w)).toBe(true);

    (details.element as HTMLDetailsElement).open = false;
    await details.trigger("toggle");
    expect(isOpen(w)).toBe(false);
  });

  it("follows initiallyCollapsed when the prop changes", async () => {
    const w = card({ kind: "bash", initiallyCollapsed: true });

    await w.setProps({ initiallyCollapsed: false });
    expect(isOpen(w)).toBe(true);
    await w.setProps({ initiallyCollapsed: true });
    expect(isOpen(w)).toBe(false);
  });

  it("stays open for a diff when inline diffs are on, even if told to start collapsed", () => {
    const w = card({ kind: "edit", initiallyCollapsed: true, diffLines: [{ type: "add", content: "+x" }] }, true);

    expect(isOpen(w)).toBe(true);
  });

  it("starts collapsed for a diff when inline diffs are off", () => {
    const w = card({ kind: "edit", initiallyCollapsed: true, diffLines: [{ type: "add", content: "+x" }] }, false);

    expect(isOpen(w)).toBe(false);
    expect(w.find("[data-testid='tool-card-diff']").exists()).toBe(false);
    // With the diff hidden and no output or summary, the body says so.
    expect(w.find("[data-testid='tool-card-empty-state']").exists()).toBe(true);
  });
});

describe("ToolCard label and detail", () => {
  it.each([
    ["read", "Read"],
    ["edit", "Edit"],
    ["bash", "Bash"],
    ["webfetch", "Web Fetch"],
    ["task", "Task"],
    ["execute", "Code"],
    ["fleet_page_show", "Show page"],
    // Pinned as is today: an unlisted tool gets its name with a capital and the Wrench; todowrite is "Todowrite".
    ["todowrite", "Todowrite"],
    ["mystery_tool", "Mystery_tool"],
  ])("labels a %s call %s", (kind, label) => {
    expect(card({ kind }).get(".tool-header__label").text()).toBe(label);
  });

  it("shows the title as a quiet detail, or as a pill for glob and grep", () => {
    const plain = card({ kind: "read", title: "src/a.ts" });
    const pattern = card({ kind: "grep", title: "TODO", isPatternTool: true });

    expect(plain.get(".tool-header__detail").text()).toBe("src/a.ts");
    expect(plain.find(".tool-header__pattern").exists()).toBe(false);
    expect(pattern.get(".tool-header__pattern").text()).toBe("TODO");
    expect(pattern.find(".tool-header__detail").exists()).toBe(false);
  });

  it("uses one icon for a tool it has no icon for, the same one tools sharing a mapping get", () => {
    const icon = (kind: string) => card({ kind }).get(".tool-header__icon").html();

    // todowrite gets the same fallback Wrench as any unknown tool.
    expect(icon("todowrite")).toBe(icon("mystery_tool"));
    expect(icon("todowrite")).not.toBe(icon("read"));
    expect(icon("write")).toBe(icon("edit"));
    expect(icon("glob")).toBe(icon("grep"));
  });

  it("offers Show only on a finished call that has a canvas", () => {
    expect(card({ kind: "fleet_app_start", canvasId: "cv_1" }).find("[data-testid='tool-card-show']").exists()).toBe(true);
    expect(card({ kind: "fleet_app_start", canvasId: "cv_1", status: "Error" }).find("[data-testid='tool-card-show']").exists()).toBe(false);
    expect(card({ kind: "fleet_app_start" }).find("[data-testid='tool-card-show']").exists()).toBe(false);
  });
});

describe("toToolCardItem, which feeds the ToolCard", () => {
  function part(overrides: Record<string, unknown> = {}): AccumulatedToolPart {
    return { type: "tool", partId: "p1", callId: "call-1", tool: "read", state: { status: "completed", input: { filePath: "src/a.ts" } }, ...overrides } as AccumulatedToolPart;
  }
  const withState = (tool: string, state: Record<string, unknown>) => part({ tool, state });

  it("keeps the fields the card shows and drops the raw input", () => {
    const item = toToolCardItem(withState("read", { status: "completed", input: { filePath: "src/a.ts", offset: 5 }, output: "one\ntwo" }));

    expect(item).toMatchObject({
      id: "p1",
      kind: "read",
      status: "Completed",
      output: "one\ntwo",
      preview: "└ one (2 lines)",
      callId: "call-1",
      isPatternTool: false,
    });
    // Pinned as is today: the item carries no `input`; whatever wants it later must read the part again.
    expect(Object.keys(item)).not.toContain("input");
  });

  it("starts every call collapsed except one that failed", () => {
    expect(toToolCardItem(part()).initiallyCollapsed).toBe(true);
    expect(toToolCardItem(withState("read", { status: "running", input: {} })).initiallyCollapsed).toBe(true);
    expect(toToolCardItem(withState("read", { status: "error", input: {}, error: "boom" })).initiallyCollapsed).toBe(false);
  });

  it("capitalises the harness status and calls a missing one Pending", () => {
    expect(toToolCardItem(withState("read", { status: "running", input: {} })).status).toBe("Running");
    expect(toToolCardItem(withState("read", { status: "error", input: {} })).status).toBe("Error");
    expect(toToolCardItem(withState("read", { input: {} })).status).toBe("Pending");
  });

  it("marks glob and grep as pattern tools", () => {
    expect(toToolCardItem(withState("grep", { status: "completed", input: { pattern: "TODO" } })).isPatternTool).toBe(true);
    expect(toToolCardItem(withState("glob", { status: "completed", input: { pattern: "*.ts" } })).isPatternTool).toBe(true);
    expect(toToolCardItem(withState("bash", { status: "completed", input: { command: "ls" } })).isPatternTool).toBe(false);
  });

  it("falls back to the tool's name for the title of a tool it can't label", () => {
    expect(toToolCardItem(withState("mystery_tool", { status: "completed", input: {} })).title).toBe("mystery_tool");
  });

  it("takes the title Fleet's answer gave for Fleet's own tools only", () => {
    const state = { status: "completed", input: {}, title: "Shop · http://localhost:5173/" };

    expect(toToolCardItem(withState("fleet_app_start", state)).title).toBe("Shop · http://localhost:5173/");
    expect(toToolCardItem(withState("mystery_tool", state)).title).toBe("mystery_tool");
  });

  it("reads a kept screenshot and page from the call's metadata", () => {
    const shot = toToolCardItem(withState("fleet_browser_screenshot", {
      status: "completed",
      input: {},
      metadata: { screenshot: { sessionId: "ses-1", id: "shot_1", width: 100, height: 50 } },
    }));
    const shown = toToolCardItem(withState("fleet_page_show", {
      status: "completed",
      input: { path: "/tmp/x/a.html" },
      metadata: { page: { id: "pg_1", entry: "a.html" } },
    }));

    expect(shot.screenshot).toEqual({ path: "/api/sessions/ses-1/screenshots/shot_1", width: 100, height: 50 });
    expect(shown.page).toMatchObject({ id: "pg_1", source: "/tmp/x/a.html" });
  });

  it("never sets a delegation: ActivityStream adds that afterwards", () => {
    expect(toToolCardItem(withState("task", { status: "running", input: { description: "d" } })).delegation).toBeUndefined();
  });
});
