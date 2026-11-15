import { describe, expect, it } from "vitest";
import { checkPhrase, isTitleCase, phrasesOf, serverPhrases, type Phrase, type Rule } from "../lint-words";

const rulesOf = (file: string, content: string): Rule[] => phrasesOf(file, content).flatMap(checkPhrase).map((v) => v.rule);
const label = (text: string): Phrase => ({ file: "x.vue", line: 1, text, place: "label" });
const copy = (text: string): Phrase => ({ file: "x.vue", line: 1, text, place: "copy" });
const rules = (p: Phrase): Rule[] => checkPhrase(p).map((v) => v.rule);

describe("MACHINE_WORD", () => {
  it.each([
    "Connect another computer to this Fleet.",
    "Enter the code from your computer",
    "Default: nodes when idle, Fleets ask",
    "Pair the phone with that Fleet, and add this node to it.",
    "Fleet can't install GitHub Copilot on a riscv computer.",
  ])("flags %j", (text) => {
    expect(rules(copy(text))).toEqual(["MACHINE_WORD"]);
  });

  it.each([
    "Run fleet node up on the other machine, then paste its link.",
    "Fleet node on 10.0.0.4:6262, a machine without the web app.",
    "Mods need Node.js 22 or Bun.",
    "Embedded resource node/node-page.html is missing.",
    "On studio-pc, journalctl --user -u fleet-node says why.",
    "{x}.nodes needs one or more steps.",
    "Open on my computer",
    "Link copied. Open it on the computer.",
    "Skip the phone while Fleet is open on a computer",
    "Three fleets of ships",
  ])("allows %j", (text) => {
    expect(rules(copy(text))).toEqual([]);
  });
});

describe("THREE_DOTS", () => {
  it.each(["Search sessions...", "Loading...", "sk-ant-...", "..."])("flags %j", (text) => {
    expect(rules(copy(text))).toEqual(["THREE_DOTS"]);
  });

  it.each(["Search sessions…", "Loading…"])("allows %j", (text) => {
    expect(rules(copy(text))).toEqual([]);
  });

  it("reads a truncation in script code, but not a spread", () => {
    const ts = [
      "export const short = (t: string) => (t.length > 80 ? t.slice(0, 79) + \"...\" : t);",
      "export const merged = { ...base, ...extra };",
      "const list = [...items];",
    ].join("\n");
    expect(rulesOf("src/lib/short.ts", ts)).toEqual(["THREE_DOTS"]);
  });
});

describe("RETRY_LABEL", () => {
  it.each(["Retry", "retry", "Retry…"])("flags a %j label", (text) => {
    expect(rules(label(text))).toEqual(["RETRY_LABEL"]);
  });

  it.each(["Retry 2/3", "Retrying", "Try again", "Retry failed webhooks"])("allows %j", (text) => {
    expect(rules(label(text))).toEqual([]);
  });
});

describe("TITLE_CASE", () => {
  it.each(["New Project", "Add Lane", "Get Started", "Configure MCP Server", "Delete Project", "Confirm Delete", "Enter Code", "Environment Variables (KEY=VALUE per line)"])("flags %j", (text) => {
    expect(isTitleCase(text)).toBe(true);
    expect(rules(label(text))).toEqual(["TITLE_CASE"]);
  });

  it.each([
    "New project",
    "Open Settings → Labs",
    "Go to Sessions",
    "Open #12 on GitHub",
    "Show in Context",
    "Welcome to Weave Fleet",
    "Open in VS Code",
    "Install Claude Code",
    "Solarized Dark", // a theme's name
    "Web Fetch", // a tool's name
    "Clear (Esc)",
    "Delete",
  ])("allows %j", (text) => {
    expect(rules(label(text))).toEqual([]);
  });

  it("only reads labels, not copy", () => {
    expect(rules(copy("Pull Request Merged"))).toEqual([]);
  });
});

describe("what it reads in a .vue file", () => {
  const sfc = `<script setup lang="ts">
// A comment that says Retry and computer... isn't read.
import { toast } from "@/lib/toast";
const options = [{ label: "Add Lane", value: "lane" }];
function fail() {
  toast.error("Couldn't reach the computer");
}
const machine = computed(() => node.value);
</script>

<template>
  <div>
    <!-- Retry: a comment -->
    <Button @click="retry">Retry</Button>
    <Button><span>Try again</span></Button>
    <DropdownMenuItem>Open In New Tab</DropdownMenuItem>
    <p>Fleet runs on this computer.</p>
    <Input placeholder="Search sessions..." />
    <Button :title="busy ? 'Saving...' : 'Save'">Save</Button>
    <h2>{{ count }} Open Pull Requests</h2>
    <p class="text-muted">Retry 2/3</p>
  </div>
</template>
`;
  const found = phrasesOf("src/components/X.vue", sfc).flatMap(checkPhrase);

  it("finds each problem once, on its own line", () => {
    expect(found.map((v) => `${v.rule}:${v.line}`).sort()).toEqual(
      [
        "TITLE_CASE:4", // label: "Add Lane"
        "MACHINE_WORD:6", // toast
        "RETRY_LABEL:14",
        "TITLE_CASE:16", // the menu item
        "MACHINE_WORD:17",
        "THREE_DOTS:18",
        "THREE_DOTS:19",
        "TITLE_CASE:20", // the heading
      ].sort(),
    );
  });

  it("doesn't read comments, identifiers or class names", () => {
    expect(found.every((v) => v.line !== 2 && v.line !== 8 && v.line !== 13)).toBe(true);
  });
});

describe("what it reads in a .tsx file", () => {
  const tsx = `// The pairing screen: scan the code the computer shows... (a comment)
import { useState } from "react";
export function Pair() {
  const [code, setCode] = useState<string>("");
  return (
    <Screen title="Pair Your Phone">
      <Text tone="muted">In Fleet on the machine, open Settings → Machines.</Text>
      <Button label="Retry" onPress={() => setCode(code.length > 2 ? "" : code)} />
      <Text>Ask the computer for a new code.</Text>
    </Screen>
  );
}
`;

  it("reads JSX text and props, not comments or generics", () => {
    const found = phrasesOf("mobile/src/app/pair.tsx", tsx).flatMap(checkPhrase);
    expect(found.map((v) => `${v.rule}:${v.line}`).sort()).toEqual(["MACHINE_WORD:9", "RETRY_LABEL:8", "TITLE_CASE:6"].sort());
  });
});

describe("the server's sentences", () => {
  const cs = `namespace X;
public static class Messages
{
    // The computer... a comment
    public const string Gone = "Ask for a new code on the computer.";
    public const string Wait = "Waiting...";
    public const string Route = "/api/nodes/{id}";
    public static void Log(ILogger logger) => logger.LogInformation("Node {Name} started on this computer.", "x");
    public static string Retry => "Retry";
}
`;

  it("checks sentences for the word rules only, and skips log lines and routes", () => {
    const found = serverPhrases("src/X.cs", cs).flatMap(checkPhrase);
    expect(found.map((v) => `${v.rule}:${v.line}`)).toEqual(["MACHINE_WORD:5", "THREE_DOTS:6"]);
  });
});
