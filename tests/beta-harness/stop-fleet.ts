/**
 * Best-effort kill of any fleet process listening on the configured beta-harness port.
 * Useful when start-fleet.ts crashed or the user pressed Ctrl-C in a way that left
 * the dotnet child orphaned.
 */

import { execSync } from "node:child_process";

const port = process.env.FLEET_PORT ? Number(process.env.FLEET_PORT) : 5099;

function killByPort(p: number): void {
  if (process.platform === "win32") {
    // netstat -ano | findstr :PORT  →  pid in last column
    let output = "";
    try {
      output = execSync(`netstat -ano -p tcp`, { encoding: "utf8" });
    } catch {
      return;
    }
    const pids = new Set<string>();
    for (const line of output.split(/\r?\n/)) {
      const trimmed = line.trim();
      if (!trimmed.startsWith("TCP")) continue;
      const parts = trimmed.split(/\s+/);
      const local = parts[1] ?? "";
      if (!local.endsWith(`:${p}`)) continue;
      const pid = parts[parts.length - 1];
      if (pid && pid !== "0") pids.add(pid);
    }
    for (const pid of pids) {
      try {
        execSync(`taskkill /PID ${pid} /F`, { stdio: "ignore" });
        process.stdout.write(`killed pid ${pid}\n`);
      } catch {
        /* ignore */
      }
    }
    if (pids.size === 0) process.stdout.write(`no process listening on :${p}\n`);
    return;
  }

  // POSIX: lsof -t -i :PORT
  try {
    const pidsOut = execSync(`lsof -t -i :${p}`, { encoding: "utf8" }).trim();
    if (!pidsOut) {
      process.stdout.write(`no process listening on :${p}\n`);
      return;
    }
    for (const pid of pidsOut.split(/\s+/)) {
      execSync(`kill -TERM ${pid}`, { stdio: "ignore" });
      process.stdout.write(`sent SIGTERM to pid ${pid}\n`);
    }
  } catch {
    process.stdout.write(`no process listening on :${p}\n`);
  }
}

killByPort(port);
