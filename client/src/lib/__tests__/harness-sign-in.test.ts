import { describe, expect, it } from "vitest";
import type { HarnessSignInField, HarnessSignInProvider } from "@/api/client";
import {
  answersToSend,
  codeToEnter,
  initialAnswers,
  isRemoteBrowser,
  methodsToOffer,
  missingAnswer,
  signInSummary,
  visibleFields,
} from "@/lib/harness-sign-in";

function field(key: string, extra: Partial<HarnessSignInField> = {}): HarnessSignInField {
  return { key, type: "string", required: false, hidden: false, ...extra };
}

// GitHub Copilot's device sign-in on OpenCode 2.0.9: an enterprise URL only for GitHub Enterprise.
const copilot = [
  field("deploymentType", {
    title: "Select GitHub deployment type",
    required: true,
    options: [{ value: "github.com", label: "GitHub.com" }, { value: "enterprise", label: "GitHub Enterprise" }],
  }),
  field("enterpriseUrl", {
    title: "Enter your GitHub Enterprise URL or domain",
    required: true,
    when: [{ key: "deploymentType", op: "eq", value: "enterprise" }],
  }),
];

describe("sign-in forms", () => {
  it("starts a choice on its first option and a hidden field on its default", () => {
    const answers = initialAnswers([...copilot, field("server", { hidden: true, default: "https://opencode.ai/console" })]);

    expect(answers).toEqual({ deploymentType: "github.com", enterpriseUrl: "", server: "https://opencode.ai/console" });
  });

  it("asks a field only while its condition holds", () => {
    expect(visibleFields(copilot, { deploymentType: "github.com" }).map((f) => f.key)).toEqual(["deploymentType"]);
    expect(visibleFields(copilot, { deploymentType: "enterprise" }).map((f) => f.key)).toEqual(["deploymentType", "enterpriseUrl"]);
    expect(visibleFields([field("x", { when: [{ key: "mode", op: "neq", value: "a" }] })], { mode: "b" })).toHaveLength(1);
  });

  it("names the first needed field without an answer", () => {
    expect(missingAnswer(copilot, { deploymentType: "enterprise", enterpriseUrl: "" })).toBe("Enter your GitHub Enterprise URL or domain");
    expect(missingAnswer(copilot, { deploymentType: "github.com", enterpriseUrl: "" })).toBeNull();
  });

  it("sends what was asked and the hidden defaults, and numbers as numbers", () => {
    const fields = [...copilot, field("server", { hidden: true, default: "https://opencode.ai/console" }), field("port", { type: "integer" })];

    expect(answersToSend(fields, { deploymentType: "github.com", enterpriseUrl: "left over", port: "8080" }))
      .toEqual({ deploymentType: "github.com", server: "https://opencode.ai/console", port: 8080 });
  });
});

describe("sign-in helpers", () => {
  it("offers the environment last", () => {
    const provider: HarnessSignInProvider = {
      id: "openai",
      name: "OpenAI",
      connections: [],
      methods: [
        { type: "key", label: "API key", fields: [] },
        { type: "env", label: "Environment variable", fields: [], environmentVariables: ["OPENAI_API_KEY"] },
        { type: "oauth", id: "chatgpt-browser", label: "ChatGPT Pro/Plus (browser)", fields: [] },
      ],
    };

    expect(methodsToOffer(provider).map((m) => m.type)).toEqual(["key", "oauth", "env"]);
  });

  it("finds the code a device sign-in shows", () => {
    expect(codeToEnter("Enter code: ABCD-1234")).toBe("ABCD-1234");
    expect(codeToEnter("Open https://x.ai/device on any device and enter code: W1Z2-QQ")).toBe("W1Z2-QQ");
    expect(codeToEnter("Complete authorization in your browser. This window will close automatically.")).toBeNull();
  });

  it("tells this computer from another device", () => {
    for (const host of ["localhost", "127.0.0.1", "[::1]", "fleet.localhost"]) expect(isRemoteBrowser(host)).toBe(false);
    for (const host of ["my-laptop.tail1234.ts.net", "100.101.102.103", "192.168.1.20"]) expect(isRemoteBrowser(host)).toBe(true);
  });

  it("says which sign-in a provider uses", () => {
    const provider = (connections: HarnessSignInProvider["connections"]): HarnessSignInProvider =>
      ({ id: "anthropic", name: "Anthropic", methods: [], connections });

    expect(signInSummary(provider([]))).toBeNull();
    expect(signInSummary(provider([{ kind: "credential", id: "cred_1", label: "Work", active: true }]))).toBe("Signed in");
    expect(signInSummary(provider([{ kind: "env", id: "ANTHROPIC_API_KEY", label: "ANTHROPIC_API_KEY", active: true }])))
      .toBe("From ANTHROPIC_API_KEY");
  });
});
