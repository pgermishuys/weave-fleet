/**
 * A one-line description of what an OpenCode profile changes, for pickers and lists: its default model, default
 * agent, and how many providers, MCP servers and agents it adds. OpenCode 2 reads the same file with its own names
 * for some settings (`providers`, `agents`, `mcp.servers`), so both count. Unreadable JSON falls back to a plain
 * note; the harness is what checks a profile for real, when it's saved.
 */
export function profileSummary(content: string): string {
  const config = parseJsonc(content);
  if (!config) return "Can't read this profile";

  const parts: string[] = [];
  const count = (value: unknown): number =>
    value && typeof value === "object" && !Array.isArray(value) ? Object.keys(value).length : 0;
  const plural = (n: number, word: string): string => `${n} ${word}${n === 1 ? "" : "s"}`;

  const mcp = config.mcp && typeof config.mcp === "object" && "servers" in config.mcp ? (config.mcp as { servers: unknown }).servers : config.mcp;
  const providers = count(config.provider) + count(config.providers);
  const agents = count(config.agent) + count(config.agents);

  if (typeof config.model === "string") parts.push(config.model);
  if (typeof config.default_agent === "string") parts.push(`default agent ${config.default_agent}`);
  if (providers) parts.push(plural(providers, "provider"));
  if (count(mcp)) parts.push(plural(count(mcp), "MCP server"));
  if (agents) parts.push(plural(agents, "agent"));
  if (Array.isArray(config.enabled_providers)) parts.push(`only ${config.enabled_providers.join(", ")}`);
  if (Array.isArray(config.disabled_providers)) parts.push(`turns off ${config.disabled_providers.join(", ")}`);
  if (parts.length === 0) {
    const settings = Object.keys(config).filter((key) => key !== "$schema").length;
    parts.push(settings === 0 ? "No settings yet" : plural(settings, "setting"));
  }
  return parts.join(" · ");
}

/** JSON with comments and trailing commas, as opencode.json allows. Null when it doesn't parse to an object. */
export function parseJsonc(text: string): Record<string, unknown> | null {
  try {
    const value: unknown = JSON.parse(stripJsonc(text));
    return value && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

function stripJsonc(text: string): string {
  let out = "";
  let inString = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    const next = text[i + 1];
    if (inString) {
      out += c;
      if (c === "\\") {
        out += next ?? "";
        i++;
      } else if (c === '"') {
        inString = false;
      }
    } else if (c === '"') {
      inString = true;
      out += c;
    } else if (c === "/" && next === "/") {
      while (i < text.length && text[i] !== "\n") i++;
      out += "\n";
    } else if (c === "/" && next === "*") {
      i += 2;
      while (i < text.length && !(text[i] === "*" && text[i + 1] === "/")) i++;
      i++;
    } else {
      out += c;
    }
  }
  return out.replace(/,(\s*[}\]])/g, "$1");
}

/** What a new profile starts with. */
export const NEW_PROFILE_CONTENT = `{
  "$schema": "https://opencode.ai/config.json"
  // Anything here layers over your own opencode.json
}
`;
