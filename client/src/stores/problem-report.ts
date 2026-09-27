import { defineStore } from "pinia";
import { shallowRef } from "vue";
import { captureScreenshot, type ReportSource, type Screenshot } from "@/lib/problem-report";

export interface ReportOpening {
  from: ReportSource;
  /** The session the report is about; the open session when not given. */
  sessionId?: string | null;
  /** Text to start the description with, such as the error a failure card showed. */
  description?: string;
}

/** Long enough for a closing menu's fade (150 ms) to finish before the screenshot. */
const MENU_CLOSE_MS = 200;

/**
 * Help → Report a problem: whether the report is open, and the screenshot taken when it was asked for. The screenshot
 * is taken before the report window opens, so it shows what the person was looking at.
 */
export const useProblemReportStore = defineStore("problem-report", () => {
  const open = shallowRef(false);
  const opening = shallowRef<ReportOpening | null>(null);
  const screenshot = shallowRef<Screenshot | null>(null);
  const capturing = shallowRef(false);

  async function show(request: ReportOpening): Promise<void> {
    if (open.value || capturing.value) return;
    capturing.value = true;
    try {
      // A menu the report was chosen from is still closing; let it go first.
      await new Promise((resolve) => setTimeout(resolve, MENU_CLOSE_MS));
      screenshot.value = await captureScreenshot();
    } finally {
      capturing.value = false;
    }
    opening.value = request;
    open.value = true;
  }

  /** Takes the screenshot again with the report window out of the way. */
  async function retake(): Promise<void> {
    if (!open.value) return;
    open.value = false;
    capturing.value = true;
    try {
      // Let the window close before drawing the page.
      await new Promise((resolve) => setTimeout(resolve, 250));
      screenshot.value = await captureScreenshot();
    } finally {
      capturing.value = false;
      open.value = true;
    }
  }

  function hide(): void {
    open.value = false;
    opening.value = null;
    screenshot.value = null;
  }

  return { open, opening, screenshot, capturing, show, retake, hide };
});
