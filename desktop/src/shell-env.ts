import { execFile } from "node:child_process";

/**
 * Apps started from the Dock, Finder or a desktop menu don't get the PATH your shell sets up, so Fleet wouldn't
 * find `opencode`, `node` or `git` from Homebrew, nvm or ~/.local/bin. Ask a login shell once, the way t3code's
 * DesktopShellEnvironment does, and merge what it says into the app's environment before starting Fleet.
 */
export const SHELL_ENV_NAMES = [
  "PATH",
  "SSH_AUTH_SOCK",
  "LANG",
  "LC_ALL",
  "LC_CTYPE",
  "HOMEBREW_PREFIX",
  "HOMEBREW_CELLAR",
  "HOMEBREW_REPOSITORY",
] as const;

const TIMEOUT_MS = 5000;
const marker = (name: string, edge: "START" | "END") => `__FLEET_ENV_${name}_${edge}__`;

/** A command that prints each variable between markers, so shell startup noise doesn't matter. */
export function captureCommand(names: readonly string[]): string {
  return names
    .map((name) => `printf '%s\\n' '${marker(name, "START")}'; printenv ${name} || true; printf '%s\\n' '${marker(name, "END")}'`)
    .join("; ");
}

export function parseCaptured(output: string, names: readonly string[]): Record<string, string> {
  const result: Record<string, string> = {};
  for (const name of names) {
    const start = output.indexOf(marker(name, "START"));
    const end = output.indexOf(marker(name, "END"));
    if (start < 0 || end < start) continue;
    const value = output.slice(start + marker(name, "START").length, end).trim();
    if (value) result[name] = value;
  }
  return result;
}

/** The shell's PATH first, then entries only the app's own PATH had. */
export function mergePath(shellPath: string | undefined, appPath: string | undefined, delimiter: string): string | undefined {
  const entries = [...(shellPath?.split(delimiter) ?? []), ...(appPath?.split(delimiter) ?? [])].filter(Boolean);
  if (entries.length === 0) return undefined;
  return [...new Set(entries)].join(delimiter);
}

export function readLoginShellEnv(shell: string): Promise<Record<string, string>> {
  return new Promise((resolve) => {
    execFile(
      shell,
      ["-ilc", captureCommand(SHELL_ENV_NAMES)],
      { timeout: TIMEOUT_MS, encoding: "utf8", maxBuffer: 1024 * 1024, env: { ...process.env, TERM: "dumb" } },
      (_error, stdout) => resolve(parseCaptured(stdout ?? "", SHELL_ENV_NAMES)),
    );
  });
}

/** Merges the login shell's environment into `env`. On Windows, apps already get the user's PATH. */
export async function installLoginShellEnv(env: NodeJS.ProcessEnv, platform: NodeJS.Platform): Promise<void> {
  if (platform === "win32") return;
  const shell = env.SHELL || (platform === "darwin" ? "/bin/zsh" : "/bin/bash");
  const captured = await readLoginShellEnv(shell);
  const path = mergePath(captured.PATH, env.PATH, ":");
  if (path) env.PATH = path;
  for (const name of SHELL_ENV_NAMES) {
    if (name !== "PATH" && !env[name] && captured[name]) env[name] = captured[name];
  }
}
