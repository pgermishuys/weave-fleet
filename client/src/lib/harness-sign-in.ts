import type { HarnessSignInField, HarnessSignInMethod, HarnessSignInProvider } from "@/api/client";
import type { SignInAnswer } from "@/stores/harness-sign-in";

/** Answers as the form holds them, by field key. */
export type SignInAnswers = Record<string, SignInAnswer>;

/** Each field's default, or an empty answer of its kind. */
export function initialAnswers(fields: readonly HarnessSignInField[]): SignInAnswers {
  const answers: SignInAnswers = {};
  for (const field of fields) {
    const fallback = field.default;
    if (field.type === "boolean") answers[field.key] = typeof fallback === "boolean" ? fallback : false;
    else if (field.type === "multiselect") answers[field.key] = Array.isArray(fallback) ? fallback.map(String) : [];
    else if (field.type === "number" || field.type === "integer") answers[field.key] = typeof fallback === "number" ? fallback : "";
    else if (field.type !== "external") {
      answers[field.key] = typeof fallback === "string" ? fallback : field.options?.[0]?.value ?? "";
    }
  }
  return answers;
}

function holds(condition: NonNullable<HarnessSignInField["when"]>[number], answers: SignInAnswers): boolean {
  const same = answers[condition.key] === condition.value;
  return condition.op === "eq" ? same : !same;
}

/** The fields to ask: not hidden, and every condition holds for the answers so far. */
export function visibleFields(fields: readonly HarnessSignInField[], answers: SignInAnswers): HarnessSignInField[] {
  return fields.filter((field) => !field.hidden && (field.when ?? []).every((condition) => holds(condition, answers)));
}

function isEmpty(value: SignInAnswer | undefined): boolean {
  return value === undefined || value === "" || (Array.isArray(value) && value.length === 0);
}

/** The first field that's needed and has no answer, by the name the form shows. */
export function missingAnswer(fields: readonly HarnessSignInField[], answers: SignInAnswers): string | null {
  const missing = visibleFields(fields, answers).find((field) => field.required && field.type !== "external" && isEmpty(answers[field.key]));
  return missing ? missing.title ?? missing.key : null;
}

/** What to send: the asked fields' answers, and hidden fields' defaults. Numbers go as numbers. */
export function answersToSend(fields: readonly HarnessSignInField[], answers: SignInAnswers): SignInAnswers {
  const asked = new Set(visibleFields(fields, answers).map((field) => field.key));
  const sent: SignInAnswers = {};
  for (const field of fields) {
    if (field.type === "external") continue;
    if (field.hidden) {
      if (field.default !== undefined && field.default !== null) sent[field.key] = field.default as SignInAnswer;
      continue;
    }
    const value = answers[field.key];
    if (!asked.has(field.key) || isEmpty(value)) continue;
    sent[field.key] = (field.type === "number" || field.type === "integer") && typeof value === "string" ? Number(value) : value;
  }
  return sent;
}

/** The methods to offer, in V2's order with the environment last: it's advice, not a way to sign in from here. */
export function methodsToOffer(provider: HarnessSignInProvider): HarnessSignInMethod[] {
  return [...provider.methods].sort((a, b) => Number(a.type === "env") - Number(b.type === "env"));
}

/** A key for a method, unique within its provider. */
export function methodKey(method: HarnessSignInMethod): string {
  return `${method.type}:${method.id ?? ""}`;
}

/** The code a device sign-in shows ("Enter code: ABCD-1234"), to copy. */
export function codeToEnter(instructions: string): string | null {
  return /code:\s*([A-Z0-9][A-Z0-9-]{3,})/i.exec(instructions)?.[1] ?? null;
}

const LOOPBACK_HOSTS = new Set(["localhost", "127.0.0.1", "::1", "[::1]"]);

/**
 * Whether this browser runs on another device than Fleet: it reached Fleet by a name other than this computer's own
 * (a phone over Tailscale, another laptop). A sign-in whose provider sends the browser back to `localhost` then
 * can't finish by itself.
 */
export function isRemoteBrowser(hostname: string): boolean {
  const host = hostname.toLowerCase();
  return !LOOPBACK_HOSTS.has(host) && !host.endsWith(".localhost") && !host.startsWith("127.");
}

/** How a provider stands, in a few words: signed in, or using a variable from Fleet's environment. */
export function signInSummary(provider: HarnessSignInProvider): string | null {
  const active = provider.connections.find((connection) => connection.active);
  if (!active) return null;
  return active.kind === "env" ? `From ${active.id}` : "Signed in";
}
