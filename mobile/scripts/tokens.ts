// Writes src/theme/tokens.generated.ts from desktop Fleet's tokens, so the phone and the desktop can't drift apart.
//   bun scripts/tokens.ts           regenerate
//   bun scripts/tokens.ts --check   exit 1 when the generated file is out of date
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { generate } from "./tokens-lib";

const root = resolve(import.meta.dir, "..");
const source = resolve(root, "../client/src/assets/main.css");
const target = resolve(root, "src/theme/tokens.generated.ts");

if (process.argv[1]?.endsWith("tokens.ts")) {
  const next = generate(readFileSync(source, "utf8"));
  if (process.argv.includes("--check")) {
    if (!existsSync(target) || readFileSync(target, "utf8") !== next) {
      console.error("src/theme/tokens.generated.ts is out of date with client/src/assets/main.css. Run `bun run tokens` in mobile/.");
      process.exit(1);
    }
  } else {
    writeFileSync(target, next);
    console.log("Wrote src/theme/tokens.generated.ts");
  }
}
