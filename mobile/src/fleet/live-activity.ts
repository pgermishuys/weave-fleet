// Live Activities are iOS only (see live-activity.ios.ts). Android shows an ongoing notification instead.
export interface ActivityState {
  sessionId: string;
  title: string;
  state: string;
  detail: string;
  startedAt: number;
  needsYou: boolean;
}

export async function syncLiveActivity(_activity: ActivityState): Promise<void> {}
export async function endLiveActivity(_sessionId: string): Promise<void> {}
