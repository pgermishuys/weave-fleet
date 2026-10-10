import { checkMod } from "../../src/check";
import { fakeCheck } from "./fake-check";

async function probe(): Promise<boolean> {
  try {
    await checkMod(new URL("../fixtures/mods/test-chips", import.meta.url).pathname);
    return true;
  } catch (e) {
    return !(e instanceof Error && e.message === "not implemented");
  }
}

/** The real static check when it exists, the stand-in until then. */
export const checkUsed: "real" | "fake" = (await probe()) ? "real" : "fake";
export const check: typeof checkMod = checkUsed === "real" ? checkMod : fakeCheck;
