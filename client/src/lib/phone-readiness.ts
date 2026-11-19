/**
 * Where a phone will reach a machine, in plain words, worked out from the addresses its pairing code carries (or, before
 * there is a code, from the ones Fleet knows). Settings → Machines → Add your phone shows it as "Your phone will reach
 * studio". No Vue.
 */
import { kindOf } from "@/lib/machine-routes";

export interface ReachLine {
  id: "home" | "tailscale" | "own";
  /** What the phone needs for this one: "At home, on your Wi-Fi". */
  title: string;
  /** The hosts as shown: `192.168.1.20`, `studio.tail4f2a.ts.net`. */
  hosts: string[];
}

function hostOf(url: string): string {
  try {
    return new URL(url).hostname;
  } catch {
    return url;
  }
}

const unique = (hosts: string[]) => hosts.filter((host, index) => hosts.indexOf(host) === index);

/**
 * One line for each way the phone can reach the machine, home network first, then Tailscale, then an address of its own.
 * A line appears only when there is an address behind it. Loopback addresses mean nothing to a phone and are left out.
 */
export function reachLines(urls: readonly string[]): ReachLine[] {
  const byKind = (...kinds: string[]) => urls.filter((url) => kinds.includes(kindOf(url))).map(hostOf);
  const lan = byKind("lan");
  const home = lan.length ? lan : byKind("hostname");
  // Tailscale's name says more than its IP, so the IP only shows when there is no name.
  const tailnet = byKind("tailnet").length ? byKind("tailnet") : byKind("tailnet-ip");
  const own = urls.filter((url) => kindOf(url) === "other" && !/^(localhost|127\.|\[::1\])/i.test(hostOf(url))).map(hostOf);

  const lines: ReachLine[] = [];
  if (home.length) lines.push({ id: "home", title: "At home, on your Wi-Fi", hosts: unique(home) });
  if (tailnet.length) lines.push({ id: "tailscale", title: "Away from home, with Tailscale on the phone", hosts: unique(tailnet) });
  if (own.length) lines.push({ id: "own", title: "From anywhere, at your own address", hosts: unique(own) });
  return lines;
}
