const NAME_PART = "[a-z][a-z0-9-]{0,63}";
const SESSION_PART = "[A-Za-z0-9_-]{1,128}";

/** A mod's name: 1 to 64 lowercase letters, digits and "-", starting with a letter. The client applies the same rule. */
export const MOD_NAME = new RegExp(`^${NAME_PART}$`);
/** A session id as a draft's id carries it: 1 to 128 letters, digits, "_" and "-". */
export const SESSION_ID = new RegExp(`^${SESSION_PART}$`);
/** A mod's id: `name@v<n>` (n from 1, no leading zero) or `name@draft:<sessionId>`. */
export const MOD_ID = new RegExp(`^${NAME_PART}@(?:v[1-9][0-9]*|draft:${SESSION_PART})$`);

export type ParsedModId = { name: string; version: number } | { name: string; draft: string };

/** Splits a mod id into its parts, or undefined when it isn't one. */
export function parseModId(id: string): ParsedModId | undefined {
  if (!MOD_ID.test(id)) return undefined;
  const at = id.indexOf("@");
  const name = id.slice(0, at);
  const rest = id.slice(at + 1);
  if (rest.startsWith("draft:")) {
    const draft = rest.slice("draft:".length);
    return SESSION_ID.test(draft) ? { name, draft } : undefined;
  }
  const version = Number(rest.slice(1));
  return Number.isSafeInteger(version) ? { name, version } : undefined;
}
