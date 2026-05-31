<script setup lang="ts">
import {
  siAlacritty,
  siAndroidstudio,
  siClion,
  siCursor,
  siGoland,
  siIntellijidea,
  siPycharm,
  siRider,
  siRustrover,
  siSublimetext,
  siVisualstudio,
  siVisualstudiocode,
  siWebstorm,
  siWindsurf,
  siWindowsterminal,
  siXcode,
  siZedindustries,
} from "simple-icons";
import {
  FolderOpen,
  SquareTerminal,
  Terminal,
} from "lucide-vue-next";
import { computed } from "vue";

interface Props {
  toolId: string;
  size?: number;
}

const props = withDefaults(defineProps<Props>(), { size: 14 });

interface SimpleIconEntry {
  svg: string;
  hex: string;
}

const simpleIconMap: Record<string, SimpleIconEntry> = {
  "android-studio": siAndroidstudio,
  alacritty: siAlacritty,
  clion: siClion,
  cursor: siCursor,
  goland: siGoland,
  intellij: siIntellijidea,
  pycharm: siPycharm,
  rider: siRider,
  rustrover: siRustrover,
  sublime: siSublimetext,
  "visual-studio": siVisualstudio,
  vscode: siVisualstudiocode,
  "vscode-insiders": siVisualstudiocode,
  webstorm: siWebstorm,
  windsurf: siWindsurf,
  wt: siWindowsterminal,
  xcode: siXcode,
  zed: siZedindustries,
};

const lucideIconMap: Record<string, typeof Terminal> = {
  terminal: Terminal,
  iterm2: SquareTerminal,
  explorer: FolderOpen,
};

const simpleIcon = computed(() => simpleIconMap[props.toolId]);
const LucideIcon = computed(() => lucideIconMap[props.toolId] ?? Terminal);

// Simple icons are solid fills — render slightly smaller than Lucide's stroked icons
// to achieve visual balance
const innerSize = computed(() => Math.round(props.size * 0.85));

const svgContent = computed(() => {
  const icon = simpleIcon.value;
  if (!icon) return null;
  const color = `#${icon.hex}`;
  return icon.svg.replace(
    "<svg",
    `<svg width="${innerSize.value}" height="${innerSize.value}" fill="${color}"`,
  );
});
</script>

<template>
  <span
    v-if="svgContent"
    class="inline-flex shrink-0 items-center justify-center"
    :style="{ width: `${size}px`, height: `${size}px` }"
    v-html="svgContent"
  />
  <component
    :is="LucideIcon"
    v-else
    :width="size"
    :height="size"
    class="shrink-0"
  />
</template>
