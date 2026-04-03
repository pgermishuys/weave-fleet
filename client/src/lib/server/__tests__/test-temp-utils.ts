/**
 * Secure temp-directory helpers for tests.
 *
 * `mkdtempSync` creates directories with mode 0o700 (owner-only access),
 * which makes them secure for temporary file storage. We use `realpathSync`
 * to resolve the canonical path and validate with `statSync` before writing.
 */

import { mkdtempSync, writeFileSync, mkdirSync, realpathSync, statSync } from "fs";
import { tmpdir } from "os";
import { join } from "path";

/** Create a secure temporary directory with a given prefix. */
export function createSecureTempDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

/**
 * Write a file inside a previously-created secure temp directory.
 *
 * Accepts a base directory (from `createSecureTempDir`) and a relative
 * path within it. Validates the base directory exists and is a directory
 * before writing. Intermediate directories are created automatically.
 */
export function writeTempFile(
  baseDir: string,
  relativePath: string,
  content: string,
): void {
  // Validate base directory is an existing directory (not a symlink race target)
  const stat = statSync(baseDir);
  if (!stat.isDirectory()) {
    throw new Error(`writeTempFile: baseDir is not a directory: ${baseDir}`);
  }
  const fullPath = join(baseDir, relativePath);
  const parentDir = join(fullPath, "..");
  mkdirSync(parentDir, { recursive: true });
  writeFileSync(fullPath, content, "utf-8");
}
