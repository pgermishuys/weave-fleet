/** The server's preference for Jobs (Settings → Features): off, there's no rail item, no screen and no request. */
export const JOBS_PREFERENCE_KEY = "Jobs";

export const MAX_JOB_SEATS = 16;

export type JobState = "waiting" | "running" | "needs_you" | "done" | "failed";

export interface Job {
  id: string;
  title: string;
  ask: string;
  repository: string;
  labels: string[];
  pinnedMachineId: string | null;
  state: JobState;
  machineId: string | null;
  machineName: string | null;
  sessionId: string | null;
  /** In plain words, why a waiting job hasn't started. */
  whyWaiting: string | null;
  error: string | null;
  createdAt: string;
}

export interface JobMachine {
  id: string;
  name: string;
  isLocal: boolean;
  seats: number;
  used: number;
  labels: string[];
  status: string;
}

export interface JobsResponse {
  jobs: Job[];
  machines: JobMachine[];
}

/** What New job can pick: a repository once, with the machines that have it. */
export interface JobRepository {
  name: string;
  /** What a job names: the origin remote, or the folder name when there is none. */
  repository: string;
  machines: string[];
}

export interface NewJob {
  title: string;
  ask: string;
  repository: string;
  labels: string[];
  pinnedMachineId: string | null;
}

export interface JobColumn {
  id: "waiting" | "running" | "needs_you" | "done";
  title: string;
  jobs: Job[];
}

/** The queue's four columns; a failed job stays in the last, marked, so its reason is read where finished work is. */
export function jobColumns(jobs: readonly Job[]): JobColumn[] {
  const inColumn = (...states: JobState[]) => jobs.filter((job) => states.includes(job.state));
  return [
    { id: "waiting", title: "Waiting", jobs: inColumn("waiting") },
    { id: "running", title: "Running", jobs: inColumn("running") },
    { id: "needs_you", title: "Needs you", jobs: inColumn("needs_you") },
    { id: "done", title: "Done", jobs: inColumn("done", "failed") },
  ];
}

/** Labels typed as `mac, gpu`: trimmed, without blanks or repeats. */
export function parseLabels(text: string): string[] {
  const seen = new Set<string>();
  return text.split(",").map((label) => label.trim()).filter((label) => {
    const key = label.toLowerCase();
    if (!label || seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}
