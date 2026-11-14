/**
 * The pairing link another machine prints (`fleet node pair`) and Add a machine reads: `<address>/add-machine#<CODE>`.
 * The address keeps its scheme and port; the code sits in the fragment so it never reaches a server log.
 *
 * No Vue here, like `pairing.ts`.
 */

import { normalizeBaseUrl } from "@/lib/machines";
import { isLoopbackHost, normalizeManualCode } from "@/lib/pairing";

/** What was typed or pasted into the Pairing link field. */
export type PairingInput =
  /** A whole link: the address and the code are both known. */
  | { kind: "link"; baseUrl: string; code: string }
  /** Only an address: the code still has to be typed. */
  | { kind: "address"; baseUrl: string }
  /** Looks like a link but isn't one Fleet can use; `message` says what's wrong. */
  | { kind: "invalid"; message: string };

const LINK_PATH = "/add-machine";

/** Reads the Pairing link field. Null while it's empty. */
export function parsePairingInput(typed: string): PairingInput | null {
  const text = typed.trim();
  if (!text) return null;

  const hashAt = text.indexOf("#");
  const address = hashAt === -1 ? text : text.slice(0, hashAt);
  const fragment = hashAt === -1 ? null : text.slice(hashAt + 1);

  let baseUrl: string;
  try {
    baseUrl = normalizeBaseUrl(address.replace(new RegExp(`${LINK_PATH}/?$`), ""));
  } catch {
    return { kind: "invalid", message: "That isn't a pairing link or an address. A link looks like http://100.64.90.72:2113/add-machine#ABCD-EFGH." };
  }

  if (fragment === null) return { kind: "address", baseUrl };

  const code = normalizeManualCode(fragment);
  if (!code) return { kind: "invalid", message: "That link has no valid code. Run fleet node pair on the other machine for a new one." };
  return { kind: "link", baseUrl, code };
}

/** How `baseUrl` is reached, in a few words: over a tailnet, on this network, or by name. */
export function describeReach(baseUrl: string): string {
  const { hostname, host } = new URL(baseUrl);
  const parts = hostname.split(".").map(Number);
  const [first = 0, second = 0] = parts;
  const isIpv4 = parts.length === 4 && parts.every((part) => Number.isInteger(part));

  if (hostname.endsWith(".ts.net") || (isIpv4 && first === 100 && second >= 64 && second <= 127)) return `over Tailscale at ${host}`;
  if (hostname.endsWith(".local")) return `on your network at ${host}`;
  if (isLoopbackHost(hostname)) return `on this machine at ${host}`;
  if (isIpv4 && (first === 10 || (first === 172 && second >= 16 && second <= 31) || (first === 192 && second === 168))) return `on your network at ${host}`;
  return `at ${host}`;
}
