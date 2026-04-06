import { defineConfig, globalIgnores } from "eslint/config";
import tseslint from "typescript-eslint";
import reactPlugin from "eslint-plugin-react";
import reactHooksPlugin from "eslint-plugin-react-hooks";

const eslintConfig = defineConfig([
  ...tseslint.configs.recommended,
  {
    plugins: { react: reactPlugin },
    settings: { react: { version: "detect" } },
  },
  {
    plugins: { "react-hooks": reactHooksPlugin },
    rules: reactHooksPlugin.configs.recommended.rules,
  },
  globalIgnores([
    // Weave internal files (plans, spikes, state):
    ".weave/**",
    // Build artifacts:
    "dist/**",
    // Stale Next.js build cache (delete with task 35):
    ".next/**",
  ]),
]);

export default eslintConfig;
