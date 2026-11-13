/**
 * Seed a scratch Fleet (test harness) with invented, realistically sized data:
 * several git projects, ~150 sessions, a few long conversations with many turns and tool calls,
 * a handful of sessions primed to stream slowly on their next prompt, and spare replies on the
 * long sessions for the send-a-prompt measurement.
 *
 * Every conversation is played through the real API (POST /api/sessions, POST /prompt), so it is
 * stored the way the version under test stores it, whatever its schema.
 */

import { execFileSync } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { answer, PROJECTS, pick, prompt, rng, title, toolCalls } from "./content.ts";

export interface SeedSpec {
  sessions: number;
  longSessions: number;
  longTurns: number;
  mediumSessions: number;
  mediumTurns: number;
  streamers: number;
  spareReplies: number;
  /** Slow streaming replies queued on each streamer, one per run of the streaming scenario. */
  streamRuns: number;
}

export const DEFAULT_SPEC: SeedSpec = {
  sessions: 150,
  longSessions: 3,
  longTurns: 60,
  mediumSessions: 12,
  mediumTurns: 10,
  streamers: 4,
  spareReplies: 40,
  streamRuns: 20,
};

export interface SeededSession {
  id: string;
  title: string;
  project: string;
  kind: "long" | "medium" | "short";
  turns: number;
  streamer: boolean;
  /** The last prompt sent: seeing it on screen proves the conversation is shown. */
  lastPrompt: string;
}

export interface SeedResult {
  sessions: SeededSession[];
  long: string[];
  streamers: string[];
  changesProject: string;
}

type Ev = { type: string; delayMs?: number; payload: unknown };

let clock = Date.UTC(2026, 9, 1, 9, 0, 0);

export function turnEvents(msgId: string, r: () => number, opts: { tools: number; streamChunks?: number; chunkDelayMs?: number }): Ev[] {
  const sid = "_placeholder_";
  const created = (clock += 60_000);
  const ev: Ev[] = [
    { type: "session.status", payload: { sessionId: sid, status: { type: "busy" } } },
    { type: "message.updated", payload: { info: { id: msgId, sessionID: sid, role: "assistant", modelID: "orbit-large", providerID: "acme", time: { created: created + 1 } } } },
  ];
  const calls = toolCalls(r, opts.tools);
  const streaming = (opts.streamChunks ?? 0) > 0;
  calls.forEach((c, i) => {
    const part = { type: "tool", id: `${msgId}-t${i}`, callID: `${msgId}-call${i}`, tool: c.tool, sessionID: sid, messageID: msgId };
    const start = created + 2 + i * 1000;
    ev.push({ type: "message.part.updated", delayMs: streaming ? opts.chunkDelayMs : 0, payload: { sessionID: sid, part: { ...part, state: { status: "running", input: c.input, time: { start } } } } });
    ev.push({
      type: "message.part.updated", delayMs: streaming ? opts.chunkDelayMs : 0,
      payload: { sessionID: sid, part: { ...part, state: { status: "completed", input: c.input, output: c.output, title: c.title, metadata: c.metadata ?? {}, time: { start, end: start + 800 } } } },
    });
  });
  const text = answer(r);
  const textPart = { type: "text", id: `${msgId}-text`, sessionID: sid, messageID: msgId };
  if (streaming) {
    const n = opts.streamChunks!;
    for (let k = 1; k <= n; k++) {
      ev.push({ type: "message.part.updated", delayMs: opts.chunkDelayMs, payload: { sessionID: sid, part: { ...textPart, text: text.slice(0, Math.ceil((text.length * k) / n)) } } });
    }
  } else {
    ev.push({ type: "message.part.updated", payload: { sessionID: sid, part: { ...textPart, text } } });
  }
  ev.push({
    type: "message.updated",
    payload: { info: { id: msgId, sessionID: sid, role: "assistant", modelID: "orbit-large", providerID: "acme", time: { created: created + 1, completed: created + 30_000 }, tokens: { input: 12000, output: 900, reasoning: 0, cache: { read: 8000, write: 0 } }, cost: 0.04 } },
  });
  ev.push({ type: "session.status", payload: { sessionId: sid, status: { type: "idle" } } });
  ev.push({ type: "session.idle", payload: { sessionId: sid } });
  return ev;
}

function git(cwd: string, ...args: string[]) {
  execFileSync("git", ["-c", "user.name=Seed", "-c", "user.email=seed@example.invalid", ...args], { cwd, stdio: "ignore" });
}

function makeProject(dir: string, r: () => number, dirty: boolean) {
  mkdirSync(join(dir, "src"), { recursive: true });
  for (let i = 0; i < 12; i++) {
    writeFileSync(join(dir, "src", `module${i}.ts`), Array.from({ length: 80 }, (_, j) => `export const value${i}_${j} = ${Math.floor(r() * 1000)};`).join("\n") + "\n");
  }
  writeFileSync(join(dir, "README.md"), `# ${dir.split(/[\\/]/).pop()}\n\nAn invented project for the snappiness check.\n`);
  git(dir, "init", "-q", "-b", "main");
  git(dir, "add", ".");
  git(dir, "commit", "-q", "-m", "Initial commit");
  if (dirty) {
    for (let i = 0; i < 8; i++) {
      writeFileSync(join(dir, "src", `module${i}.ts`), Array.from({ length: 80 }, (_, j) => `export const value${i}_${j} = ${j % 7 === 0 ? "changed" + j : Math.floor(r() * 1000)};`).join("\n") + "\n");
    }
    writeFileSync(join(dir, "src", "new-feature.ts"), "export function newFeature() {\n  return 42;\n}\n");
  }
}

async function api(baseUrl: string, path: string, init?: RequestInit): Promise<any> {
  const r = await fetch(baseUrl + path, { ...init, headers: { "Content-Type": "application/json", ...(init?.headers ?? {}) } });
  const text = await r.text();
  if (!r.ok) throw new Error(`${init?.method ?? "GET"} ${path} -> ${r.status}: ${text.slice(0, 300)}`);
  return text ? JSON.parse(text) : null;
}

async function waitTurn(baseUrl: string, id: string, assistantId: string) {
  const deadline = Date.now() + 30_000;
  while (Date.now() < deadline) {
    const msgs = await fetch(`${baseUrl}/api/sessions/${id}/messages?limit=4`).then((r) => r.text());
    if (msgs.includes(assistantId)) {
      const st = await api(baseUrl, `/api/sessions/${id}/status`);
      if (JSON.stringify(st).includes('"idle"')) return;
    }
    await new Promise((r) => setTimeout(r, 40));
  }
  const last = await fetch(`${baseUrl}/api/sessions/${id}/messages?limit=4`).then((r) => r.text());
  const st = await fetch(`${baseUrl}/api/sessions/${id}/status`).then((r) => r.text());
  throw new Error(`Turn ${assistantId} on ${id} never finished: ${st} ${last.slice(0, 600)}`);
}

export async function seed(baseUrl: string, projectsDir: string, scenarioDir: string, spec: SeedSpec = DEFAULT_SPEC): Promise<SeedResult> {
  const r = rng(42);
  const projectDirs = PROJECTS.map((p, i) => {
    const dir = join(projectsDir, p);
    makeProject(dir, r, i === 0);
    return dir;
  });

  await api(baseUrl, "/api/workspace-roots", { method: "POST", body: JSON.stringify({ path: projectsDir }) }).catch((e) => {
    if (!String(e).includes("409")) console.warn(`workspace-roots: ${e}`);
  });

  // One Fleet project per folder, so the session list groups the way a real one does.
  const projectIds: string[] = [];
  for (const p of PROJECTS) {
    const created = await api(baseUrl, "/api/projects", { method: "POST", body: JSON.stringify({ name: p }) });
    projectIds.push(created.id);
  }

  const plan: { kind: SeededSession["kind"]; turns: number; streamer: boolean }[] = [];
  for (let i = 0; i < spec.sessions; i++) {
    if (i < spec.longSessions) plan.push({ kind: "long", turns: spec.longTurns, streamer: false });
    else if (i < spec.longSessions + spec.mediumSessions) plan.push({ kind: "medium", turns: spec.mediumTurns, streamer: i < spec.longSessions + spec.streamers });
    else plan.push({ kind: "short", turns: 1 + Math.floor(r() * 2), streamer: false });
  }

  const sessions: SeededSession[] = [];
  // Short sessions first (oldest), then the long and medium ones in random order: the list shows the 100 most recent
  // sessions, and the sessions you work in are among them.
  const shuffled = plan.map((_, i) => i).sort(() => r() - 0.5);
  const order = [...shuffled.filter((i) => plan[i]!.kind === "short"), ...shuffled.filter((i) => plan[i]!.kind !== "short")];
  let n = 0;
  for (const idx of order) {
    const p = plan[idx]!;
    const project = idx === 0 ? PROJECTS[0]! : pick(r, PROJECTS);
    const dir = projectDirs[PROJECTS.indexOf(project)]!;
    const scenarioId = `snap-${idx}`;
    const msgIds = Array.from({ length: p.turns }, (_, t) => `m${idx}x${t}`);
    const tools = p.kind === "long" ? () => 4 + Math.floor(r() * 7) : () => 1 + Math.floor(r() * 4);
    const responses = msgIds.map((m) => ({ events: turnEvents(m, r, { tools: tools() }) }));
    if (p.streamer) {
      for (let s = 0; s < spec.streamRuns; s++) responses.push({ events: turnEvents(`m${idx}stream${s}`, r, { tools: 3, streamChunks: 60, chunkDelayMs: 100 }) });
    }
    if (p.kind === "long") {
      for (let s = 0; s < spec.spareReplies; s++) responses.push({ events: turnEvents(`m${idx}spare${s}`, r, { tools: 1 }) });
    }
    writeFileSync(join(scenarioDir, `${scenarioId}.json`), JSON.stringify({ promptResponses: responses }));

    const t = p.kind === "long" ? `Long: ${title(r)}` : title(r);
    // The test harness reads this at spawn (TestHarnessRuntime.NextScenarioFromFile); spawn happens inside the POST.
    writeFileSync(join(scenarioDir, "next-scenario.txt"), scenarioId);
    const created = await api(baseUrl, "/api/sessions", { method: "POST", body: JSON.stringify({ directory: dir, title: t, projectId: projectIds[PROJECTS.indexOf(project)] }) });
    const id: string = created.session?.id ?? created.id;
    let lastPrompt = "";
    for (let k = 0; k < p.turns; k++) {
      lastPrompt = prompt(r, k);
      await api(baseUrl, `/api/sessions/${id}/prompt`, { method: "POST", body: JSON.stringify({ text: lastPrompt }) });
      await waitTurn(baseUrl, id, msgIds[k]!);
    }
    sessions.push({ id, title: t, project, kind: p.kind, turns: p.turns, streamer: p.streamer, lastPrompt });
    if (++n % 25 === 0) console.log(`  seeded ${n}/${spec.sessions} sessions`);
  }

  return {
    sessions,
    long: sessions.filter((s) => s.kind === "long").map((s) => s.id),
    streamers: sessions.filter((s) => s.streamer).map((s) => s.id),
    changesProject: PROJECTS[0]!,
  };
}
