/**
 * Fleet canvas tools for pooled OpenCode sessions, and the folders with Fleet's skills.
 *
 * Fleet embeds this file, writes it into its data folder, and loads it through the "plugin" list in
 * OPENCODE_CONFIG_CONTENT. Edit it here, in the Fleet repo: Fleet overwrites the installed copy.
 *
 * No imports, on purpose. A plugin whose import fails breaks every prompt in the process. With plain
 * objects as `args`, OpenCode builds each tool's JSON Schema from them as written, and every arg is
 * required. OpenCode doesn't validate the args; Fleet does.
 *
 * Every export is treated as a plugin, so export only the plugin function.
 */

const BRIDGE_PATH = "/api/bridge/opencode/canvas/"

type PermissionRequest = { permission: string; patterns: string[]; always: string[]; metadata: Record<string, unknown> }
type ToolContext = { sessionID: string; ask?: (request: PermissionRequest) => Promise<void> }
type ToolResult = { title: string; output: string; metadata: Record<string, unknown> }

async function callFleet(tool: string, context: ToolContext, args: Record<string, unknown>): Promise<ToolResult> {
  const url = process.env.FLEET_URL
  const token = process.env.FLEET_BRIDGE_TOKEN
  if (!url || !token) {
    throw new Error("Fleet canvas tools aren't connected in this OpenCode process. Restart Fleet to connect them.")
  }

  let response: Response
  try {
    response = await fetch(url + BRIDGE_PATH + tool, {
      method: "POST",
      headers: { "content-type": "application/json", authorization: "Bearer " + token },
      body: JSON.stringify({ ...args, openCodeSessionId: context.sessionID }),
    })
  } catch (error) {
    throw new Error("Couldn't reach Fleet at " + url + ": " + (error instanceof Error ? error.message : String(error)))
  }

  const text = await response.text()
  let body: { title?: string; output?: string; metadata?: Record<string, unknown>; error?: string } | undefined
  try {
    body = JSON.parse(text)
  } catch {
    body = undefined
  }

  if (!response.ok || !body) {
    throw new Error(body?.error ?? "Fleet answered " + response.status + ": " + text)
  }

  return { title: body.title ?? "", output: body.output ?? "", metadata: body.metadata ?? {} }
}

const canvasId = {
  type: "string",
  description: "The canvas id (cv_…) from fleet_canvas_list or fleet_canvas_open.",
}

type Config = { skills?: { paths?: string[] } }

export const FleetCanvasPlugin = async () => ({
  // Fleet's own skills (fleet-api, and the built-in skills the user turned on) are in its data folder, one or more
  // folders separated like PATH. Adding them here keeps the user's skill paths: skills.paths in OPENCODE_CONFIG_CONTENT
  // would replace them.
  config: async (config: Config) => {
    const skills = process.env.FLEET_SKILLS_PATH
    if (!skills) return
    const folders = skills.split(process.platform === "win32" ? ";" : ":").filter(Boolean)
    config.skills ??= {}
    config.skills.paths = [...(config.skills.paths ?? []), ...folders]
  },

  tool: {
    fleet_canvas_list: {
      description: "List the canvases open in this session's side panel: id, kind, title and version.",
      args: {},
      execute: (_args: Record<string, never>, context: ToolContext) => callFleet("list", context, {}),
    },

    fleet_canvas_open: {
      description: [
        "Show the user a diagram (architecture, a flow, dependencies, a sequence) in a canvas beside the chat.",
        "Use it only when the user asks for a diagram, or when a diagram is clearly the best way to answer them; then draw it here rather than as text or Mermaid in chat.",
        "Don't open one for your own notes or progress, or when you're working on a task delegated by another agent: your result goes to that agent, not to the user.",
        "Read a canvas before you describe it or change it.",
        "Opening a title that already exists updates that canvas to the new state, and reopens it if it was closed. To change part of a canvas, use fleet_canvas_patch.",
      ].join(" "),
      args: {
        kind: {
          type: "string",
          enum: ["diagram", "sequence"],
          description: "diagram: boxes and arrows (architecture, flows, dependencies). sequence: a Mermaid sequence diagram.",
        },
        title: {
          type: "string",
          description: "Short title for the canvas tab, e.g. \"Session event flow\".",
        },
        state: {
          type: "object",
          description: [
            "For diagram: {\"direction\"?: \"TB\"|\"LR\"|\"BT\"|\"RL\", \"nodes\": [{\"id\", \"label\", \"detail\"?}], \"edges\": [{\"id\", \"from\", \"to\", \"label\"?, \"style\"?: \"solid\"|\"dashed\"|\"planned\"}]}.",
            "Ids use letters, digits and _ . : - and are unique across boxes and edges. Don't give positions; the canvas lays boxes out.",
            "For sequence: {\"source\": \"sequenceDiagram\\n  Agent->>Fleet: open\"}, Mermaid source without code fences.",
          ].join(" "),
        },
      },
      execute: (args: { kind: string; title: string; state: unknown }, context: ToolContext) =>
        callFleet("open", context, { kind: args.kind, title: args.title, state: args.state }),
    },

    fleet_canvas_read: {
      description: "Read a canvas as compact text: every box and edge by id, the Mermaid source, or a browser canvas's page with its app's status and recent output.",
      args: { canvasId },
      execute: (args: { canvasId: string }, context: ToolContext) =>
        callFleet("read", context, { canvasId: args.canvasId }),
    },

    fleet_canvas_patch: {
      description: [
        "Change part of a canvas. Ops apply in order, all or nothing, and name boxes and edges by id.",
        "Diagram ops: addNode {id, label, detail?}; updateNode {id, label?, detail?}; removeNode {id} (removes its edges too);",
        "addEdge {id, from, to, label?, style?}; updateEdge {id, label?, style?}; removeEdge {id}; setDirection {direction}.",
        "Sequence op: setSource {source}.",
        "Example: [{\"op\": \"addNode\", \"id\": \"n4\", \"label\": \"Cache\"}, {\"op\": \"addEdge\", \"id\": \"e5\", \"from\": \"n2\", \"to\": \"n4\"}].",
      ].join(" "),
      args: {
        canvasId,
        ops: {
          type: "array",
          items: { type: "object" },
          description: "The ops, each an object whose \"op\" field names it.",
        },
      },
      execute: (args: { canvasId: string; ops: unknown }, context: ToolContext) =>
        callFleet("patch", context, { canvasId: args.canvasId, ops: args.ops }),
    },

    fleet_canvas_focus: {
      description: "Bring a canvas to the front of the side panel, reopening it if the user closed it.",
      args: { canvasId },
      execute: (args: { canvasId: string }, context: ToolContext) =>
        callFleet("focus", context, { canvasId: args.canvasId }),
    },

    fleet_app_start: {
      description: [
        "Start the project's web app as a long-running server and show its page to the user in a browser canvas beside the chat.",
        "Use it only when the user asks to run, host, serve, preview or see the app, whatever it's built with (npm, bun, dotnet, python, cargo...), or to try a change to the app's pages or HTTP behavior in the running app.",
        "It is not a shell. Commands that finish on their own (git, gh, builds, tests, formatters, scripts, echo) fail here: run them with your shell tool.",
        "If you can't run shell commands, don't use this tool either.",
        "Don't start dev servers with bash: they never exit, and the user can't see them.",
        "Fleet runs the command in the session's folder, keeps it running, finds the page it serves and waits until it answers (up to 3 minutes).",
        "Fleet sets PORT to a free port; servers that ignore PORT keep their own port, and Fleet finds it.",
        "ASP.NET ignores PORT: append `-- --urls http://localhost:$PORT` (%PORT% on Windows) to dotnet run or dotnet watch so two copies of the project don't collide.",
        "Prefer a command that reloads on changes (npm run dev, bun --hot, dotnet watch). Calling this again with the same command restarts the app.",
        "If it fails, the result has the last lines of output: fix the problem and call it again.",
      ].join(" "),
      args: {
        command: {
          type: "string",
          description: "The command that starts the app and keeps serving it, e.g. \"npm run dev\" or \"dotnet watch --project src/Web\". Never a command that exits, such as git or a build. Read package.json, the README or the project files first.",
        },
        title: {
          type: "string",
          description: "Short title for the canvas tab, e.g. \"Storefront\".",
        },
      },
      execute: async (args: { command: string; title: string }, context: ToolContext) => {
        // The command runs in a shell, so it needs the calling agent's shell permission, as OpenCode's own bash
        // tool does: an agent that may not run shell commands mustn't run them through Fleet. OpenCode throws
        // when the permission is denied, and the model reads OpenCode's own refusal.
        await context.ask?.({ permission: "bash", patterns: [args.command], always: [args.command], metadata: {} })
        return callFleet("app-start", context, { command: args.command, title: args.title })
      },
    },

    fleet_browser_open: {
      description: [
        "Show a page that's already running on this machine in a browser canvas beside the chat, e.g. a server the user started or one in a container.",
        "To run the app first, use fleet_app_start instead. Only http and https addresses on this machine work.",
      ].join(" "),
      args: {
        url: {
          type: "string",
          description: "The page's address, e.g. \"http://localhost:5173/\".",
        },
        title: {
          type: "string",
          description: "Short title for the canvas tab.",
        },
      },
      execute: (args: { url: string; title: string }, context: ToolContext) =>
        callFleet("browser-open", context, { url: args.url, title: args.title }),
    },
  },
})
