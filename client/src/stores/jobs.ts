import { defineStore } from "pinia";
import { shallowRef } from "vue";
import { apiFetchOn } from "@/lib/api-client";
import { extractApiError } from "@/lib/api-error";
import type { Job, JobMachine, JobRepository, JobsResponse, NewJob } from "@/lib/jobs";

/** How often the open screen asks again: Fleet starts and ends jobs by itself, and live events come later. */
export const JOBS_POLL_MS = 3000;

async function errorFrom(response: Response, fallback: string): Promise<string> {
  try {
    const body = (await response.json()) as { error?: unknown };
    return extractApiError(body.error ?? body, fallback);
  } catch {
    return fallback;
  }
}

/**
 * The queue and each machine's seats. They belong to the home Fleet, which does the matching, so every request goes
 * there (`null`) whichever machine is live. Nothing asks until `load` or `watch` is called, which only the Jobs screen
 * and Settings → Machines do, and only with Jobs on.
 */
export const useJobsStore = defineStore("jobs", () => {
  const jobs = shallowRef<Job[]>([]);
  const machines = shallowRef<JobMachine[]>([]);
  const isLoaded = shallowRef(false);
  const error = shallowRef<string | null>(null);
  let timer: ReturnType<typeof setInterval> | null = null;
  let watchers = 0;

  async function load(): Promise<void> {
    try {
      const response = await apiFetchOn(null, "/api/jobs");
      if (!response.ok) {
        error.value = await errorFrom(response, "Couldn't load the jobs.");
        return;
      }

      const body = (await response.json()) as JobsResponse;
      jobs.value = body.jobs;
      machines.value = body.machines;
      error.value = null;
      isLoaded.value = true;
    } catch {
      error.value = "Couldn't reach Fleet.";
    }
  }

  /** Loads now and every few seconds until the returned function is called; several screens can watch at once. */
  function watch(): () => void {
    watchers++;
    void load();
    timer ??= setInterval(() => void load(), JOBS_POLL_MS);

    let stopped = false;
    return () => {
      if (stopped) return;
      stopped = true;
      watchers--;
      if (watchers === 0 && timer) {
        clearInterval(timer);
        timer = null;
      }
    };
  }

  async function send(path: string, method: string, body: unknown, fallback: string): Promise<Response> {
    const response = await apiFetchOn(null, path, {
      method,
      headers: { "Content-Type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!response.ok) throw new Error(await errorFrom(response, fallback));
    return response;
  }

  async function post(job: NewJob): Promise<void> {
    await send("/api/jobs", "POST", job, "Couldn't post the job.");
    await load();
  }

  /** Only a waiting job; one that started is handled in its session. */
  async function cancel(id: string): Promise<void> {
    await send(`/api/jobs/${encodeURIComponent(id)}`, "DELETE", undefined, "Couldn't cancel the job.");
    await load();
  }

  /** How many jobs the machine runs at once, and its labels. A field left out stays as it is. */
  async function saveMachine(id: string, change: { seats?: number; labels?: string[] }): Promise<void> {
    const response = await send(`/api/jobs/machines/${encodeURIComponent(id)}`, "PUT", change, "Couldn't save the seats.");
    const saved = (await response.json()) as JobMachine;
    machines.value = machines.value.map((machine) => (machine.id === saved.id ? saved : machine));
  }

  async function loadRepositories(): Promise<JobRepository[]> {
    const response = await apiFetchOn(null, "/api/jobs/repositories");
    if (!response.ok) throw new Error(await errorFrom(response, "Couldn't list the repositories."));
    return (await response.json()) as JobRepository[];
  }

  return { jobs, machines, isLoaded, error, load, watch, post, cancel, saveMachine, loadRepositories };
});
