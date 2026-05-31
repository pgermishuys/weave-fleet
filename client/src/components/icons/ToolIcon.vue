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
  siSublimetext,
  siWebstorm,
  siWindsurf,
  siXcode,
  siZedindustries,
} from "simple-icons";
import {
  AppWindow,
  Code2,
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
  alacritty: siAlacritty,
  "android-studio": siAndroidstudio,
  clion: siClion,
  cursor: siCursor,
  goland: siGoland,
  intellij: siIntellijidea,
  pycharm: siPycharm,
  rider: siRider,
  sublime: siSublimetext,
  webstorm: siWebstorm,
  windsurf: siWindsurf,
  xcode: siXcode,
  zed: siZedindustries,
};

const lucideIconMap: Record<string, typeof Terminal> = {
  vscode: Code2,
  "vscode-insiders": Code2,
  "visual-studio": AppWindow,
  "fleet-jb": AppWindow,
  rustrover: AppWindow,
  terminal: Terminal,
  wt: SquareTerminal,
  iterm2: SquareTerminal,
  explorer: FolderOpen,
};

const simpleIcon = computed(() => simpleIconMap[props.toolId]);
const LucideIcon = computed(() => lucideIconMap[props.toolId] ?? AppWindow);

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
