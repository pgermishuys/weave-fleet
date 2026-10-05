/**
 * Pairing a phone with a machine: what the QR code carries and how each side reads it.
 *
 * The computer asks its Fleet for a one-time code (`POST /api/machine/pairing`) and shows the URL it gets back as
 * a QR code: `<phone base>/pair#p=<base64url(JSON)>`. The JSON is versioned and app-neutral, so a native app's
 * scanner can read the same code. The secret sits in the fragment, which browsers never send to a server.
 *
 * No Vue here: the pairing page, Settings and (later) a native wrapper share it.
 */

/** Version 1 of what a pairing QR code carries. */
export interface PairingPayloadV1 {
  v: 1;
  machineId: string;
  machineName: string;
  /** The machine's base URL as the phone should reach it, e.g. `https://hangar.tail9c2e.ts.net`. */
  url: string;
  /** The one-time secret. */
  secret: string;
}

function toBase64Url(text: string): string {
  const bytes = new TextEncoder().encode(text);
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function fromBase64Url(value: string): string {
  const padded = value.replace(/-/g, "+").replace(/_/g, "/") + "=".repeat((4 - (value.length % 4)) % 4);
  const binary = atob(padded);
  const bytes = Uint8Array.from(binary, (char) => char.charCodeAt(0));
  return new TextDecoder().decode(bytes);
}

/** The fragment value for `payload`: base64url of its JSON. */
export function encodePairingPayload(payload: PairingPayloadV1): string {
  return toBase64Url(JSON.stringify(payload));
}

/** The pairing URL a QR code shows. */
export function pairingUrl(payload: PairingPayloadV1): string {
  return `${payload.url}/pair#p=${encodePairingPayload(payload)}`;
}

/**
 * Reads a pairing payload from a URL fragment (`#p=…`, with or without the `#`). Null when there's none, or it
 * isn't version 1, or a field is missing.
 */
export function decodePairingFragment(hash: string): PairingPayloadV1 | null {
  const params = new URLSearchParams(hash.replace(/^#/, ""));
  const encoded = params.get("p");
  if (!encoded) return null;

  let parsed: unknown;
  try {
    parsed = JSON.parse(fromBase64Url(encoded));
  } catch {
    return null;
  }

  if (!parsed || typeof parsed !== "object") return null;
  const candidate = parsed as Record<string, unknown>;
  if (candidate.v !== 1) return null;
  for (const field of ["machineId", "machineName", "url", "secret"] as const) {
    if (typeof candidate[field] !== "string" || !(candidate[field] as string).length) return null;
  }
  if (!/^https?:\/\//i.test(candidate.url as string)) return null;

  return {
    v: 1,
    machineId: candidate.machineId as string,
    machineName: candidate.machineName as string,
    url: candidate.url as string,
    secret: candidate.secret as string,
  };
}

/** Whether `host` (a hostname, no port) only means this computer. */
export function isLoopbackHost(host: string): boolean {
  const bare = host.replace(/^\[|\]$/g, "").toLowerCase();
  return bare === "localhost" || bare.endsWith(".localhost") || bare === "::1" || /^127\./.test(bare);
}

/**
 * The address the phone should open: the one someone saved for this machine, else the page's own origin when a
 * phone could reach it (not loopback), else the first https address Fleet knows, else the first address at all.
 * Null when there's nothing a phone could use.
 */
export function choosePhoneBaseUrl(
  publicUrl: string | null | undefined,
  location: { origin: string; hostname: string },
  addresses: readonly { url: string }[],
): string | null {
  if (publicUrl) return publicUrl.replace(/\/+$/, "");
  if (!isLoopbackHost(location.hostname)) return location.origin;
  const https = addresses.find((address) => address.url.startsWith("https://"));
  return (https ?? addresses[0])?.url.replace(/\/+$/, "") ?? null;
}

/** Whether a phone can install Fleet and get notifications from `url`: only over https. */
export function supportsInstall(url: string): boolean {
  return /^https:\/\//i.test(url);
}

/** Pulls the parts out of what someone typed as a manual code; returns `XXXX-XXXX` or null. */
export function normalizeManualCode(typed: string): string | null {
  const compact = typed.toUpperCase().replace(/[\s-]/g, "").replace(/[IL]/g, "1").replace(/O/g, "0");
  if (!/^[0-9A-HJKMNP-TV-Z]{8}$/.test(compact)) return null;
  return `${compact.slice(0, 4)}-${compact.slice(4)}`;
}

/** `ios`, `android` or `other`, from a user agent. iPadOS reports itself as a Mac but has touch. */
export function guessPlatform(userAgent: string, maxTouchPoints = 0): "ios" | "android" | "other" {
  if (/iPhone|iPad|iPod/.test(userAgent)) return "ios";
  if (/Macintosh/.test(userAgent) && maxTouchPoints > 1) return "ios";
  if (/Android/.test(userAgent)) return "android";
  return "other";
}

/** A first guess at what to call this phone: its model where the user agent says (Android), else its kind. */
export function guessDeviceName(userAgent: string, maxTouchPoints = 0): string {
  if (/iPad/.test(userAgent) || (/Macintosh/.test(userAgent) && maxTouchPoints > 1)) return "iPad";
  if (/iPhone/.test(userAgent)) return "iPhone";
  const android = /Android [\d.]+; ([^;)]+?)(?: Build\/[^;)]*)?\)/.exec(userAgent);
  if (android) {
    const model = android[1].trim();
    // Chrome's reduced user agent hides the model behind "K".
    return model && model !== "K" ? model : "Android phone";
  }
  if (/Android/.test(userAgent)) return "Android phone";
  return "Phone";
}
