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
    // Plugins own what they add; core reads what they contribute (src/plugins/registry.ts, slots.ts) and never
    // imports one. Only the composition root (src/routes, src/main.ts) may name a plugin. Tests of a plugin may too.
    // The two later `no-restricted-imports` blocks below replace this one for the files they list, so a file
    // listed there is not covered: keep plugin imports out of them.
    files: ["src/**/*.{ts,vue}"],
    ignores: ["src/plugins/**", "src/routes/**", "src/main.ts", "src/**/__tests__/**", "src/**/*.test.ts"],
    rules: {
      "no-restricted-imports": [
        "error",
        {
          patterns: [
            {
              group: ["@/plugins/builtin/*", "**/plugins/builtin/*"],
              message: "Core doesn't import a plugin. Read what it contributes through `@/plugins/slots`.",
            },
          ],
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
      "src/plugins/builtin/github/pages/GitHubWorkItemDetailPage.vue",
      "src/components/session/MessageBubble.vue",
    ],
    rules: {
      "vue/no-v-html": "off",
    },
  },
  {
    // Code working on a session asks the session's machine, not whichever machine is live (see src/lib/machine-target.ts).
    files: [
      "src/api/session-files.ts",
      "src/components/canvas/AgentTabView.vue",
      "src/components/canvas/BrowserCanvas.vue",
      "src/components/canvas/BrowserOpenDialog.vue",
      "src/components/canvas/CanvasHost.vue",
      "src/components/canvas/FileCanvas.vue",
      "src/components/canvas/PageCanvas.vue",
      "src/components/harness-setup/HarnessSetupRows.vue",
      "src/components/phone/session/ChangesSheet.vue",
      "src/components/phone/session/FilesSheet.vue",
      "src/components/session-context/SessionContextCanvas.vue",
      "src/components/session-context/SessionContextChips.vue",
      "src/components/session-context/SmartLinkCard.vue",
      "src/components/session-context/SmartLinkRow.vue",
      "src/components/session/ActivityStream.vue",
      "src/components/session/BrowserSteps.vue",
      "src/components/session/SessionDetailHeader.vue",
      "src/components/session/ToolScreenshot.vue",
      "src/components/session/activity-stream-tool-card.ts",
      "src/components/sessions/SessionsV2RightPanel.vue",
      "src/components/terminal/TerminalDrawer.vue",
      "src/components/terminal/TerminalView.vue",
      "src/composables/phone/use-machine-reachability.ts",
      "src/composables/use-agent-browser.ts",
      "src/composables/use-agents.ts",
      "src/composables/use-autocomplete.ts",
      "src/composables/use-diff-base.ts",
      "src/composables/use-diffs.ts",
      "src/composables/use-file-browser.ts",
      "src/composables/use-file-live-updates.ts",
      "src/composables/use-find-files.ts",
      "src/composables/use-machine-image.ts",
      "src/composables/use-models.ts",
      "src/composables/use-open-directory.ts",
      "src/composables/use-open-file.ts",
      "src/composables/use-question-answer.ts",
      "src/composables/use-run-shell-command.ts",
      "src/composables/use-send-command.ts",
      "src/composables/use-send-prompt.ts",
      "src/composables/use-send-to-agent.ts",
      "src/composables/use-server-canvases.ts",
      "src/composables/use-session-actions.ts",
      "src/composables/use-session-context.ts",
      "src/composables/use-session-lineage.ts",
      "src/composables/use-session-permissions.ts",
      "src/composables/use-session-progress.ts",
      "src/composables/use-session-queue.ts",
      "src/composables/use-session-recap.ts",
      "src/composables/use-session-retry.ts",
      "src/composables/use-session-stream.ts",
      "src/composables/use-session-terminals.ts",
      "src/composables/use-side-conversation.ts",
      "src/lib/code-editor/buffers.ts",
      "src/lib/terminal-api.ts",
      "src/lib/terminal-socket.ts",
      "src/routes/sessions.$id.tsx",
      "src/stores/app-runs.ts",
      "src/stores/session-progress.ts",
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
            {
              // Its events too: the socket calls take the machine, and here that's the session's.
              name: "@/lib/machine-target",
              importNames: ["liveTarget"],
              message: "Ask the session's machine: `useMachineTarget()`.",
            },
          ],
        },
      ],
    },
  },
  {
    // The open session may be on another machine, whose sessions the live machine's list doesn't hold
    // (see `elsewhere` in src/stores/sessions.ts).
    files: [
      "src/components/canvas/AgentDetail.vue",
      "src/components/canvas/AgentsCanvas.vue",
      "src/components/canvas/ProgressSubagentCard.vue",
      "src/components/layout/StatusBar.vue",
      "src/components/phone/session/PhoneSessionPage.vue",
      "src/components/session-context/SessionContextCanvas.vue",
      "src/components/session/ActivityStream.vue",
      "src/components/session/BackgroundStrip.vue",
      "src/components/session/BackgroundWorkRow.vue",
      "src/components/session/Composer.vue",
      "src/components/session/ContextRing.vue",
      "src/components/session/SessionDetailHeader.vue",
      "src/components/sessions/SessionsV2RightPanel.vue",
      "src/composables/use-commands.ts",
      "src/composables/use-composer-actions.ts",
      "src/composables/use-send-command.ts",
      "src/composables/use-send-prompt.ts",
      "src/composables/use-session-actions.ts",
      "src/composables/use-session-lineage.ts",
      "src/composables/use-session-stream.ts",
      "src/routes/sessions.$id.tsx",
    ],
    rules: {
      "no-restricted-syntax": [
        "error",
        {
          selector: "CallExpression[callee.property.name='find'][callee.object.property.name='sessions']",
          message: "Find a session with `sessionsStore.sessionById(id)`: it also knows sessions on other machines.",
        },
        {
          selector: "CallExpression[callee.property.name='find'][callee.object.property.name='value'][callee.object.object.name='sessions']",
          message: "Find a session with `sessionsStore.sessionById(id)`: it also knows sessions on other machines.",
        },
      ],
    },
  },
  globalIgnores([".next/**", "coverage/**", "dist/**", "src/routeTree.gen.ts"]),
]);

export default eslintConfig;
