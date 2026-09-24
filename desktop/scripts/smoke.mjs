// Smoke test for a packaged app: start it on a scratch data directory, wait for the Fleet it bundles, check the UI
// and API, list a repository (which runs git from Fleet), then kill the app the hard way and check its Fleet stops
// too and gives up the database lock.
//
//   node scripts/smoke.mjs <app executable> [app arguments...]
//
// Linux needs a display (xvfb-run) and, for an unpacked build, --no-sandbox.
import { execFileSync, spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const [executable, ...appArgs] = process.argv.slice(2);
if (!executable) {
  console.error("usage: node scripts/smoke.mjs <app executable> [app arguments...]");
  process.exit(2);
}

const root = fs.mkdtempSync(path.join(os.tmpdir(), "fleet-smoke-"));
const dataDir = path.join(root, "data");
const instanceFile = path.join(dataDir, "fleet.instance.json");
const reposDir = path.join(root, "repos");
const repoDir = path.join(reposDir, "smoke-repo");
fs.mkdirSync(repoDir, { recursive: true });
fs.writeFileSync(path.join(repoDir, "README.md"), "smoke\n");
const git = (...args) => execFileSync("git", ["-C", repoDir, ...args], { stdio: "ignore" });
git("init", "-q");
git("add", ".");
git("-c", "user.name=smoke", "-c", "user.email=smoke@example.com", "commit", "-q", "-m", "smoke");
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const alive = (pid) => {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return error.code === "EPERM";
  }
};
const readInstance = () => {
  try {
    return JSON.parse(fs.readFileSync(instanceFile, "utf8"));
  } catch {
    return null;
  }
};
async function until(what, check, ms) {
  const deadline = Date.now() + ms;
  while (Date.now() < deadline) {
    const value = await check();
    if (value) return value;
    await sleep(500);
  }
  throw new Error(`Timed out waiting for ${what}`);
}

const app = spawn(executable, [...appArgs, `--user-data-dir=${path.join(root, "user-data")}`], {
  env: { ...process.env, FLEET_DESKTOP_DATA_DIR: dataDir, FLEET_HARNESS: "test", FLEET_WORKSPACE_ROOTS: reposDir },
  stdio: "inherit",
});
let appExited = false;
app.on("exit", () => {
  appExited = true;
});

let failed = false;
try {
  const instance = await until(
    "the app's Fleet to write its instance file",
    () => {
      if (appExited) throw new Error("The app exited before its Fleet started");
      const value = readInstance();
      return value?.desktop === true && value;
    },
    120_000,
  );
  console.log(`Fleet ${instance.version} is up at ${instance.url} (pid ${instance.pid})`);

  const ready = await fetch(`${instance.url}/readyz`);
  if (!ready.ok) throw new Error(`/readyz answered ${ready.status}`);
  const page = await (await fetch(`${instance.url}/`)).text();
  if (!page.includes('id="app"')) throw new Error("Fleet didn't serve its UI from the bundle");
  const status = await fetch(`${instance.url}/api/desktop/status`);
  if (!status.ok) throw new Error(`/api/desktop/status answered ${status.status}`);
  console.log(`UI served, desktop status ${JSON.stringify(await status.json())}`);

  // Listing repositories runs git for each one. On Windows a git that inherited Fleet's standard input (the app's
  // pipe, which Fleet is reading) hung forever, so the list never came (issue #301).
  const started = Date.now();
  const repos = await fetch(`${instance.url}/api/repositories`, { signal: AbortSignal.timeout(30_000) }).catch((error) => {
    throw new Error(`/api/repositories didn't answer: ${error.message}`);
  });
  if (!repos.ok) throw new Error(`/api/repositories answered ${repos.status}`);
  const { repositories } = await repos.json();
  if (!repositories.some((repository) => repository.name === "smoke-repo")) {
    throw new Error(`/api/repositories didn't list the smoke repository: ${JSON.stringify(repositories)}`);
  }
  console.log(`Listed the smoke repository in ${Date.now() - started} ms.`);

  app.kill("SIGKILL");
  await until("the app's Fleet to stop after the app was killed", () => !alive(instance.pid), 30_000);
  await until("the instance file to go", () => !fs.existsSync(instanceFile), 10_000);
  console.log("Killing the app stopped its Fleet and released the lock.");
} catch (error) {
  failed = true;
  console.error(`Smoke test failed: ${error.message}`);
  for (const log of findLogs(root)) {
    console.error(`\n── ${log}\n${fs.readFileSync(log, "utf8").slice(-8000)}`);
  }
} finally {
  if (!appExited) app.kill("SIGKILL");
  const leftover = readInstance();
  if (leftover && alive(leftover.pid)) process.kill(leftover.pid, "SIGKILL");
}
process.exit(failed ? 1 : 0);

function findLogs(dir) {
  const found = [];
  const visit = (current, depth) => {
    if (depth > 6) return;
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      const full = path.join(current, entry.name);
      if (entry.isDirectory()) visit(full, depth + 1);
      else if (entry.name === "fleet-server.log") found.push(full);
    }
  };
  const candidates = [dir, path.join(os.homedir(), ".config", "Fleet"), path.join(os.homedir(), "Library", "Logs", "Fleet")];
  if (process.env.APPDATA) candidates.push(path.join(process.env.APPDATA, "Fleet"));
  for (const candidate of candidates) {
    if (fs.existsSync(candidate)) visit(candidate, 0);
  }
  return found;
}
