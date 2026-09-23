/**
 * Fleet's own tools for OpenCode 2 sessions: canvases, the app runner, the browser canvas and its screenshots.
 *
 * Fleet embeds this file, writes it into its data folder as fleet/index.js, and loads the folder through the "plugins"
 * list in OPENCODE_CONFIG_CONTENT: OpenCode 2 loads a plugin from a folder, not a file. Edit it here, in the Fleet
 * repo: Fleet overwrites the installed copy. The OpenCode (1.x) harness has its own plugin
 * (opencode/fleet/fleet-canvas.ts); the two change separately.
 *
 * Plain JavaScript and no imports, on purpose: OpenCode 2 loads it as it is, and a plugin whose import fails breaks
 * every prompt on the server. OpenCode 2 checks the args against each tool's JSON Schema; Fleet checks them again.
 *
 * Every tool sets `codemode: false`. Without it OpenCode 2 offers the tool only inside Code Mode's `execute` tool.
 */

const BRIDGE_PATH = "/api/bridge/canvas/"
const MESSAGE_PATH = "/api/bridge/session/message"
const STEP_DONE_PATH = "/api/bridge/workflow/step-done"

/**
 * Calls Fleet's bridge for one tool call. FLEET_URL names this server (…/agent/{token}), and the bridge token says
 * which server is calling; Fleet finds its session from the OpenCode 2 session id.
 */
async function callFleet(tool, sessionID, args, path = BRIDGE_PATH + tool) {
  const url = process.env.FLEET_URL
  const token = process.env.FLEET_BRIDGE_TOKEN
  if (!url || !token) {
    throw new Error("Fleet's tools aren't connected in this OpenCode 2 server. Restart Fleet to connect them.")
  }

  let response
  try {
    response = await fetch(url + path, {
      method: "POST",
      headers: { "content-type": "application/json", authorization: "Bearer " + token },
      body: JSON.stringify({ ...args, harnessSessionId: sessionID }),
    })
  } catch (error) {
    throw new Error("Couldn't reach Fleet at " + url + ": " + (error instanceof Error ? error.message : String(error)))
  }

  const text = await response.text()
  let body
  try {
    body = JSON.parse(text)
  } catch {
    body = undefined
  }

  if (!response.ok || !body) {
    throw new Error(body?.error ?? "Fleet answered " + response.status + ": " + text)
  }

  // Images come back base64 and go to the model as file content with a data URL, the way OpenCode 2's own read
  // tool returns an image.
  const files = (body.attachments ?? []).map((file) => ({
    type: "file",
    uri: "data:" + file.mime + ";base64," + file.base64,
    mime: file.mime,
    name: file.fileName,
  }))

  return {
    title: body.title ?? "",
    content: [{ type: "text", text: body.output ?? "" }, ...files],
    metadata: body.metadata ?? {},
  }
}

const canvasId = {
  type: "string",
  description: "The canvas id (cv_…) from fleet_canvas_list or fleet_canvas_open.",
}

/** A tool whose every property is required, as Fleet's tools have always been. */
function fleetTool(name, description, properties, execute, options = {}) {
  return {
    name,
    description,
    input: { type: "object", properties, required: Object.keys(properties), additionalProperties: false },
    options: { codemode: false, ...options },
    execute,
  }
}

const tools = [
  fleetTool(
    "fleet_canvas_list",
    "List the canvases open in this session's side panel: id, kind, title and version.",
    {},
    (_input, tool) => callFleet("list", tool.sessionID, {}),
  ),

  fleetTool(
    "fleet_canvas_open",
    [
      "Show the user a diagram (architecture, a flow, dependencies, a sequence) in a canvas beside the chat.",
      "Use it only when the user asks for a diagram, or when a diagram is clearly the best way to answer them; then draw it here rather than as text or Mermaid in chat.",
      "Don't open one for your own notes or progress, or when you're working on a task delegated by another agent: your result goes to that agent, not to the user.",
      "Read a canvas before you describe it or change it.",
      "Opening a title that already exists updates that canvas to the new state, and reopens it if it was closed. To change part of a canvas, use fleet_canvas_patch.",
    ].join(" "),
    {
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
    (input, tool) => callFleet("open", tool.sessionID, { kind: input.kind, title: input.title, state: input.state }),
  ),

  fleetTool(
    "fleet_canvas_read",
    "Read a canvas as compact text: every box and edge by id, the Mermaid source, or a browser canvas's page with its app's status and recent output.",
    { canvasId },
    (input, tool) => callFleet("read", tool.sessionID, { canvasId: input.canvasId }),
  ),

  fleetTool(
    "fleet_canvas_patch",
    [
      "Change part of a canvas. Ops apply in order, all or nothing, and name boxes and edges by id.",
      "Diagram ops: addNode {id, label, detail?}; updateNode {id, label?, detail?}; removeNode {id} (removes its edges too);",
      "addEdge {id, from, to, label?, style?}; updateEdge {id, label?, style?}; removeEdge {id}; setDirection {direction}.",
      "Sequence op: setSource {source}.",
      "Example: [{\"op\": \"addNode\", \"id\": \"n4\", \"label\": \"Cache\"}, {\"op\": \"addEdge\", \"id\": \"e5\", \"from\": \"n2\", \"to\": \"n4\"}].",
    ].join(" "),
    {
      canvasId,
      ops: {
        type: "array",
        items: { type: "object" },
        description: "The ops, each an object whose \"op\" field names it.",
      },
    },
    (input, tool) => callFleet("patch", tool.sessionID, { canvasId: input.canvasId, ops: input.ops }),
  ),

  fleetTool(
    "fleet_canvas_focus",
    "Bring a canvas to the front of the side panel, reopening it if the user closed it.",
    { canvasId },
    (input, tool) => callFleet("focus", tool.sessionID, { canvasId: input.canvasId }),
  ),

  fleetTool(
    "fleet_app_start",
    [
      "Start the project's web app as a long-running server and show its page to the user in a browser canvas beside the chat.",
      "Use it only when the user asks to run, host, serve, preview or see the app, whatever it's built with (npm, bun, dotnet, python, cargo...), or to try a change to the app's pages or HTTP behavior in the running app.",
      "It is not a shell. Commands that finish on their own (git, gh, builds, tests, formatters, scripts, echo) fail here: run them with your shell tool.",
      "If you can't run shell commands, don't use this tool either.",
      "Don't start dev servers with your shell tool: they never exit, and the user can't see them.",
      "Fleet runs the command in the session's folder, keeps it running, finds the page it serves and waits until it answers (up to 3 minutes).",
      "Fleet sets PORT to a free port; servers that ignore PORT keep their own port, and Fleet finds it.",
      "ASP.NET ignores PORT: append `-- --urls http://localhost:$PORT` (%PORT% on Windows) to dotnet run or dotnet watch so two copies of the project don't collide.",
      "Prefer a command that reloads on changes (npm run dev, bun --hot, dotnet watch). Calling this again with the same command restarts the app.",
      "If it fails, the result has the last lines of output: fix the problem and call it again.",
    ].join(" "),
    {
      command: {
        type: "string",
        description: "The command that starts the app and keeps serving it, e.g. \"npm run dev\" or \"dotnet watch --project src/Web\". Never a command that exits, such as git or a build. Read package.json, the README or the project files first.",
      },
      title: {
        type: "string",
        description: "Short title for the canvas tab, e.g. \"Storefront\".",
      },
    },
    (input, tool) => callFleet("app-start", tool.sessionID, { command: input.command, title: input.title }),
    // The command runs in a shell, so the tool goes under the shell permission, as OpenCode 2's own shell tool does:
    // an agent that may not run shell commands doesn't get it.
    { permission: "shell" },
  ),

  fleetTool(
    "fleet_browser_open",
    [
      "Show a page that's already running on this machine in a browser canvas beside the chat, e.g. a server the user started or one in a container.",
      "To run the app first, use fleet_app_start instead. Only http and https addresses on this machine work.",
    ].join(" "),
    {
      url: {
        type: "string",
        description: "The page's address, e.g. \"http://localhost:5173/\".",
      },
      title: {
        type: "string",
        description: "Short title for the canvas tab.",
      },
    },
    (input, tool) => callFleet("browser-open", tool.sessionID, { url: input.url, title: input.title }),
  ),

  fleetTool(
    "fleet_browser_screenshot",
    [
      "Look at a page in a browser canvas: Fleet takes a screenshot and attaches it to this tool's result, as an image you can see.",
      "Use it to check UI work you just did — layout, spacing, colours, whether the thing you changed is even on the screen — instead of assuming the code is enough.",
      "Take one after a change, and again after the fix.",
      "Fleet shoots the page in its own headless browser, so the user's tab doesn't move and nothing is clicked.",
      "A shot costs roughly width × height / 750 tokens of context (about 1,400 for desktop, 500 for phone), so take the ones you'll actually read.",
    ].join(" "),
    {
      canvasId,
      path: {
        type: "string",
        description: "Empty string for the page the canvas is showing, or a path on the same app to shoot instead, e.g. \"/settings\".",
      },
      viewport: {
        type: "string",
        enum: ["desktop", "phone"],
        description: "desktop: 1280×800. phone: 390×844, for checking a narrow layout.",
      },
    },
    (input, tool) =>
      callFleet("screenshot", tool.sessionID, { canvasId: input.canvasId, path: input.path, viewport: input.viewport }),
  ),
]

// Only on servers started with messages between sessions on. Fleet then refuses prompts from agents through its API,
// so this is the one way to message a session, and the message says which session sent it.
if (process.env.FLEET_SESSION_MESSAGES === "1") {
  tools.push(
    fleetTool(
      "fleet_message",
      [
        "Send a message to another Fleet session. It arrives there marked as coming from this session, as a teammate's request, not the user's,",
        "and wakes the session if it's idle. Its reply stays in that session unless you set notifyWhenDone.",
        "Get the session's id from the Fleet API skill (GET $FLEET_URL/api/sessions). This is the only way to message a session: Fleet refuses prompts from agents through its API.",
      ].join(" "),
      {
        sessionId: {
          type: "string",
          description: "The Fleet id of the session to message, from GET $FLEET_URL/api/sessions. Not an OpenCode session id.",
        },
        text: {
          type: "string",
          description: "The message: what you need from that session and why, with the paths and details it needs to act on its own.",
        },
        notifyWhenDone: {
          type: "boolean",
          description: [
            "true only when your own work depends on that session's answer: Fleet then sends you its reply, wrapped in <fleet-session-update>,",
            "when the turn that handles your message ends, and starts a turn here to read it. Each update costs a turn, so don't poll or check on it meanwhile.",
            "false when you're handing work off or just telling it something.",
          ].join(" "),
        },
      },
      (input, tool) =>
        callFleet(
          "message",
          tool.sessionID,
          { sessionId: input.sessionId, text: input.text, notifyWhenDone: input.notifyWhenDone === true },
          MESSAGE_PATH,
        ),
    ),
  )
}

/**
 * How the agent's shell commands differ from this server's environment, from Fleet (FLEET_SHELL_ENVIRONMENT):
 * {"NAME": null} removes a variable, {"NAME": "value"} sets it. Fleet starts the server with variables for the server
 * alone (its password, its config and database, Fleet's config for it), and every shell V2 starts would inherit them:
 * an `opencode` the agent ran would open this server's database. V2 hands each new shell's environment to the
 * "create.before" hook first: the session's own commands, a subagent's, a backgrounded one and the user's.
 */
function shellEnvironment() {
  try {
    const changes = JSON.parse(process.env.FLEET_SHELL_ENVIRONMENT ?? "null")
    return changes && typeof changes === "object" ? changes : null
  } catch {
    return null
  }
}

// Only in servers started with workflows on. Every session on such a server that isn't a workflow step is created
// with a rule that denies this tool, so only the sessions a workflow starts see it. Fleet refuses a call from any
// other session, including a step's subagents.
if (process.env.FLEET_WORKFLOWS === "1") {
  tools.push(
    fleetTool(
      "fleet_step_done",
      [
        "Finish this workflow step. Call it once, as your last action, when the step's work is complete.",
        "Fleet reads the outcome to choose the next step, and passes your summary on to it.",
      ].join(" "),
      {
        outcome: { type: "string", description: "One of the outcomes the step's instructions list." },
        summary: { type: "string", description: "What you did and what the next step needs to know, in a few sentences." },
      },
      (input, tool) => callFleet("step-done", tool.sessionID, { outcome: input.outcome, summary: input.summary }, STEP_DONE_PATH),
    ),
  )
}

export default {
  id: "fleet",
  async setup(ctx) {
    await ctx.tool.transform((editor) => {
      for (const tool of tools) editor.add(tool)
    })

    const changes = shellEnvironment()
    if (changes && ctx.shell?.hook) {
      await ctx.shell.hook("create.before", (event) => {
        if (!event.env) return
        for (const [name, value] of Object.entries(changes)) {
          if (value === null) delete event.env[name]
          else event.env[name] = String(value)
        }
      })
    }
  },
}
