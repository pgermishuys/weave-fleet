/**
 * The one line under a machine in Settings → Machines that says how it can be reached: "Reaches your phone over
 * Tailscale and the home network", "Only on Tailscale", "Off since 08:40". The rules for what is out of reach and why
 * are `phone-reach.ts`'s, so the desktop page and the phone say the same thing.
 *
 * Pure: no Vue, no clock except what is passed in.
 */
import { isTailscale, type MachineAddress } from "@/lib/machine-routes";
import { formatTime } from "@/lib/format";
import { onlyOnTailscale } from "@/lib/phone-reach";
import type { ReachMode } from "@/lib/reach-api";

export interface ReachLineInput {
  name: string;
  /** Whether the machine answers this client now. */
  answering: boolean;
  /** When it stopped answering, if this client saw it happen. */
  offlineSince?: number | null;
  /** The addresses a phone could use to reach it. */
  addresses: readonly Pick<MachineAddress, "kind">[];
  /** For the machine this client runs inside: who the desktop app lets reach it. Missing for the rest. */
  reachMode?: ReachMode | null;
  /** The person's own address, when the mode is `own`. */
  ownAddress?: string | null;
}

export interface ReachLine {
  text: string;
  /** `out` is a machine that doesn't answer, `limited` one a phone away from home can't use, `ok` the rest. */
  tone: "ok" | "limited" | "out";
}

/** What the machine's addresses reach: Tailscale, the home network, or something else. */
function viaWords(addresses: readonly Pick<MachineAddress, "kind">[]): string | null {
  const tailscale = addresses.some((address) => isTailscale(address.kind));
  const home = addresses.some((address) => address.kind === "lan" || address.kind === "hostname");
  const other = addresses.some((address) => address.kind === "other");
  if (tailscale && home) return "over Tailscale and the home network";
  if (tailscale) return "over Tailscale";
  if (home) return "on the home network";
  if (other) return "by its own address";
  return null;
}

export function reachLine(input: ReachLineInput): ReachLine {
  // Not answering is told first, in the phone's words for it (`reachNote`'s "Off since 08:40"); "only on Tailscale" is for a
  // machine that answers here but not from a phone away from home.
  if (!input.answering) return { text: input.offlineSince ? `Off since ${formatTime(input.offlineSince)}` : "Not reachable", tone: "out" };
  if (input.reachMode === "off") return { text: "Only this machine", tone: "limited" };
  if (input.reachMode === "own") return { text: input.ownAddress ? `Reaches your phone at ${input.ownAddress}` : "Reaches your phone at your own address", tone: "ok" };
  if (input.reachMode === "home") return { text: "Reaches your phone on the home network", tone: "ok" };
  if (onlyOnTailscale(input.addresses)) return { text: "Only on Tailscale", tone: "limited" };
  const via = viaWords(input.addresses);
  return via ? { text: `Reaches your phone ${via}`, tone: "ok" } : { text: "Only this machine", tone: "limited" };
}
