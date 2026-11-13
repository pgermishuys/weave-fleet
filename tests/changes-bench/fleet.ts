/**
 * Starts a scratch Fleet for the Changes bench, isolated from the user's own Fleet:
 * its own profile folder (HOME, USERPROFILE, APPDATA, LOCALAPPDATA, XDG_*), its own database, its own port, bound to
 * 127.0.0.1, with the test harness instead of a real agent. Every git process Fleet runs logs its argv and duration to
 * <home>/git-trace2.jsonl (GIT_TRACE2_EVENT), which bench.ts reads for server-side git time.
 *
 *   bun run fleet.ts start --bin <publish folder> --home <scratch home> --port 6390
 *   bun run fleet.ts stop --port 6390
 *
 * Never point --home at a real profile.
 */
import { spawn, execSync } from "node:child_process";
import { mkdirSync, openSync } from "node:fs";
import { join, resolve } from "node:path";

function arg(name: string, fallback?: string): string {
  const index = process.argv.indexOf(`--${name}`);
  const value = index >= 0 ? process.argv[index + 1] : fallback;
  if (!value) throw new Error(`--${name} is required`);
  return value;
}

const command = process.argv[2];
const port = Number(arg("port", "6390"));
if (port === 6262) throw new Error("6262 is the real Fleet's port");

if (command === "start") {
  const bin = resolve(arg("bin"));
  const home = resolve(arg("home"));
  if (/\\Users\\[^\\]+$/i.test(home)) throw new Error(`${home} looks like a real profile`);
  for (const dir of ["AppData/Roaming", "AppData/Local", ".config", ".local/share", ".cache", ".local/state", `data-${port}`, "proj"]) {
    mkdirSync(join(home, dir), { recursive: true });
  }
  const env: Record<string, string> = {
    ...(process.env as Record<string, string>),
    HOME: home,
    USERPROFILE: home,
    APPDATA: join(home, "AppData/Roaming"),
    LOCALAPPDATA: join(home, "AppData/Local"),
    XDG_CONFIG_HOME: join(home, ".config"),
    XDG_DATA_HOME: join(home, ".local/share"),
    XDG_CACHE_HOME: join(home, ".cache"),
    XDG_STATE_HOME: join(home, ".local/state"),
    Fleet__Host: "127.0.0.1",
    Fleet__Port: String(port),
    Fleet__DatabasePath: join(home, `data-${port}/fleet.db`),
    Fleet__AnalyticsDatabasePath: join(home, `data-${port}/fleet-analytics.db`),
    FLEET_HARNESS: "test",
    GIT_TRACE2_EVENT: join(home, "git-trace2.jsonl"),
    GIT_TRACE2_EVENT_NESTING: "1",
  };
  const log = openSync(join(home, "fleet.log"), "a");
  const child = spawn("dotnet", [join(bin, "WeaveFleet.Api.dll"), "--harness=test", "--host", "127.0.0.1", "--port", String(port)], {
    cwd: bin,
    env,
    stdio: ["ignore", log, log],
    detached: true,
    windowsHide: true,
  });
  child.unref();
  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`http://127.0.0.1:${port}/healthz`);
      if (response.ok) {
        console.log(JSON.stringify({ pid: child.pid, url: `http://127.0.0.1:${port}` }));
        process.exit(0);
      }
    } catch {
      // not up yet
    }
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error(`Fleet on ${port} didn't answer /healthz; see ${join(home, "fleet.log")}`);
} else if (command === "stop") {
  // Stop by the PID that owns the port, never by name: the user's own Fleet may be running.
  const out = execSync(
    `pwsh -NoProfile -Command "(Get-NetTCPConnection -LocalPort ${port} -State Listen -ErrorAction SilentlyContinue).OwningProcess | Sort-Object -Unique"`,
  ).toString().trim();
  for (const pid of out.split(/\s+/).filter(Boolean)) {
    execSync(`taskkill /F /PID ${pid}`);
    console.log(`stopped ${pid}`);
  }
} else {
  console.log("usage: bun run fleet.ts start --bin <dir> --home <dir> --port <n> | stop --port <n>");
}
