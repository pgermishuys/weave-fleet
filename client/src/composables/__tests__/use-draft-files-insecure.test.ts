import { afterEach, expect, it, vi } from "vitest";
import { useDraftFiles } from "../use-draft-files";

afterEach(() => vi.unstubAllGlobals());

it("adds a pasted file over plain http, where crypto.randomUUID is missing", () => {
  vi.stubGlobal("crypto", { getRandomValues: globalThis.crypto.getRandomValues.bind(globalThis.crypto) });
  const { files, addFile } = useDraftFiles("s-insecure");
  addFile(new File(["x"], "shot.png", { type: "image/png" }));
  expect(files.value).toHaveLength(1);
});
