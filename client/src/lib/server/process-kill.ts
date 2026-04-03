/**
 * Cross-platform process tree kill utility.
 *
 * On Windows, Node.js child_process uses `shell: true` to spawn via cmd.exe,
 * creating a process tree: Node → cmd.exe → opencode.exe. Sending SIGTERM or
 * SIGKILL to the ChildProcess object only kills cmd.exe — opencode.exe is
 * orphaned. `taskkill /T /F` kills the entire tree.
 *
 * On POSIX, `proc.kill(signal)` is used directly. NOTE: do NOT use
 * `process.kill(-pid, signal)` (negative-PID group kill) — the child is
 * spawned WITHOUT `detached: true`, so it shares the parent's process group.
 * A negative-PID kill would terminate the entire Next.js application.
 */

import { execSync, exec } from "child_process";
import type { ChildProcess } from "child_process";

/**
 * Kill a process (or process tree on Windows) synchronously.
 *
 * - Windows: uses `taskkill /PID <pid> /T /F` to kill the entire process tree.
 *   The `signal` parameter is ignored — taskkill is always forceful on Windows.
 * - POSIX: calls `proc.kill(signal)` on the ChildProcess object.
 *
 * ESRCH errors (process already dead) are swallowed silently.
 * All other errors are re-thrown.
 *
 * @param proc - The ChildProcess object (used on POSIX only).
 * @param signal - The signal to send (used on POSIX only).
 * @param pid - The numeric PID (used on Windows for taskkill).
 * @param platform - Optional platform override for testability (defaults to process.platform).
 */
export function killProcessTree(
  proc: ChildProcess,
  signal: NodeJS.Signals,
  pid: number,
  platform: string = process.platform
): void {
  try {
    if (platform === "win32") {
      // Windows: kill entire process tree (cmd.exe + opencode.exe).
      // Signal is irrelevant — taskkill /T /F is always forceful.
      // taskkill exits with non-zero if PID not found (process already dead) — swallow.
      execSync(`taskkill /PID ${pid} /T /F`, { stdio: "ignore" });
    } else {
      // POSIX: kill the ChildProcess directly. Do NOT use process.kill(-pid)
      // because the child shares the parent's process group (no detached:true).
      proc.kill(signal);
    }
  } catch (err) {
    if (isEsrch(err)) {
      // Process already dead — ignore silently
      return;
    }
    throw err;
  }
}

/**
 * Kill a process (or process tree on Windows) asynchronously.
 *
 * - Windows: uses `exec("taskkill /PID <pid> /T /F")` (async, non-blocking).
 *   Suitable for graceful shutdown paths where we don't want to block the event loop.
 * - POSIX: calls `proc.kill(signal)` on the ChildProcess object (synchronous under the hood).
 *
 * ESRCH errors (process already dead) are swallowed silently.
 * All other errors are re-thrown.
 *
 * @param proc - The ChildProcess object (used on POSIX only).
 * @param signal - The signal to send (used on POSIX only).
 * @param pid - The numeric PID (used on Windows for taskkill).
 * @param platform - Optional platform override for testability (defaults to process.platform).
 */
export function killProcessTreeAsync(
  proc: ChildProcess,
  signal: NodeJS.Signals,
  pid: number,
  platform: string = process.platform
): Promise<void> {
  if (platform === "win32") {
    // Windows: async taskkill — does not block the event loop.
    return new Promise<void>((resolve) => {
      exec(`taskkill /PID ${pid} /T /F`, (err) => {
        if (err && !isEsrch(err) && !isTaskkillNotFound(err)) {
          // Log but don't reject — we've done our best effort.
          // Caller should handle remaining cleanup (e.g. SIGKILL escalation).
          console.warn(
            `[process-kill] taskkill async failed for PID ${pid}: ${err.message}`
          );
        }
        resolve();
      });
    });
  }

  // POSIX: proc.kill() is synchronous but wrap in Promise for uniform API
  return new Promise<void>((resolve, reject) => {
    try {
      proc.kill(signal);
      resolve();
    } catch (err) {
      if (isEsrch(err)) {
        resolve();
        return;
      }
      reject(err);
    }
  });
}

/**
 * Check if an error is ESRCH (process not found / already dead).
 * Works for both Node.js errors (errno ESRCH) and Windows exit code errors.
 */
function isEsrch(err: unknown): boolean {
  if (err instanceof Error) {
    // Node.js POSIX error: code === "ESRCH"
    if ((err as NodeJS.ErrnoException).code === "ESRCH") return true;
    // Windows: taskkill exits with error when PID not found
    // Error message contains "not found" or exit code 128
    if (isTaskkillNotFound(err)) return true;
  }
  return false;
}

/**
 * Check if a taskkill error indicates the process was not found (already dead).
 */
function isTaskkillNotFound(err: unknown): boolean {
  if (err instanceof Error) {
    const msg = err.message.toLowerCase();
    // taskkill: "ERROR: The process ... not found."
    // exit code 128 is also used for "not found"
    if (msg.includes("not found") || msg.includes("no running instance")) return true;
    // spawnSync/execSync error with status code 128
    const exitCode = (err as NodeJS.ErrnoException & { status?: number }).status;
    if (exitCode === 128) return true;
  }
  return false;
}
