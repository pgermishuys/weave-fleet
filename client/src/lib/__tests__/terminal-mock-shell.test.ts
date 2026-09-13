import { describe, expect, it } from "vitest";
import { createMockTerminalConnection } from "@/lib/terminal-mock-shell";
import type { TerminalConnectionHandlers } from "@/lib/terminal-socket";

const ESC = String.fromCharCode(27);
const CTRL_C = String.fromCharCode(3);

/** Output with colours removed, so tests read like the screen. */
function plain(text: string): string {
  return text.replaceAll(new RegExp(`${ESC}\\[[0-9;]*[A-Za-z]`, "g"), "");
}

async function start(terminalId: string) {
  let output = "";
  const exits: Array<number | null> = [];
  const decoder = new TextDecoder();
  const handlers: TerminalConnectionHandlers = {
    onReset: () => {
      output = "";
    },
    onOutput: (data) => {
      output += decoder.decode(data);
    },
    onReady: () => {},
    onCleared: () => {},
    onExit: (code) => exits.push(code),
    onStatus: () => {},
  };
  const connection = createMockTerminalConnection({ sessionId: "s1", terminalId, cols: 80, rows: 24, handlers });
  await Promise.resolve();
  return { connection, screen: () => plain(output), exits };
}

describe("createMockTerminalConnection", () => {
  it("starts the first terminal with some output and a prompt", async () => {
    const { screen } = await start("mock-a-term-1");

    expect(screen()).toContain("git status --short");
    expect(screen()).toContain("src/stores/sessions.ts");
    expect(screen().trimEnd().endsWith("❯")).toBe(true);
  });

  it("echoes typing and runs the commands it knows", async () => {
    const { connection, screen } = await start("mock-b-term-2");

    connection.write("pwd\r");
    connection.write("npx vitest run sessions\r");
    connection.write("nope\r");

    expect(screen()).toContain("/home/dev/source/weave-fleet/client");
    expect(screen()).toContain('expected "fetch" to be called 1 times');
    expect(screen()).toContain("zsh: command not found: nope");
  });

  it("keeps a dev server running until Ctrl C", async () => {
    const { connection, screen } = await start("mock-c-term-2");

    connection.write("npm run dev:mock\r");
    connection.write("ignored\r");
    expect(screen()).toContain("Local:   http://localhost:3002/");
    expect(screen()).not.toContain("ignored");

    connection.write(CTRL_C);
    connection.write("echo back\r");
    expect(screen()).toContain("back");
  });

  it("replays its output when opened again", async () => {
    const first = await start("mock-d-term-2");
    first.connection.write("echo remembered\r");
    first.connection.dispose();

    const second = await start("mock-d-term-2");

    expect(second.screen()).toContain("remembered");
  });

  it("ends on exit", async () => {
    const { connection, exits } = await start("mock-e-term-2");

    connection.write("exit\r");

    expect(exits).toEqual([0]);
  });
});
