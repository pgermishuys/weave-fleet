import pluginVue from "eslint-plugin-vue";
import { defineConfig, globalIgnores } from "eslint/config";
import tseslint from "typescript-eslint";
import vueParser from "vue-eslint-parser";

const eslintConfig = defineConfig([
  ...pluginVue.configs["flat/recommended"],
  ...tseslint.configs.recommended,
  {
    files: ["**/*.{ts,vue}"],
    languageOptions: {
      parser: vueParser,
      parserOptions: {
        parser: tseslint.parser,
        extraFileExtensions: [".vue"],
        projectService: true,
        sourceType: "module",
      },
    },
    rules: {
      "vue/block-order": [
        "error",
        {
          order: ["script", "template", "style"],
        },
      ],
    },
  },
  {
    files: ["src/components/ui/**/*.vue"],
    rules: {
      "vue/multi-word-component-names": "off",
      "vue/require-default-prop": "off",
    },
  },
  {
    files: [
      "src/components/pages/GitHubWorkItemDetailPage.vue",
      "src/components/session/MessageBubble.vue",
    ],
    rules: {
      "vue/no-v-html": "off",
    },
  },
  {
    // Code working on a session asks the session's machine, not whichever machine is live (see src/lib/machine-target.ts).
    files: [
      "src/components/session/ActivityStream.vue",
      "src/components/session/SessionDetailHeader.vue",
      "src/composables/use-agents.ts",
      "src/composables/use-autocomplete.ts",
      "src/composables/use-message-pagination.ts",
      "src/composables/use-models.ts",
      "src/composables/use-question-answer.ts",
      "src/composables/use-run-shell-command.ts",
      "src/composables/use-send-command.ts",
      "src/composables/use-send-prompt.ts",
      "src/composables/use-send-to-agent.ts",
      "src/composables/use-session-actions.ts",
      "src/composables/use-session-context.ts",
      "src/composables/use-session-lineage.ts",
      "src/composables/use-session-permissions.ts",
      "src/composables/use-session-queue.ts",
      "src/composables/use-session-retry.ts",
      "src/composables/use-side-conversation.ts",
      "src/routes/sessions.$id.tsx",
    ],
    rules: {
      "no-restricted-imports": [
        "error",
        {
          paths: [
            {
              name: "@/api/client",
              importNames: ["api"],
              message: "Ask the session's machine: `useMachineTarget().api`.",
            },
            {
              name: "@/lib/api-client",
              importNames: ["apiFetch", "apiUrl", "wsUrl"],
              message: "Ask the session's machine: `apiFetchOn`/`apiUrlOn`/`wsUrlOn(useMachineTarget().connection, …)`.",
            },
          ],
        },
      ],
    },
  },
  globalIgnores([".next/**", "coverage/**", "dist/**", "src/routeTree.gen.ts"]),
]);

export default eslintConfig;
