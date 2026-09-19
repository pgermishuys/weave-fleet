/**
 * Extracts a human-readable label for a tool call based on its name and input.
 *
 * Used by the activity stream to show context-aware one-line summaries
 * instead of raw truncated output.
 */

/** Shorten a file path to `parentDir/filename` when it exceeds `maxLen` chars. */
export function shortenPath(filePath: string, maxLen = 50): string {
  if (filePath.length <= maxLen) return filePath;
  const segments = filePath.split("/");
  if (segments.length <= 2) return filePath;
  // Keep last two segments: parentDir/filename
  return "…/" + segments.slice(-2).join("/");
}

/** Truncate a string to `maxLen` chars, appending "…" if trimmed. */
function truncate(value: string, maxLen: number): string {
  if (value.length <= maxLen) return value;
  return value.slice(0, maxLen) + "…";
}

/** The file a call is about: OpenCode names it `filePath`, OpenCode 2 `path`. */
function filePathOf(input: Record<string, unknown> | null): string | undefined {
  if (typeof input?.filePath === "string" && input.filePath) return input.filePath;
  if (typeof input?.path === "string" && input.path) return input.path;
  return undefined;
}

export function getToolLabel(
  toolName: string,
  input: Record<string, unknown> | null,
): string {
  switch (toolName) {
    case "bash": {
      if (typeof input?.description === "string" && input.description) {
        return input.description;
      }
      if (typeof input?.command === "string" && input.command) {
        return truncate(input.command, 60);
      }
      return "bash";
    }

    // OpenCode 2's shell tool; its input has no description, but a model may send one.
    case "shell": {
      if (typeof input?.description === "string" && input.description) {
        return input.description;
      }
      if (typeof input?.command === "string" && input.command) {
        return truncate(input.command, 60);
      }
      return "shell";
    }

    case "read":
    case "edit":
    case "write": {
      const path = filePathOf(input);
      return path ? shortenPath(path) : toolName;
    }

    case "glob": {
      if (typeof input?.pattern === "string" && input.pattern) {
        return input.pattern;
      }
      return "glob";
    }

    case "grep": {
      if (typeof input?.pattern === "string" && input.pattern) {
        return input.pattern;
      }
      return "grep";
    }

    case "fleet_app_start":
    case "fleet_browser_open": {
      const what = typeof input?.command === "string" && input.command ? input.command : input?.url;
      const title = typeof input?.title === "string" && input.title ? input.title : "";
      if (typeof what === "string" && what) return title ? `${title} · ${truncate(what, 60)}` : truncate(what, 60);
      return title || toolName;
    }

    case "fleet_browser_screenshot": {
      const viewport = typeof input?.viewport === "string" && input.viewport ? input.viewport : "desktop";
      const path = typeof input?.path === "string" && input.path ? ` ${truncate(input.path, 40)}` : "";
      return `screenshot${path} (${viewport})`;
    }

    case "webfetch": {
      if (typeof input?.url === "string" && input.url) {
        return input.url;
      }
      return "webfetch";
    }

    // OpenCode names the skill `name`, OpenCode 2 `id`.
    case "skill": {
      if (typeof input?.name === "string" && input.name) {
        return input.name;
      }
      if (typeof input?.id === "string" && input.id) {
        return input.id;
      }
      return "skill";
    }

    case "websearch": {
      if (typeof input?.query === "string" && input.query) {
        return truncate(input.query, 60);
      }
      return "websearch";
    }

    // OpenCode 2's sub-agent tool (OpenCode's is `task`).
    case "subagent": {
      const agent = typeof input?.agent === "string" && input.agent ? input.agent : "";
      const description = typeof input?.description === "string" && input.description ? input.description : "";
      if (agent && description) return `${agent} · ${truncate(description, 60)}`;
      return description || agent || "subagent";
    }

    case "question": {
      const questions = Array.isArray(input?.questions) ? input.questions : [];
      const first = questions[0] as Record<string, unknown> | undefined;
      const heading = typeof first?.question === "string" && first.question ? first.question : first?.header;
      return typeof heading === "string" && heading ? truncate(heading, 60) : "question";
    }

    // OpenCode 2's Code Mode: the model runs a script that calls other tools.
    case "execute": {
      const code = typeof input?.code === "string" ? input.code : "";
      const firstLine = code.split("\n").find((line) => line.trim());
      return firstLine ? truncate(firstLine.trim(), 60) : "execute";
    }

    default:
      return toolName;
  }
}
