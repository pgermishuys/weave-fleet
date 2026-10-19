import { describe, expect, it } from "vitest";
import { allowedRuleWording } from "@/lib/tools";

const rule = (kind: string, tool: string, ...patterns: string[]) => ({ kind, tool, patterns });

describe("allowedRuleWording", () => {
  it("says what a command pattern lets the agent run", () => {
    expect(allowedRuleWording(rule("shell", "bash", "bun test*"))).toEqual({ lead: "Run", code: "bun test*", tail: " without asking", broad: false });
  });

  it("lists every pattern one click allowed", () => {
    expect(allowedRuleWording(rule("shell", "bash", "git status*", "git diff*")).code).toBe("git status*, git diff*");
  });

  it("marks a rule with no pattern as broad", () => {
    expect(allowedRuleWording(rule("shell", "bash", "*"))).toMatchObject({ lead: "Run any command", code: null, broad: true });
    expect(allowedRuleWording(rule("edit", "edit", "*"))).toMatchObject({ lead: "Edit files", broad: true });
    expect(allowedRuleWording(rule("web", "WebFetch"))).toMatchObject({ lead: "Go online", broad: true });
  });

  it("names another tool", () => {
    expect(allowedRuleWording(rule("other", "fleet_page_show", "*"))).toMatchObject({ lead: "Use", code: "fleet_page_show", broad: true });
    expect(allowedRuleWording(rule("other", "mcp__acme__lookup", "orders/*")).code).toBe("mcp__acme__lookup on orders/*");
  });
});
