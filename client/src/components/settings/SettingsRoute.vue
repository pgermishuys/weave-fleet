<script setup lang="ts">
import { computed, watch } from "vue";
import { useLocation, useRouter } from "@tanstack/vue-router";
import SettingsPage from "@/components/settings/SettingsPage.vue";
import { useSettingsNav, type SettingsSectionId } from "@/composables/use-settings-nav";

const PLUGINS_PATH = "/settings/plugins";

// Settings is one page whose menu picks the section. Plugins and each plugin also have their own address
// (/settings/plugins, /settings/plugins/<id>): the address picks the section, and picking one moves the address.
const router = useRouter();
const { activeSection, setActiveSection } = useSettingsNav();
const pathname = useLocation({ select: (location) => location.pathname });

const pathSection = computed(() => {
  const path = pathname.value.replace(/\/$/, "");
  if (path === PLUGINS_PATH) return "plugins";
  if (path.startsWith(`${PLUGINS_PATH}/`)) return `plugin:${decodeURIComponent(path.slice(PLUGINS_PATH.length + 1))}`;
  return null;
});

const sectionPath = computed(() => {
  if (activeSection.value === "plugins") return PLUGINS_PATH;
  if (activeSection.value.startsWith("plugin:")) return `${PLUGINS_PATH}/${encodeURIComponent(activeSection.value.slice("plugin:".length))}`;
  return "/settings";
});

watch(pathSection, (section) => {
  if (section) setActiveSection(section as SettingsSectionId);
}, { immediate: true });

watch(sectionPath, (path) => {
  if (path !== pathname.value.replace(/(.)\/$/, "$1")) void router.navigate({ to: path });
}, { immediate: true });
</script>

<template>
  <SettingsPage />
</template>
