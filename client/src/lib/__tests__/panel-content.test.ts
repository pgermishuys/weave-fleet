import { describe, expect, it } from "vitest";
import { panelHasContent, type PanelContentState } from "@/lib/panel-content";

const base: PanelContentState = {
  changedFiles: 0,
  canvases: [{ kind: "changes" }, { kind: "files" }],
  progress: false,
  context: false,
  agents: false,
};

describe("panelHasContent", () => {
  it("has something to show when the changes failed to load: the panel opens on the error and Try again, not shut as if empty", () => {
    expect(panelHasContent({ ...base, changesFailed: true })).toBe(true);
  });

  it("is empty for a fresh session", () => {
    expect(panelHasContent(base)).toBe(false);
  });

  it.each(["page", "browser", "visual", "file", "terminal", "my-mod-pane"])("counts a %s tab as content", (kind) => {
    expect(panelHasContent({ ...base, canvases: [...base.canvases, { kind }] })).toBe(true);
  });

  it.each([
    ["changes", { changedFiles: 2 }],
    ["a todo list", { progress: true }],
    ["links", { context: true }],
    ["running agents", { agents: true }],
  ])("counts %s as content", (_name, patch) => {
    expect(panelHasContent({ ...base, ...patch })).toBe(true);
  });
});
