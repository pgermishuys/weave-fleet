/**
 * The wording of a permission ask, kind by kind, as the desktop PermissionCard, the phone's ask pages and the phone's
 * inbox say it. All three read lib/tools/permission-wording.ts now, so they agree. It was written three times before:
 * PermissionCard had no `read` case (a read ask was headed "Use <tool>") where the phone said "Read a file"; the card
 * now says "Read a file" too. A read ask for a tool that is not a file read (todowrite, task, skill, fleet_*) keeps
 * the tool's name, "Use todowrite", where the phone used to say "Read a file" for them as well.
 */
import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import PermissionCard from "@/components/session/PermissionCard.vue";
import type { PermissionAsk } from "@/composables/use-session-permissions";
import { askPreview, type InboxItem } from "@/lib/phone/inbox";
import { dontAskAgainWording, permissionHeading } from "@/lib/tools";

vi.mock("@tanstack/vue-router", () => ({ useNavigate: () => vi.fn() }));

type Kind = PermissionAsk["kind"];

function ask(kind: Kind, tool: string, extra: Partial<PermissionAsk> = {}): PermissionAsk {
  return { id: "per_1", sessionId: "s1", kind, tool, title: "the thing", always: ["*"], askedAt: "2026-10-01T10:00:00Z", ...extra };
}

const CASES: { kind: Kind; tool: string }[] = [
  { kind: "shell", tool: "bash" },
  { kind: "edit", tool: "edit" },
  { kind: "read", tool: "read" },
  { kind: "web", tool: "webfetch" },
  { kind: "other", tool: "mcp__acme__lookup_order" },
  { kind: "other", tool: "external_directory" },
];

function card(value: PermissionAsk) {
  const wrapper = mount(PermissionCard, { props: { ask: value, onAnswer: async () => {} } });
  const button = wrapper.get("[data-testid='permission-allow-always']");
  const code = button.find("code");
  const result = {
    heading: wrapper.get(".pcard__title").text(),
    // The same shape dontAskAgainWording returns: the words, and the code after them.
    always: {
      lead: button.text().replace(/^2/, "").replace(/this session$/, "").replace(code.exists() ? code.text() : "", "").trim(),
      code: code.exists() ? code.text() : null,
    },
  };
  wrapper.unmount();
  return result;
}

function preview(value: PermissionAsk) {
  const entry = { status: "waiting_input", ask: { kind: "permission", ask: value } } as unknown as InboxItem;
  return askPreview(entry);
}

describe("permission wording today", () => {
  it("the desktop card's heading and 'Don't ask again' line, per kind", () => {
    expect(CASES.map(({ kind, tool }) => card(ask(kind, tool)))).toEqual([
      { heading: "Run a command", always: { lead: "Don't ask again for commands", code: null } },
      { heading: "Edit a file", always: { lead: "Don't ask again for file edits", code: null } },
      // The card used to have no `read` case ("Use read").
      { heading: "Read a file", always: { lead: "Don't ask again for", code: "read" } },
      { heading: "Go online", always: { lead: "Don't ask again for web access", code: null } },
      { heading: "Use mcp__acme__lookup_order", always: { lead: "Don't ask again for", code: "mcp__acme__lookup_order" } },
      { heading: "Work outside the folder", always: { lead: "Don't ask again for", code: "external_directory" } },
    ]);
  });

  it("the card names the harness's patterns when it sent some", () => {
    expect(card(ask("shell", "bash", { always: ["dotnet test *", "*"] })).always).toEqual({ lead: "Don't ask again for", code: "dotnet test *" });
  });

  it("the phone's heading and 'Don't ask again', per kind", () => {
    expect(CASES.map(({ kind, tool }) => permissionHeading(ask(kind, tool)))).toEqual([
      "Run a command",
      "Edit a file",
      "Read a file",
      "Go online",
      "Use mcp__acme__lookup_order",
      "Work outside the folder",
    ]);
    expect(CASES.map(({ kind, tool }) => dontAskAgainWording(ask(kind, tool)))).toEqual([
      { lead: "Don't ask again for commands", code: null },
      { lead: "Don't ask again for file edits", code: null },
      { lead: "Don't ask again for", code: "read" },
      { lead: "Don't ask again for web access", code: null },
      { lead: "Don't ask again for", code: "mcp__acme__lookup_order" },
      { lead: "Don't ask again for", code: "external_directory" },
    ]);
    expect(dontAskAgainWording(ask("shell", "bash", { always: ["dotnet test *"] }))).toEqual({ lead: "Don't ask again for", code: "dotnet test *" });
  });

  it("the phone inbox's one-line preview, per kind", () => {
    expect(CASES.map(({ kind, tool }) => preview(ask(kind, tool)))).toEqual([
      { lead: "Wants to run", detail: "the thing", code: true },
      { lead: "Wants to edit", detail: "the thing", code: true },
      { lead: "Wants to read", detail: "the thing", code: true },
      { lead: "Wants to open", detail: "the thing", code: true },
      { lead: "Wants to use mcp__acme__lookup_order", detail: "the thing", code: true },
      { lead: "Wants to use external_directory", detail: "the thing", code: true },
    ]);
  });
});
