/**
 * Away from home: why a phone can't reach a machine, and the one thing to do about it.
 *
 * A machine that only has Tailscale addresses (a cloud server) answers a phone only while that phone is on the
 * tailnet. When it doesn't, the phone says so: "Out of reach from this phone: atlas is only on Tailscale", with one
 * button, kept apart from a machine that simply went offline ("Off since 08:40"). The phone can't ask Tailscale
 * whether it is signed in, so it infers it from which addresses answer: any machine that answered over a Tailscale
 * address means this phone is on the tailnet. Whether Tailscale is installed is inferred the same way: a phone that
 * once reached a machine over Tailscale has the app, so the button says "Turn on Tailscale"; one that never did is
 * told to "Get Tailscale".
 *
 * Pure: no Vue, no React Native, no clock except what is passed in. Used by the phone app and the phone web app.
 */
import { formatTime } from "@/lib/format";
import { isTailscale, kindOf, type MachineAddress } from "@/lib/machine-routes";

/** What the phone can tell about itself and Tailscale. */
export type PhoneTailnet = "on" | "off" | "unknown";

/** The state of a machine's connection, as the phone's hub reports it. */
export type LinkState = "connecting" | "connected" | "reconnecting" | "offline" | "signed-out";

/** What the phone knows about one machine's reach. */
export interface ReachInput {
  machineName: string;
  state: LinkState;
  addresses: readonly MachineAddress[];
  /** When the connection went down, as a timestamp, when the phone saw it happen. */
  offlineSince?: number | null;
}

export type TailscaleAction = "get" | "turn-on";

export interface ReachNote {
  /** `tailscale-only`: the phone can't get there without Tailscale. `offline`: the machine, or the network, is down. */
  kind: "tailscale-only" | "offline";
  /** The line under the machine's name: "Out of reach from this phone" or "Off since 08:40". */
  headline: string;
  /** Why, in a sentence: "atlas is only on Tailscale". */
  detail: string | null;
  /** The one button, only for `tailscale-only`. */
  action: { kind: TailscaleAction; label: string } | null;
}

/** True when every address the machine has is Tailscale's, so nothing else can ever reach it. */
export function onlyOnTailscale(addresses: readonly Pick<MachineAddress, "kind">[]): boolean {
  return addresses.length > 0 && addresses.every((address) => isTailscale(address.kind));
}

/** The same, for the addresses in a pairing code. */
export function urlsOnlyOnTailscale(urls: readonly string[]): boolean {
  return onlyOnTailscale(urls.map((url) => ({ kind: kindOf(url) })));
}

/**
 * Whether this phone is on the tailnet, from the machines it talks to: on when one answered over a Tailscale address.
 * Off when machines have Tailscale addresses, none answered and the phone reaches another machine some other way
 * (so the phone is up, and Tailscale is what's missing). Unknown otherwise: with nothing else answering, a dead
 * machine and a phone without Tailscale look the same.
 */
export function phoneTailnet(machines: readonly Pick<ReachInput, "state" | "addresses">[]): PhoneTailnet {
  const answeredOnTailnet = machines.some((machine) => machine.addresses.some((address) => isTailscale(address.kind) && address.lastResult === "ok"));
  if (answeredOnTailnet) return "on";
  const reachedElsewhere = machines.some((machine) => machine.state === "connected" && machine.addresses.some((address) => !isTailscale(address.kind) && address.lastResult === "ok"));
  const triedTailnet = machines.some((machine) => machine.addresses.some((address) => isTailscale(address.kind) && address.lastResult === "no-answer"));
  return reachedElsewhere && triedTailnet ? "off" : "unknown";
}

/**
 * The note for one machine, or null while it is connected, connecting, or the machine turned this phone away
 * ("signed-out" has its own words). `everOnTailnet` is true when this phone has reached something over Tailscale
 * before, so the app is there and only switched off.
 */
export function reachNote(machine: ReachInput, tailnet: PhoneTailnet, everOnTailnet: boolean): ReachNote | null {
  if (machine.state === "connected" || machine.state === "connecting" || machine.state === "signed-out") return null;
  if (tailnet !== "on" && onlyOnTailscale(machine.addresses)) {
    const kind: TailscaleAction = everOnTailnet ? "turn-on" : "get";
    return {
      kind: "tailscale-only",
      headline: "Out of reach from this phone",
      detail: `${machine.machineName} is only on Tailscale`,
      action: { kind, label: kind === "get" ? "Get Tailscale" : "Turn on Tailscale" },
    };
  }
  if (machine.state === "reconnecting") return null;
  return {
    kind: "offline",
    headline: machine.offlineSince ? `Off since ${formatTime(machine.offlineSince)}` : "Not reachable",
    detail: null,
    action: null,
  };
}

/** The warning before pairing a machine that only has Tailscale addresses with a phone that isn't on the tailnet. */
export interface PairWarning {
  title: string;
  body: string;
  action: { kind: TailscaleAction; label: string };
}

export function pairWarning(machineName: string, urls: readonly string[], tailnet: PhoneTailnet, everOnTailnet: boolean): PairWarning | null {
  if (tailnet === "on" || !urlsOnlyOnTailscale(urls)) return null;
  const kind: TailscaleAction = everOnTailnet ? "turn-on" : "get";
  return {
    title: `${machineName} is only on Tailscale`,
    body: kind === "get"
      ? `This phone can't reach it without Tailscale. Install Tailscale, sign in with the same account as ${machineName}, then connect.`
      : `This phone can't reach it while Tailscale is off. Turn Tailscale on, then connect.`,
    action: { kind, label: kind === "get" ? "Get Tailscale" : "Turn on Tailscale" },
  };
}

/** Where "Get Tailscale" and "Turn on Tailscale" go: the store page, which offers Open when the app is already there. */
export function tailscaleLink(platform: "ios" | "android" | "web" | string): string {
  if (platform === "ios") return "https://apps.apple.com/app/tailscale/id1470499037";
  if (platform === "android") return "https://play.google.com/store/apps/details?id=com.tailscale.ipn";
  return "https://tailscale.com/download";
}
