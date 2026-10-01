// Spike: the smallest OpenCode 2 browser attachment, backed by a headless Chrome over CDP.
// usage: bun attach.ts <v2 base url> <password> <sessionID> <directory>
const [base, password, sessionID, directory] = Bun.argv.slice(2)
const auth = "Basic " + btoa(`opencode:${password}`)
const loc = `location%5Bdirectory%5D=${encodeURIComponent(directory)}`
const connectionID = crypto.randomUUID()
const log = (...a: unknown[]) => console.log(new Date().toISOString().slice(11, 23), ...a)

async function rpc(method: string, input: object) {
  const res = await fetch(`${base}/api/rpc/experimental.browser/${method}?${loc}`, {
    method: "POST", headers: { authorization: auth, "content-type": "application/json" }, body: JSON.stringify({ input }),
  })
  const text = await res.text()
  if (!res.ok) throw new Error(`${method} ${res.status} ${text.slice(0, 400)}`)
  return text ? JSON.parse(text) : null
}

// ---- headless Chrome over CDP ----
const profile = `${process.env.TMPDIR}/attach-chrome-${connectionID}`
const chromePath = process.env.CHROME!
const chrome = Bun.spawn([chromePath, "--headless=new", "--remote-debugging-port=0", `--user-data-dir=${profile}`, "--no-first-run", "--no-sandbox", "about:blank"], { stderr: "pipe" })
let wsURL = ""
{
  const r = chrome.stderr.getReader(); const d = new TextDecoder(); let acc = ""
  while (!wsURL) { const { value, done } = await r.read(); if (done) break; acc += d.decode(value); const m = acc.match(/DevTools listening on (ws:\S+)/); if (m) wsURL = m[1] }
  if (!wsURL) { console.error("chrome said:", acc, "exit", await chrome.exited); process.exit(1) }
  ;(async () => { for (;;) { const { done } = await r.read(); if (done) break } })()
}
const ws = new WebSocket(wsURL)
await new Promise((r) => (ws.onopen = r))
let nextID = 1
const waiting = new Map<number, (m: any) => void>()
const listeners: ((m: any) => void)[] = []
ws.onmessage = (e) => { const m = JSON.parse(String(e.data)); if (m.id && waiting.has(m.id)) { waiting.get(m.id)!(m); waiting.delete(m.id) } else listeners.forEach((l) => l(m)) }
function cdp(method: string, params: object = {}, sessionId?: string): Promise<any> {
  const id = nextID++
  ws.send(JSON.stringify({ id, method, params, sessionId }))
  return new Promise((resolve, reject) => waiting.set(id, (m) => (m.error ? reject(new Error(`${method}: ${m.error.message}`)) : resolve(m.result))))
}
function once(method: string, sessionId: string) {
  return new Promise<void>((resolve) => { const l = (m: any) => { if (m.method === method && m.sessionId === sessionId) { listeners.splice(listeners.indexOf(l), 1); resolve() } }; listeners.push(l) })
}
log("chrome", wsURL)

type Tab = { id: string; target: string; session: string; generation: number; console: any[]; refs: Map<string, number> }
const tabs = new Map<string, Tab>()
let focused: string | null = null
async function info(tab: Tab) {
  const r = await cdp("Runtime.evaluate", { expression: "JSON.stringify({u: location.href, t: document.title, l: document.readyState !== 'complete'})", returnByValue: true }, tab.session)
  const v = JSON.parse(r.result.value)
  const h = await cdp("Page.getNavigationHistory", {}, tab.session)
  return { id: tab.id, url: v.u, title: v.t.slice(0, 2048), loading: v.l, canGoBack: h.currentIndex > 0, canGoForward: h.currentIndex < h.entries.length - 1, generation: tab.generation }
}
async function state() { return { tabs: await Promise.all([...tabs.values()].map(info)), focusedTabID: focused } }
async function publish() { await rpc("state", { sessionID, connectionID, state: await state() }) }
async function navigate(tab: Tab, url: string) {
  const loaded = once("Page.loadEventFired", tab.session)
  await cdp("Page.navigate", { url }, tab.session)
  await Promise.race([loaded, Bun.sleep(10_000)])
  tab.generation++; tab.refs.clear(); tab.console = []
}
async function snapshot(tab: Tab) {
  const ax = await cdp("Accessibility.getFullAXTree", { depth: 12 }, tab.session)
  const nodes = new Map(ax.nodes.map((n: any) => [n.nodeId, n]))
  tab.refs.clear(); let n = 0; const lines: string[] = []
  const walk = (node: any, level: number) => {
    const role = String(node.role?.value ?? "node")
    const props = new Map((node.properties ?? []).map((p: any) => [p.name, p.value.value]))
    if (!node.ignored) {
      const actionable = role !== "RootWebArea" && (props.get("focusable") || /^(button|link|textbox|combobox|checkbox|radio|option)$/.test(role))
      const ref = actionable && node.backendDOMNodeId ? `e${++n}` : ""
      if (ref) tab.refs.set(ref, node.backendDOMNodeId)
      lines.push(`${"  ".repeat(level)}${ref ? `@${ref} ` : ""}[${role}] ${JSON.stringify(String(node.name?.value ?? "").replace(/\s+/g, " ").slice(0, 300))}`)
    }
    for (const c of node.childIds ?? []) { const child = nodes.get(c); if (child) walk(child, level + 1) }
  }
  walk(ax.nodes[0], 0)
  return { tab: await info(tab), content: lines.join("\n").slice(0, 100_000), truncated: false }
}
async function center(tab: Tab, ref: string) {
  const backendNodeId = tab.refs.get(ref.replace(/^@/, ""))
  if (!backendNodeId) throw new Error("Unknown ref. Take a fresh snapshot.")
  await cdp("DOM.scrollIntoViewIfNeeded", { backendNodeId }, tab.session)
  const { model } = await cdp("DOM.getBoxModel", { backendNodeId }, tab.session)
  const q = model.content; return { x: (q[0] + q[4]) / 2, y: (q[1] + q[5]) / 2, backendNodeId }
}
const fileID = () => `file_${crypto.randomUUID()}`

async function execute(action: any): Promise<{ value: unknown; files: any[] }> {
  const tab = action.tabID ? tabs.get(action.tabID) : undefined
  if (action.tabID && !tab) throw new Error("tab_unavailable")
  switch (action.type) {
    case "tabs.list": return { value: await state(), files: [] }
    case "tabs.open": {
      const { targetId } = await cdp("Target.createTarget", { url: "about:blank" })
      const { sessionId } = await cdp("Target.attachToTarget", { targetId, flatten: true })
      const t: Tab = { id: `tab_${crypto.randomUUID()}`, target: targetId, session: sessionId, generation: 0, console: [], refs: new Map() }
      await cdp("Page.enable", {}, sessionId); await cdp("Runtime.enable", {}, sessionId); await cdp("DOM.enable", {}, sessionId)
      await cdp("Emulation.setDeviceMetricsOverride", { width: 1280, height: 800, deviceScaleFactor: 1, mobile: false }, sessionId)
      listeners.push((m) => { if (m.sessionId === sessionId && m.method === "Runtime.consoleAPICalled") t.console.push({ id: String(t.console.length + 1), timestampMs: Date.now(), level: m.params.type === "warning" ? "warning" : m.params.type === "error" ? "error" : "info", text: m.params.args.map((a: any) => a.value ?? a.description ?? "").join(" ").slice(0, 2000), textTruncated: false }) })
      tabs.set(t.id, t)
      if (action.url) await navigate(t, action.url)
      if (action.focus !== false) focused = t.id
      await publish() // the server must know the tab before the result names it
      return { value: await info(t), files: [] }
    }
    case "tabs.focus": focused = tab!.id; await publish(); return { value: await info(tab!), files: [] }
    case "tabs.close": await cdp("Target.closeTarget", { targetId: tab!.target }); tabs.delete(tab!.id); if (focused === tab!.id) focused = null; await publish(); return { value: await state(), files: [] }
    case "navigate": await navigate(tab!, action.url); await publish(); return { value: await info(tab!), files: [] }
    case "snapshot": return { value: await snapshot(tab!), files: [] }
    case "click": {
      const p = await center(tab!, action.ref)
      for (const type of ["mousePressed", "mouseReleased"]) await cdp("Input.dispatchMouseEvent", { type, x: p.x, y: p.y, button: "left", clickCount: 1 }, tab!.session)
      await Bun.sleep(150); return { value: await info(tab!), files: [] }
    }
    case "fill": {
      const p = await center(tab!, action.ref)
      await cdp("DOM.focus", { backendNodeId: p.backendNodeId }, tab!.session)
      await cdp("Runtime.evaluate", { expression: "document.activeElement.select && document.activeElement.select()" }, tab!.session)
      await cdp("Input.insertText", { text: action.text }, tab!.session)
      return { value: await info(tab!), files: [] }
    }
    case "press": await cdp("Input.dispatchKeyEvent", { type: "keyDown", key: action.key, code: action.key, windowsVirtualKeyCode: action.key === "Enter" ? 13 : 0, text: action.key === "Enter" ? "\r" : undefined }, tab!.session); await cdp("Input.dispatchKeyEvent", { type: "keyUp", key: action.key }, tab!.session); return { value: await info(tab!), files: [] }
    case "evaluate": {
      const r = await cdp("Runtime.evaluate", { expression: action.script, awaitPromise: true, returnByValue: true, userGesture: true }, tab!.session)
      if (r.exceptionDetails) throw new Error(`Page JavaScript threw: ${r.exceptionDetails.text}`)
      return { value: { tab: await info(tab!), value: r.result.value ?? null }, files: [] }
    }
    case "console": return { value: { tab: await info(tab!), messages: tab!.console.slice(-(action.limit ?? 100)), truncated: false, dropped: 0 }, files: [] }
    case "screenshot": {
      const shot = await cdp("Page.captureScreenshot", { format: "png" }, tab!.session)
      return { value: { tab: await info(tab!) }, files: [{ id: fileID(), name: "screenshot.png", mime: "image/png", data: shot.data }] }
    }
    default: { const e: any = new Error(`browser.${action.type} isn't supported by this spike attachment.`); e.code = "unsupported"; throw e }
  }
}

// ---- the attachment: events first, then attach (stays pending) ----
const events = await fetch(`${base}/api/event`, { headers: { authorization: auth, accept: "text/event-stream" } })
const reader = events.body!.getReader()
const decoder = new TextDecoder()
let buffer = ""
let attachCall: Promise<unknown> | undefined
;(async () => {
  for (;;) {
    const { value, done } = await reader.read(); if (done) break
    buffer += decoder.decode(value, { stream: true })
    let i
    while ((i = buffer.indexOf("\n\n")) >= 0) {
      const frame = buffer.slice(0, i); buffer = buffer.slice(i + 2)
      const data = frame.split("\n").filter((l) => l.startsWith("data:")).map((l) => l.slice(5).trim()).join("\n")
      if (!data) continue
      const event = JSON.parse(data)
      if (event.type === "server.connected" && !attachCall) {
        log("event stream connected; attaching", { sessionID, connectionID })
        attachCall = rpc("attach", { sessionID, connectionID, version: 4 }).then((r) => { log("attach returned", JSON.stringify(r)); process.exit(0) }, (e) => { log("attach failed", e.message); process.exit(1) })
        continue
      }
      if (event.type !== "rpc.experimental.browser.control" || event.data.connectionID !== connectionID) continue
      const message = event.data
      log("control", JSON.stringify(message))
      if (message.type === "attached") { await publish(); log("published empty state; ready"); continue }
      if (message.type === "cancel") continue
      ;(async () => {
        const raw = await rpc("command", { sessionID, connectionID, requestID: message.requestID })
        const command = raw.data ?? raw.output ?? raw
        log("command", JSON.stringify(command).slice(0, 300))
        let outcome: any
        try {
          const result = await execute(command.action)
          outcome = { type: "success", result }
        } catch (e: any) { outcome = { type: "failure", code: e.code ?? "failed", message: String(e.message).slice(0, 2000) } }
        await rpc("result", { sessionID, connectionID, requestID: message.requestID, outcome })
        log("result", command.action.type, outcome.type, outcome.type === "success" ? `${JSON.stringify(outcome.result.value).length} chars, ${outcome.result.files.length} file(s)` : outcome.message)
      })().catch((e) => log("command error", e.message))
    }
  }
})()
process.on("SIGTERM", () => { chrome.kill(); process.exit(0) })
