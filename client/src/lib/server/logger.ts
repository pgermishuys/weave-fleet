/**
 * Structured logger — lightweight JSON logging for server-side modules.
 *
 * Outputs one JSON line per log call with timestamp, level, context (module name),
 * message, and optional structured details. Uses console.info/warn/error under
 * the hood so Next.js dev overlay picks them up.
 *
 * No external dependencies.
 */

import { getProfileName, isDefaultProfile } from "./profile";

type LogLevel = "info" | "warn" | "error";

interface LogEntry {
  timestamp: string;
  level: LogLevel;
  context: string;
  message: string;
  [key: string]: unknown;
}

function formatError(err: unknown): Record<string, unknown> {
  if (err instanceof Error) {
    return {
      errorName: err.name,
      errorMessage: err.message,
      ...(err.stack ? { stack: err.stack } : {}),
    };
  }
  return { errorValue: String(err) };
}

function emit(
  level: LogLevel,
  context: string,
  message: string,
  details?: Record<string, unknown>,
): void {
  const entry: LogEntry = {
    timestamp: new Date().toISOString(),
    level,
    context,
    message,
  };

  // Include profile name when non-default — helps correlate logs across simultaneous instances
  if (!isDefaultProfile()) {
    entry.profile = getProfileName();
  }

  if (details) {
    for (const [key, value] of Object.entries(details)) {
      // Expand `err` / `error` keys into structured error fields
      if ((key === "err" || key === "error") && value != null) {
        Object.assign(entry, formatError(value));
      } else {
        entry[key] = value;
      }
    }
  }

  const line = JSON.stringify(entry);

  switch (level) {
    case "info":
      console.info(line);
      break;
    case "warn":
      console.warn(line);
      break;
    case "error":
      console.error(line);
      break;
  }
}

export const log = {
  info: (context: string, message: string, details?: Record<string, unknown>) =>
    emit("info", context, message, details),
  warn: (context: string, message: string, details?: Record<string, unknown>) =>
    emit("warn", context, message, details),
  error: (context: string, message: string, details?: Record<string, unknown>) =>
    emit("error", context, message, details),
};
