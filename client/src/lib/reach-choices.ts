/**
 * The choices in "Let others reach studio?", worked out from the addresses Fleet found on this machine. No Vue.
 */
import type { ReachAddress, ReachMode } from "@/lib/reach-api";

export interface ReachChoiceAddress {
  url: string;
  kind: ReachAddress["kind"];
  /** The host as shown: `studio.tail4f2a.ts.net`, `192.168.1.20`. */
  label: string;
}

export interface ReachChoice {
  mode: Exclude<ReachMode, "off">;
  label: string;
  recommended: boolean;
  /** Nothing to listen on for this choice. */
  disabled: boolean;
  /** What it means, in a sentence. */
  hint: (machineName: string) => string;
  /** What it will listen on. */
  addresses: ReachChoiceAddress[];
}

function hostOf(url: string): string {
  try {
    return new URL(url).hostname;
  } catch {
    return url;
  }
}

function isIp(host: string): boolean {
  return /^\d{1,3}(\.\d{1,3}){3}$/.test(host);
}

export function reachChoices(found: readonly ReachAddress[]): ReachChoice[] {
  const shown = found
    .filter((address) => address.kind === "tailnet" || address.kind === "lan")
    .map((address): ReachChoiceAddress => ({ url: address.url, kind: address.kind, label: hostOf(address.url) }));
  const lan = shown.filter((address) => address.kind === "lan");
  const tailnet = shown.filter((address) => address.kind === "tailnet");
  // Tailscale's name says more than its IP, so the IP only shows when there is no name.
  const named = tailnet.filter((address) => !isIp(address.label));
  const tailnetShown = named.length ? named : tailnet;

  return [
    {
      mode: "tailscale-and-home",
      label: "Tailscale and home network",
      recommended: tailnet.length > 0,
      disabled: tailnet.length + lan.length === 0,
      hint: (machine) => tailnet.length
        ? `Your phone reaches ${machine} at home and away.`
        : `Tailscale wasn't found on this machine. Until it is, your phone reaches ${machine} at home only.`,
      addresses: [...tailnetShown, ...lan],
    },
    {
      mode: "home",
      label: "Home network only",
      recommended: false,
      disabled: lan.length === 0,
      hint: (machine) => lan.length
        ? `At home only. Away from home, the phone shows ${machine} as out of reach.`
        : "Fleet didn't find a home network address on this machine.",
      addresses: lan,
    },
    {
      mode: "own",
      label: "My own address",
      recommended: false,
      disabled: false,
      hint: () => "A tunnel or a domain you run, such as https://fleet.example.com.",
      addresses: [],
    },
  ];
}
