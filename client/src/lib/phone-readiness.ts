/**
 * What a computer needs before a phone can use Fleet from anywhere, in plain words, worked out from what Fleet
 * already knows about itself (`GET /api/machine/access`) and the address the phone will open. Settings → Machines →
 * Add a phone shows it as a short checklist, with the command to run for anything missing. No Vue.
 */

export interface ReadinessItem {
  id: "tailscale" | "https" | "key";
  ok: boolean;
  title: string;
  detail: string;
  /** A command that fixes it, to copy. */
  command?: string;
  link?: { href: string; label: string };
}

export interface ReadinessInput {
  machineName: string;
  /** The address the phone will open (the saved phone address, else Fleet's best guess); null when none is known. */
  phoneUrl: string | null;
  addresses: readonly { url: string; kind?: string }[];
  requiresToken: boolean;
  port: number;
}

function hostOf(url: string | null): string {
  if (!url) return "";
  try {
    return new URL(url).hostname;
  } catch {
    return "";
  }
}

export function phoneReadiness(input: ReadinessInput): ReadinessItem[] {
  const host = hostOf(input.phoneUrl);
  const onTailnet = host.endsWith(".ts.net") || input.addresses.some((address) => address.kind === "tailnet");
  const https = !!input.phoneUrl && input.phoneUrl.toLowerCase().startsWith("https://");

  return [
    {
      id: "tailscale",
      ok: onTailnet,
      title: "Tailscale on this computer and your phone",
      detail: onTailnet
        ? `Your phone reaches ${input.machineName} over your own private network, from anywhere. Nothing goes through Weave.`
        : "A free private network, so your phone reaches this computer from anywhere without opening it to the internet. Install it on both and sign in to the same account.",
      ...(onTailnet ? {} : { link: { href: "https://tailscale.com/download", label: "Get Tailscale" } }),
    },
    {
      id: "https",
      ok: https,
      title: "A secure https:// address",
      detail: https
        ? `Your phone opens ${input.phoneUrl}.`
        : "Phones only install apps and show notifications for https:// addresses. Tailscale gives Fleet one; it may ask you to turn on HTTPS certificates for your network.",
      ...(https ? {} : { command: `tailscale serve --bg --https=443 http://127.0.0.1:${input.port}` }),
    },
    {
      id: "key",
      ok: input.requiresToken,
      title: "Fleet asks every device for a key",
      detail: input.requiresToken
        ? "Only devices you add here get in, and you can remove any of them."
        : "Through tailscale serve every request looks like it comes from this computer, so Fleet has to ask for a key. Start Fleet with:",
      ...(input.requiresToken ? {} : { command: `fleet --port ${input.port} --require-token` }),
    },
  ];
}
