// Lists the files outside mobile/ that the phone app runs (see phone-files-lib.ts).
//   bun scripts/phone-files.ts                    every shared file the app's bundle can load
//   bun scripts/phone-files.ts --changed <file>   of the changed paths in <file> (one per line), the ones that can
//                                                 alter what the phone draws; prints nothing when none can
import { readFileSync } from "node:fs";
import { filesReachingPhone, sharedRuntimeFiles } from "./phone-files-lib";

const shared = sharedRuntimeFiles();
const at = process.argv.indexOf("--changed");
if (at === -1) {
  console.log(shared.join("\n"));
} else {
  const changed = readFileSync(process.argv[at + 1], "utf8").split("\n").map((l) => l.trim()).filter(Boolean);
  const hits = filesReachingPhone(changed, shared);
  if (hits.length > 0) console.log(hits.join("\n"));
}
