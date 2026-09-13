import type { ConnectTerminalOptions, TerminalConnection } from "@/lib/terminal-socket";

/**
 * A pretend shell for Vite mock mode, which has no server to run one. It knows
 * a few commands (ls, pwd, git status, clear, echo, npx vitest run sessions,
 * npm run dev:mock) so the drawer can be styled and tried without Fleet.
 */

const ESC = String.fromCharCode(27);
const CTRL_C = String.fromCharCode(3);
const CTRL_L = String.fromCharCode(12);
const DELETE = String.fromCharCode(127);
const sgr = (code: string) => `${ESC}[${code}m`;
const RESET = sgr("0");
const bold = (text: string) => `${sgr("1")}${text}${RESET}`;
const color = (code: string, text: string) => `${sgr(code)}${text}${RESET}`;
const dim = (text: string) => color("2", text);

const PROMPT = `${color("1;34", "client")} ${color("35", "feat/session-push")} ${color("32", "❯")} `;

const STATUS = [
  ` ${color("33", "M")} src/composables/use-sessions.ts`,
  ` ${color("33", "M")} src/stores/sessions.ts`,
  `${color("31", "??")} src/composables/use-session-push.ts`,
];

const TEST_RUN = [
  "",
  ` ${color("1;36", "RUN")}  ${color("36", "v4.1.2")} ${dim("~/source/weave-fleet/client")}`,
  "",
  ` ${color("32", "✓")} src/stores/__tests__/sessions.test.ts ${dim("(9 tests) 41ms")}`,
  ` ${color("33", "❯")} src/composables/__tests__/use-sessions.test.ts ${dim("(14 tests |")} ${color("31", "1 failed")}${dim(")")} ${dim("88ms")}`,
  `   ${color("31", "×")} drops the 15s poll once the hub pushes ${dim("12ms")}`,
  "",
  ` ${color("1;41", "FAIL")} ${dim("use-sessions.test.ts >")} drops the 15s poll once the hub pushes`,
  `${color("1;31", "AssertionError")}${color("31", ': expected "fetch" to be called 1 times, but got 2 times')}`,
  ` ${color("36", "❯")} ${dim("src/composables/__tests__/")}use-sessions.test.ts${dim(":212:31")}`,
  "",
  ` ${dim("Test Files")}  ${color("1;31", "1 failed")} ${dim("|")} ${color("32", "1 passed")} ${dim("(2)")}`,
  `      ${dim("Tests")}  ${color("1;31", "1 failed")} ${dim("|")} ${color("32", "22 passed")} ${dim("(23)")}`,
  `   ${dim("Duration")}  1.94s`,
];

const DEV_SERVER = [
  "",
  dim("> weave-fleet-client@0.14.0 dev:mock"),
  dim("> vite --port 3002 --mode mock"),
  "",
  `  ${color("1;32", "VITE")} ${color("32", "v8.0.4")}  ${dim("ready in")} ${bold("412")} ${dim("ms")}`,
  "",
  `  ${color("32", "➜")}  ${bold("Local")}:   ${color("36", "http://localhost:3002/")}`,
  `  ${color("32", "➜")}  ${dim("Network: use")} ${bold("--host")} ${dim("to expose")}`,
  `  ${color("32", "➜")}  ${dim("press")} ${bold("h + enter")} ${dim("to show help")}`,
];

/**
 * Everything each mock terminal has printed, so reopening it replays its
 * output the way the server replays saved scrollback.
 */
const transcripts = new Map<string, string>();

/** The first mock terminal in a session starts with some output, so the drawer isn't empty. */
function greeting(terminalId: string): string {
  if (!terminalId.endsWith("-1")) return "";
  return [`${PROMPT}git status --short`, ...STATUS].map((text) => `${text}\r\n`).join("");
}

export function createMockTerminalConnection(options: ConnectTerminalOptions): TerminalConnection {
  const { handlers } = options;
  const encoder = new TextEncoder();
  let input = "";
  let running: "dev" | null = null;
  let disposed = false;
  const { terminalId } = options;

  const emit = (text: string) => {
    if (disposed) return;
    transcripts.set(terminalId, (transcripts.get(terminalId) ?? "") + text);
    handlers.onOutput(encoder.encode(text));
  };
  const line = (text: string) => emit(`${text}\r\n`);

  function run(command: string): void {
    const text = command.trim();
    if (!text) return;
    if (text === "clear") {
      emit(`${ESC}[2J${ESC}[H`);
      return;
    }
    if (text === "ls") {
      line(`${color("1;34", "node_modules")}  package.json  ${color("1;34", "public")}  ${color("1;34", "src")}  vite.config.ts  vitest.config.ts`);
      return;
    }
    if (text === "pwd") {
      line("/home/dev/source/weave-fleet/client");
      return;
    }
    if (text.startsWith("git status")) {
      STATUS.forEach(line);
      return;
    }
    if (/^(npx vitest|npm (run )?test|bun run test)/.test(text)) {
      TEST_RUN.forEach(line);
      return;
    }
    if (/^(npm|bun) run dev/.test(text)) {
      running = "dev";
      DEV_SERVER.forEach(line);
      return;
    }
    if (text === "exit") {
      disposed = true;
      handlers.onExit(0);
      handlers.onStatus("ended");
      return;
    }
    if (text.startsWith("echo")) {
      line(text.slice(5));
      return;
    }
    line(`zsh: command not found: ${text.split(/\s+/)[0]}`);
  }

  function receive(data: string): void {
    for (const ch of data) {
      if (disposed) return;
      if (ch === CTRL_C) {
        emit("^C\r\n");
        input = "";
        running = null;
        emit(PROMPT);
        continue;
      }
      if (running) continue;
      if (ch === "\r") {
        emit("\r\n");
        const command = input;
        input = "";
        run(command);
        if (!disposed && !running) emit(PROMPT);
        continue;
      }
      if (ch === DELETE) {
        if (input) {
          input = input.slice(0, -1);
          emit("\b \b");
        }
        continue;
      }
      if (ch === CTRL_L) {
        emit(`${ESC}[2J${ESC}[H${PROMPT}${input}`);
        continue;
      }
      if (ch >= " ") {
        input += ch;
        emit(ch);
      }
    }
  }

  // Let the view mount before the "connection" opens, like a real socket.
  queueMicrotask(() => {
    if (disposed) return;
    handlers.onStatus("connecting");
    handlers.onReset();
    const saved = transcripts.get(terminalId);
    if (saved === undefined) {
      emit(greeting(terminalId) + PROMPT);
    } else {
      handlers.onOutput(encoder.encode(saved));
    }
    handlers.onStatus("open");
    handlers.onReady();
  });

  return {
    write(data) {
      receive(typeof data === "string" ? data : new TextDecoder().decode(data));
    },
    resize() {},
    clear() {
      transcripts.set(terminalId, PROMPT + input);
      handlers.onCleared();
    },
    dispose() {
      disposed = true;
    },
  };
}
