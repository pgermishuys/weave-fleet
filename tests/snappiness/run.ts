/**
 * One command: build each ref, start a seeded scratch Fleet per ref, measure them in interleaved rounds, compare.
 *
 *   bun run.ts [--refs v0.60.0,HEAD] [--runs 5] [--rounds 2] [--throttle 1,4] [--work C:\fleet-scratch\snappiness]
 *              [--port 7311] [--only coldStart,switchLong] [--skip-build]
 *
 * HEAD means this checkout. Any other ref gets a detached worktree under --work, with harness.patch applied when
 * the ref predates TestHarnessRuntime.NextScenarioFromFile. Results land in <work>/results; the comparison in
 * <work>/results/compare.md and compare.json.
 */

import { spawn, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";

const args = new Map<string, string>();
for (let i = 2; i < process.argv.length; i++) {
  const k = process.argv[i]!.replace(/^--/, "");
  const v = process.argv[i + 1];
  if (v === undefined || v.startsWith("--")) args.set(k, "true");
  else { args.set(k, v); i++; }
}
const HERE = import.meta.dirname;
const REPO = resolve(HERE, "..", "..");
const WORK = resolve(args.get("work") ?? "C:\\fleet-scratch\\snappiness");
const REFS = (args.get("refs") ?? "v0.60.0,HEAD").split(",");
const RUNS = args.get("runs") ?? "5";
const ROUNDS = Number(args.get("rounds") ?? 2);
const THROTTLES = (args.get("throttle") ?? "1").split(",");
const PORT0 = Number(args.get("port") ?? 7311);
const RESULTS = join(WORK, "results");
mkdirSync(RESULTS, { recursive: true });

function sh(cmd: string, cwd: string, quiet = false) {
  if (!quiet) console.log(`  $ ${cmd}   (${cwd})`);
  const r = spawnSync(cmd, { cwd, shell: true, stdio: quiet ? "pipe" : "inherit", encoding: "utf8" });
  if (r.status !== 0) throw new Error(`failed (${r.status}): ${cmd}\n${r.stderr ?? ""}`);
  return (r.stdout ?? "").trim();
}

function checkout(ref: string): { name: string; dir: string } {
  if (ref === "HEAD") return { name: `main-${sh("git rev-parse --short HEAD", REPO, true)}`, dir: REPO };
  const dir = join(WORK, `wt-${ref.replace(/[^\w.-]/g, "_")}`);
  if (!existsSync(dir)) sh(`git worktree add --detach "${dir}" ${ref}`, REPO);
  const runtime = readFileSync(join(dir, "src", "WeaveFleet.TestHarness", "TestHarnessRuntime.cs"), "utf8");
  if (!runtime.includes("NextScenarioFromFile")) sh(`git apply --3way "${join(HERE, "harness.patch")}"`, dir);
  return { name: ref, dir };
}

function build(dir: string) {
  // Rebuild when the commit or the uncommitted source changes (an applied harness.patch counts).
  const diff = sh("git diff HEAD --ignore-cr-at-eol -- src client/src", dir, true);
  const sha = sh("git rev-parse HEAD", dir, true) + ":" + createHash("sha1").update(diff).digest("hex");
  const marker = join(dir, "client", "dist", ".snappiness-build");
  if (args.has("skip-build") || (existsSync(marker) && readFileSync(marker, "utf8") === sha)) return;
  // Source maps let attribute.ts map CPU profiles to file:line; they don't change what runs.
  sh("bun install --frozen-lockfile", join(dir, "client"));
  sh("bun x vite build --sourcemap", join(dir, "client"));
  sh("bun x vite build -c vite.sw.config.ts", join(dir, "client"));
  sh("dotnet build src/WeaveFleet.Api -c Release -p:SkipModsHost=true -nologo -v q", dir);
  writeFileSync(marker, sha);
}

const targets = REFS.map((ref, i) => ({ ...checkout(ref), port: PORT0 + i }));
for (const t of targets) build(t.dir);

// Serve + seed every ref at once (each its own scratch profile and port).
const servers = targets.map((t) => {
  const scratch = join(WORK, `s-${t.name}`);
  spawnSync(`rmdir /s /q "${scratch}"`, { shell: true });
  const log = join(WORK, `serve-${t.name}.log`);
  const child = spawn("bun", ["serve.ts", t.dir, scratch, String(t.port)], { cwd: HERE, stdio: ["ignore", "pipe", "pipe"], shell: false });
  let out = "";
  child.stdout.on("data", (d) => { out += d; writeFileSync(log, out); });
  child.stderr.on("data", (d) => { out += d; writeFileSync(log, out); });
  const ready = new Promise<void>((res, rej) => {
    const timer = setInterval(() => { if (out.includes("READY")) { clearInterval(timer); res(); } }, 500);
    child.on("exit", (c) => { clearInterval(timer); rej(new Error(`serve for ${t.name} exited ${c}: ${out.slice(-500)}`)); });
  });
  return { ...t, scratch, child, ready };
});

try {
  console.log("seeding...");
  await Promise.all(servers.map((s) => s.ready));
  for (const throttle of THROTTLES) {
    for (let round = 1; round <= ROUNDS; round++) {
      // Alternate the order each round, so background load on the machine falls on both sides.
      const order = round % 2 ? servers : [...servers].reverse();
      for (const s of order) {
        const label = `${s.name}-t${throttle}-r${round}`;
        console.log(`measuring ${label}`);
        const extra = args.get("only") ? ` --only ${args.get("only")}` : "";
        sh(`bun measure.ts --scratch "${s.scratch}" --label ${label} --runs ${RUNS} --throttle ${throttle} --out "${RESULTS}"${extra}`, HERE);
      }
    }
  }
} finally {
  for (const s of servers) {
    // serve.ts stops its Fleet on SIGINT; on Windows kill the tree.
    spawnSync(`taskkill /F /T /PID ${s.child.pid}`, { shell: true });
  }
}

sh(`bun compare.ts --results "${RESULTS}" --base ${targets[0]!.name} --head ${targets[targets.length - 1]!.name} --throttle ${THROTTLES.join(",")}`, HERE);
