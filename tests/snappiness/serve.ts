/**
 * Start a scratch Fleet for one checkout, seed it, write seed.json, and keep it running.
 *   bun serve.ts <repoDir> <scratchDir> <port>
 */
import { writeFileSync } from "node:fs";
import { join } from "node:path";
import { startFleet } from "./lib/fleet.ts";
import { DEFAULT_SPEC, seed } from "./lib/seed.ts";

const [repoDir, scratchDir, port] = process.argv.slice(2);
const fleet = await startFleet({ repoDir: repoDir!, scratchDir: scratchDir!, port: Number(port) });
console.log(`fleet ${fleet.baseUrl} pid ${fleet.pid}`);
const t0 = Date.now();
const result = await seed(fleet.baseUrl, fleet.projectsDir, fleet.scenarioDir);
writeFileSync(join(scratchDir!, "seed.json"), JSON.stringify({ baseUrl: fleet.baseUrl, pid: fleet.pid, streamRuns: DEFAULT_SPEC.streamRuns, ...result }, null, 2));
console.log(`seeded ${result.sessions.length} sessions in ${((Date.now() - t0) / 1000).toFixed(0)} s; READY`);
process.on("SIGINT", async () => { await fleet.stop(); process.exit(0); });
await new Promise(() => {});
