<script setup lang="ts">
import { computed } from "vue";
import { Check, Monitor } from "lucide-vue-next";
import SettingGroup from "@/components/settings/SettingGroup.vue";
import SettingRow from "@/components/settings/SettingRow.vue";
import {
  useThemeStore,
  themes,
  fontSizes,
  type FontFamily,
  type FontSize,
  type ThemeSelection,
  type ThinkingDisplay,
} from "@/stores/theme";
import { codeThemes, resolveCodePalette, resolveCodeThemeId, type CodePalette, type CodeThemeSelection } from "@/lib/code-themes";

const themeStore = useThemeStore();

const activeTheme = computed(() => themeStore.currentTheme);
const activeCodeTheme = computed(() => themeStore.codeTheme);

interface CodeThemeCard {
  id: CodeThemeSelection;
  label: string;
  /** What "Match theme" currently resolves to, so its card says which scheme it is. */
  note?: string;
  palette: CodePalette;
}

/** One card per scheme, each previewed in its own colours for the current light/dark setting. */
const codeThemeCards = computed<CodeThemeCard[]>(() => {
  const theme = themeStore.resolvedTheme;
  const matched = codeThemes.find((t) => t.id === resolveCodeThemeId("match", theme.id));
  return [
    { id: "match", label: "Match theme", note: matched?.label, palette: resolveCodePalette("match", theme.id, theme.colorScheme) },
    ...codeThemes.map((t) => ({ id: t.id, label: t.label, palette: resolveCodePalette(t.id, theme.id, theme.colorScheme) })),
  ];
});
const activeFontFamily = computed(() => themeStore.fontFamily);
const activeFontSize = computed(() => themeStore.fontSize);

const thinkingOptions: ReadonlyArray<{ id: ThinkingDisplay; label: string; help: string }> = [
  { id: "folded", label: "Folded", help: "One line per block, with its gist. Click a line to open it." },
  { id: "open", label: "Open", help: "Every block in full." },
  { id: "hidden", label: "Hidden", help: "Not shown. The harness and the history still keep it." },
];

const activeThinking = computed(() => thinkingOptions.find((option) => option.id === themeStore.thinking) ?? thinkingOptions[0]);

const fontOptions: ReadonlyArray<{ id: FontFamily; label: string; fontFamily: string }> = [
  {
    id: "inter",
    label: "Inter",
    fontFamily: '"Inter Variable", "Inter", system-ui, -apple-system, sans-serif',
  },
];

function selectTheme(theme: ThemeSelection): void {
  themeStore.setTheme(theme);
}

function selectCodeTheme(selection: CodeThemeSelection): void {
  themeStore.setCodeTheme(selection);
}

function selectFontFamily(fontFamily: FontFamily): void {
  themeStore.setFontFamily(fontFamily);
}

function selectFontSize(size: FontSize): void {
  themeStore.setFontSize(size);
}
</script>

<template>
  <div class="grid gap-6">
    <SettingGroup
      id="look"
      title="Look"
    >
      <SettingRow
        id="theme"
        title="Theme"
        :summary="`Active: ${themeStore.resolvedTheme.label}. System follows your computer.`"
      >
        <template #below>
          <div class="mt-3">
            <div class="grid gap-3 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4">
              <!-- System option -->
              <button
                type="button"
                class="flex flex-col gap-3 rounded-card border p-3 text-left transition-colors"
                :class="activeTheme === 'system'
                  ? 'border-accent bg-accent-dim'
                  : 'border-border hover:border-accent/50'"
                @click="selectTheme('system')"
              >
                <div class="flex h-10 items-center justify-center rounded-btn border border-border/60 bg-gradient-to-r from-[#0a0a0b] via-[#0a0a0b] to-[#f1f5f9]">
                  <Monitor
                    :size="14"
                    class="text-white/70"
                  />
                </div>

                <div class="flex items-center justify-between gap-2">
                  <span class="text-xs font-medium text-text">System</span>
                  <Check
                    v-if="activeTheme === 'system'"
                    :size="14"
                    class="text-accent"
                  />
                </div>
              </button>

              <!-- Theme options -->
              <button
                v-for="theme in themes"
                :key="theme.id"
                type="button"
                class="flex flex-col gap-3 rounded-card border p-3 text-left transition-colors"
                :class="activeTheme === theme.id
                  ? 'border-accent bg-accent-dim'
                  : 'border-border hover:border-accent/50'"
                @click="selectTheme(theme.id)"
              >
                <!-- Mini preview swatch -->
                <div
                  class="flex h-10 items-end gap-0.5 overflow-hidden rounded-btn border border-border/60 p-1.5"
                  :style="{ backgroundColor: theme.swatches[0] }"
                >
                  <div
                    class="h-full flex-1 rounded-sm"
                    :style="{ backgroundColor: theme.swatches[1] }"
                  />
                  <div
                    class="h-3/4 w-2 rounded-sm"
                    :style="{ backgroundColor: theme.swatches[2] }"
                  />
                  <div
                    class="h-1/2 w-2 rounded-sm"
                    :style="{ backgroundColor: theme.swatches[3], opacity: 0.6 }"
                  />
                </div>

                <div class="flex items-center justify-between gap-2">
                  <span class="text-xs font-medium text-text">{{ theme.label }}</span>
                  <Check
                    v-if="activeTheme === theme.id"
                    :size="14"
                    class="text-accent"
                  />
                </div>
              </button>
            </div>
          </div>
        </template>
      </SettingRow>
      <SettingRow
        id="code-theme"
        title="Code theme"
        summary="Syntax colours for files and code blocks. Match theme uses the scheme named after your theme, or One Dark."
      >
        <template #below>
          <div class="mt-3">
            <div
              class="grid gap-3 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4"
              role="group"
              aria-label="Code theme"
            >
              <button
                v-for="card in codeThemeCards"
                :key="card.id"
                type="button"
                class="flex flex-col gap-3 rounded-card border p-3 text-left transition-colors"
                :class="activeCodeTheme === card.id
                  ? 'border-accent bg-accent-dim'
                  : 'border-border hover:border-accent/50'"
                :aria-pressed="activeCodeTheme === card.id"
                :data-code-theme="card.id"
                @click="selectCodeTheme(card.id)"
              >
                <!-- Mini code preview in the scheme's colours -->
                <pre
                  class="code-theme-preview m-0 overflow-hidden rounded-btn border border-border/60 bg-card-bg px-2 py-1.5 font-mono text-[10px] leading-[1.5] text-text"
                  aria-hidden="true"
                ><span
  data-token="keyword"
  :style="{ color: card.palette.keyword }"
>const</span> <span
  data-token="function"
  :style="{ color: card.palette.function }"
>run</span> = (<span
  data-token="attr"
  :style="{ color: card.palette.attr }"
>name</span>) => <span
  data-token="literal"
  :style="{ color: card.palette.literal }"
>true</span>;
<span
  data-token="string"
  :style="{ color: card.palette.string }"
>"nord"</span>: <span
  data-token="number"
  :style="{ color: card.palette.number }"
>42</span> <span
  data-token="comment"
  :style="{ color: card.palette.comment }"
>// ok</span>
<span
  data-token="type"
  :style="{ color: card.palette.type }"
>Fleet</span>.<span
  data-token="function"
  :style="{ color: card.palette.function }"
>start</span>()</pre>

                <div class="flex items-center justify-between gap-2">
                  <span class="text-xs font-medium text-text">
                    {{ card.label }}
                    <span
                      v-if="card.note"
                      class="text-muted"
                    >· {{ card.note }}</span>
                  </span>
                  <Check
                    v-if="activeCodeTheme === card.id"
                    :size="14"
                    class="text-accent"
                  />
                </div>
              </button>
            </div>
          </div>
        </template>
      </SettingRow>
      <SettingRow
        id="font"
        title="Font"
        summary="The interface font."
      >
        <template #control>
          <div class="grid gap-3 sm:grid-cols-2">
            <button
              v-for="font in fontOptions"
              :key="font.id"
              type="button"
              class="flex items-center justify-between gap-3 rounded-card border p-3 text-left transition-colors"
              :class="activeFontFamily === font.id
                ? 'border-accent bg-accent-dim'
                : 'border-border hover:border-accent/50'"
              :style="{ fontFamily: font.fontFamily }"
              @click="selectFontFamily(font.id)"
            >
              <span class="text-sm font-medium text-text">{{ font.label }}</span>
              <Check
                v-if="activeFontFamily === font.id"
                :size="14"
                class="shrink-0 text-accent"
              />
            </button>
          </div>
        </template>
      </SettingRow>
      <SettingRow
        id="font-size"
        title="Font size"
        summary="The base size of the interface."
      >
        <template #control>
          <div class="inline-flex gap-0 rounded-card border border-border">
            <button
              v-for="(size, index) in fontSizes"
              :key="size.id"
              type="button"
              class="flex items-center justify-center px-4 py-2 text-sm font-medium transition-colors"
              :class="[
                activeFontSize === size.id
                  ? 'bg-accent text-white'
                  : 'text-muted hover:text-text hover:bg-card-bg',
                index === 0 ? 'rounded-l-card' : '',
                index === fontSizes.length - 1 ? 'rounded-r-card' : '',
                index > 0 ? 'border-l border-border' : '',
              ]"
              @click="selectFontSize(size.id)"
            >
              {{ size.label }}
            </button>
          </div>
        </template>
      </SettingRow>
    </SettingGroup>

    <SettingGroup
      id="conversation"
      title="Conversation"
    >
      <SettingRow
        id="thinking"
        title="Thinking"
        :summary="`${activeThinking.help}`"
      >
        <template #control>
          <div
            class="inline-flex self-start gap-0 rounded-card border border-border"
            role="group"
            aria-label="Thinking"
          >
            <button
              v-for="(option, index) in thinkingOptions"
              :key="option.id"
              type="button"
              class="flex items-center justify-center px-4 py-2 text-sm font-medium transition-colors"
              :class="[
                activeThinking.id === option.id
                  ? 'bg-accent text-white'
                  : 'text-muted hover:text-text hover:bg-card-bg',
                index === 0 ? 'rounded-l-card' : '',
                index === thinkingOptions.length - 1 ? 'rounded-r-card' : '',
                index > 0 ? 'border-l border-border' : '',
              ]"
              :aria-pressed="activeThinking.id === option.id"
              :data-testid="`thinking-${option.id}`"
              @click="themeStore.setThinking(option.id)"
            >
              {{ option.label }}
            </button>
          </div>
        </template>
      </SettingRow>
    </SettingGroup>
  </div>
</template>
