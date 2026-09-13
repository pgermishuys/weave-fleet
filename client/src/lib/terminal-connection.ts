import { createMockTerminalConnection } from "@/lib/terminal-mock-shell";
import { connectTerminal, type ConnectTerminalOptions, type TerminalConnection } from "@/lib/terminal-socket";

/** Connects a terminal view: the real socket, or the pretend shell in Vite mock mode (which has no server). */
export function openTerminalConnection(options: ConnectTerminalOptions): TerminalConnection {
  return import.meta.env.MODE === "mock" ? createMockTerminalConnection(options) : connectTerminal(options);
}
