import { describe, expect, it } from "vitest";
import { NEW_PROFILE_CONTENT, parseJsonc, profileSummary } from "@/lib/harness-profile";

describe("profileSummary", () => {
  it("names the model, then what the profile adds", () => {
    const content = `{
      // Work: Claude through Bedrock
      "model": "amazon-bedrock/anthropic.claude-sonnet-5",
      "provider": { "amazon-bedrock": { "options": { "region": "eu-west-1" } } },
      "mcp": { "linear": { "type": "remote", "url": "https://mcp.linear.app/mcp" } },
    }`;

    expect(profileSummary(content)).toBe("amazon-bedrock/anthropic.claude-sonnet-5 · 1 provider · 1 MCP server");
  });

  it("counts OpenCode 2's own names for providers, agents and MCP servers too", () => {
    const content = `{
      "providers": { "work": {}, "local": {} },
      "agents": { "review": {} },
      "mcp": { "servers": { "linear": { "type": "remote", "url": "https://mcp.linear.app/mcp" } } },
    }`;

    expect(profileSummary(content)).toBe("2 providers · 1 MCP server · 1 agent");
  });

  it("says when a profile limits the providers", () => {
    expect(profileSummary(`{ "enabled_providers": ["ollama"] }`)).toBe("only ollama");
    expect(profileSummary(`{ "disabled_providers": ["openai", "groq"] }`)).toBe("turns off openai, groq");
  });

  it("describes agents and a default agent", () => {
    expect(profileSummary(`{ "default_agent": "review", "agent": { "review": {} } }`)).toBe("default agent review · 1 agent");
  });

  it("says a new profile has no settings yet, and counts other settings", () => {
    expect(profileSummary(NEW_PROFILE_CONTENT)).toBe("No settings yet");
    expect(profileSummary(`{ "share": "disabled", "autoupdate": false }`)).toBe("2 settings");
  });

  it("doesn't pretend to read broken JSON", () => {
    expect(profileSummary(`{ "model": `)).toBe("Can't read this profile");
  });
});

describe("parseJsonc", () => {
  it("reads comments and trailing commas but keeps // inside strings", () => {
    expect(parseJsonc(`{ /* a */ "url": "https://x.dev/mcp", // b
      "list": [1, 2,], }`)).toEqual({ url: "https://x.dev/mcp", list: [1, 2] });
  });

  it("returns null for anything that isn't an object", () => {
    expect(parseJsonc("[1]")).toBeNull();
    expect(parseJsonc("nope")).toBeNull();
  });
});
