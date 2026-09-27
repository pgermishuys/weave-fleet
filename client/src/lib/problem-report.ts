import { apiFetch } from "@/lib/api-client";
import { extractApiError } from "@/lib/api-error";
import { getDesktopBridge } from "@/lib/desktop";
import { loadMachines } from "@/lib/machines";

/**
 * Help → Report a problem. Everything here is gathered when someone opens the report, never ahead of time: the
 * screenshot, where they were, and the connection as it is right now. The server adds versions and its log, and
 * replaces private details before anything is shown (POST /api/reports/prepare).
 */

export type ReportKind = "bug" | "looks-wrong" | "idea";

export const REPORT_KINDS: readonly { id: ReportKind; label: string }[] = [
  { id: "bug", label: "Something broke" },
  { id: "looks-wrong", label: "Looks wrong" },
  { id: "idea", label: "Idea" },
];

/** Where the report was opened from; it only changes the wording of the footer. */
export type ReportSource = "help" | "palette" | "failure" | "desktop-menu";

export interface ReportFact {
  name: string;
  value: string;
}

export interface ReportIncludes {
  environment: boolean;
  where: boolean;
  connection: boolean;
  log: boolean;
}

export interface ReportClientContext {
  app: string | null;
  screen: string | null;
  shownStatus: string | null;
  where: ReportFact[];
  connection: ReportFact[];
  privateValues: { kind: "machine" | "url"; value: string }[];
}

export interface PrepareReportRequest {
  kind: ReportKind;
  description: string;
  expected: string | null;
  sessionId: string | null;
  include: ReportIncludes;
  client: ReportClientContext;
}

export interface ReportReplacement {
  label: string;
  kind: string;
  shown: string;
}

export interface PreparedReport {
  title: string;
  body: string;
  log: string | null;
  logEntries: number;
  labels: string[];
  replacements: ReportReplacement[];
  problems: { item: string; reason: string }[];
  canSend: boolean;
}

export interface SendReportRequest {
  kind: ReportKind;
  title: string;
  body: string;
  log: string | null;
  screenshot: string | null;
  contact: string | null;
  labels: string[];
}

export interface Screenshot {
  /** A data: URL (PNG or JPEG). */
  dataUrl: string;
  width: number;
  height: number;
  takenAt: Date;
}

/** Longest a screenshot may take before the report goes ahead without one. */
export const SCREENSHOT_TIME_LIMIT_MS = 2000;

/** The inbox takes images up to 4 MB. */
const MAX_SCREENSHOT_BYTES = 4 * 1024 * 1024;

const LABEL_PATTERN = /‹[a-z]+(?:-\d+)?›/g;

async function post<T>(path: string, body: unknown, fallback: string): Promise<T> {
  const response = await apiFetch(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  if (!response.ok) throw new Error(await errorFrom(response, fallback));
  return (await response.json()) as T;
}

async function errorFrom(response: Response, fallback: string): Promise<string> {
  try {
    const body = (await response.json()) as { error?: unknown };
    return extractApiError(body.error ?? body, fallback);
  } catch {
    return fallback;
  }
}

export function prepareReport(request: PrepareReportRequest): Promise<PreparedReport> {
  return post<PreparedReport>("/api/reports/prepare", request, "Couldn't gather the report. Try again.");
}

export async function sendReport(request: SendReportRequest): Promise<string> {
  const result = await post<{ id: string }>("/api/reports/send", request, "Couldn't send the report. Try again, or use Save as file.");
  return result.id;
}

/** Downloads the report as a zip (report.md, fleet.log, screenshot). */
export async function saveReportFile(request: SendReportRequest): Promise<void> {
  const response = await apiFetch("/api/reports/file", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request),
  });
  if (!response.ok) throw new Error(await errorFrom(response, "Couldn't save the report."));
  const disposition = response.headers.get("content-disposition") ?? "";
  const name = /filename="?([^";]+)"?/.exec(disposition)?.[1] ?? "fleet-report.zip";
  const url = URL.createObjectURL(await response.blob());
  try {
    const link = document.createElement("a");
    link.href = url;
    link.download = name;
    document.body.append(link);
    link.click();
    link.remove();
  } finally {
    setTimeout(() => URL.revokeObjectURL(url), 10_000);
  }
}

/**
 * Takes the window as it is. The desktop app captures its own window; a browser tab draws the page to an image. Either
 * gives up after {@link SCREENSHOT_TIME_LIMIT_MS} and returns null, so a slow page never holds the report up.
 */
export async function captureScreenshot(): Promise<Screenshot | null> {
  const takenAt = new Date();
  const capture = async (): Promise<string | null> => {
    const bridge = getDesktopBridge();
    if (bridge?.captureWindow) return bridge.captureWindow();
    const { domToPng, domToJpeg } = await import("modern-screenshot");
    const options = {
      scale: Math.min(window.devicePixelRatio || 1, 1.5),
      width: window.innerWidth,
      height: window.innerHeight,
      timeout: SCREENSHOT_TIME_LIMIT_MS,
    };
    const png = await domToPng(document.body, options);
    return dataUrlBytes(png) <= MAX_SCREENSHOT_BYTES ? png : domToJpeg(document.body, { ...options, quality: 0.8 });
  };

  try {
    const dataUrl = await withTimeout(capture(), SCREENSHOT_TIME_LIMIT_MS);
    if (!dataUrl || dataUrlBytes(dataUrl) > MAX_SCREENSHOT_BYTES) return null;
    const { width, height } = await imageSize(dataUrl);
    return { dataUrl, width, height, takenAt };
  } catch (error) {
    console.warn("Couldn't take a screenshot for the report:", error);
    return null;
  }
}

function withTimeout<T>(promise: Promise<T>, ms: number): Promise<T | null> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => resolve(null), ms);
    promise.then(
      (value) => {
        clearTimeout(timer);
        resolve(value);
      },
      (error: unknown) => {
        clearTimeout(timer);
        reject(error instanceof Error ? error : new Error(String(error)));
      },
    );
  });
}

function dataUrlBytes(dataUrl: string): number {
  const comma = dataUrl.indexOf(",");
  return Math.floor(((dataUrl.length - comma - 1) * 3) / 4);
}

function imageSize(dataUrl: string): Promise<{ width: number; height: number }> {
  return new Promise((resolve) => {
    const image = new Image();
    image.onload = () => resolve({ width: image.naturalWidth, height: image.naturalHeight });
    image.onerror = () => resolve({ width: 0, height: 0 });
    image.src = dataUrl;
  });
}

/** "Desktop app 0.38.3 · linux" or the browser's name and major version. */
export function describeApp(userAgent: string = navigator.userAgent): string {
  const bridge = getDesktopBridge();
  if (bridge) return `Desktop app ${bridge.version} · ${bridge.platform}`;
  // Order matters: Edge says Chrome too, and Chrome says Safari.
  const browsers: [string, RegExp][] = [
    ["Edge", /Edg\/(\d+)/],
    ["Firefox", /Firefox\/(\d+)/],
    ["Chrome", /Chrome\/(\d+)/],
    ["Safari", /Version\/(\d+).*Safari/],
  ];
  for (const [name, pattern] of browsers) {
    const version = pattern.exec(userAgent)?.[1];
    if (version) return `${name} ${version}`;
  }
  return "Browser";
}

/** The part of Fleet the person was in, from the address: "Sessions", "Settings", "Automations"… */
export function describeScreen(pathname: string): string {
  const first = pathname.split("/").filter(Boolean)[0];
  if (!first || first === "sessions") return "Sessions";
  return first.charAt(0).toUpperCase() + first.slice(1).replace(/-/g, " ");
}

/** Names and addresses the window knows that must not leave: other machines, and the address Fleet is open at. */
export function clientPrivateValues(origin: string = window.location.origin): ReportClientContext["privateValues"] {
  const values: ReportClientContext["privateValues"] = [];
  for (const machine of loadMachines()) {
    if (machine.name) values.push({ kind: "machine", value: machine.name });
    if (machine.baseUrl) values.push({ kind: "url", value: machine.baseUrl });
  }
  values.push({ kind: "url", value: origin });
  return values;
}

/** The text split into plain runs and labels, for highlighting what was replaced. */
export function splitLabels(text: string): { text: string; label: boolean }[] {
  const parts: { text: string; label: boolean }[] = [];
  let last = 0;
  for (const match of text.matchAll(LABEL_PATTERN)) {
    if (match.index > last) parts.push({ text: text.slice(last, match.index), label: false });
    parts.push({ text: match[0], label: true });
    last = match.index + match[0].length;
  }
  if (last < text.length) parts.push({ text: text.slice(last), label: false });
  return parts;
}

export function countLabels(text: string | null): number {
  return text ? (text.match(LABEL_PATTERN)?.length ?? 0) : 0;
}

const KIND_NAMES: Record<string, [string, string]> = {
  folder: ["folder", "folders"],
  session: ["session name", "session names"],
  branch: ["branch", "branches"],
  user: ["user name", "user names"],
  machine: ["machine or address", "machines or addresses"],
  url: ["address", "addresses"],
  secret: ["token", "tokens"],
  email: ["email address", "email addresses"],
};

/** "3 folders, 1 session name, 1 token". */
export function summarizeReplacements(replacements: readonly ReportReplacement[]): string {
  const counts = new Map<string, number>();
  for (const replacement of replacements) counts.set(replacement.kind, (counts.get(replacement.kind) ?? 0) + 1);
  return Array.from(counts, ([kind, count]) => {
    const [one, many] = KIND_NAMES[kind] ?? [kind, `${kind}s`];
    return `${count} ${count === 1 ? one : many}`;
  }).join(", ");
}
