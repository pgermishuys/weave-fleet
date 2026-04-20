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
  globalIgnores([".next/**", "coverage/**", "dist/**", "src/routeTree.gen.ts"]),
]);

export default eslintConfig;
