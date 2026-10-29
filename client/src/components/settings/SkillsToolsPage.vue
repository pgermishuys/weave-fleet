<script setup lang="ts">
import { computed, onMounted, shallowRef, watch } from "vue";
import ModsSection from "@/components/settings/ModsSection.vue";
import SkillsSection from "@/components/settings/SkillsSection.vue";
import ToolsSection from "@/components/settings/ToolsSection.vue";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { useModsStore } from "@/stores/mods";

/** Skills & tools: Skills and Tools, plus Mods while the Mods switch (Labs) is on. */
const { activeAnchor } = useSettingsNav();
const mods = useModsStore();
// The switch decides whether Mods has a tab; read it once here so the tab is right on first paint.
onMounted(() => { void mods.loadSwitch(); });

type Tab = "skills" | "tools" | "mods";
const showMods = computed(() => mods.isSwitchedOn);

function tabFor(anchor: string | null): Tab {
  if (anchor === "tools") return "tools";
  if (anchor === "mods" && showMods.value) return "mods";
  return "skills";
}

const tab = shallowRef<Tab>(tabFor(activeAnchor.value));
watch(activeAnchor, (anchor) => { tab.value = tabFor(anchor); });
// Landed on Mods before the switch was read: open it as soon as it is known to be on.
watch(showMods, (on) => {
  if (on && activeAnchor.value === "mods") tab.value = "mods";
  // Mods turned off while its tab is open: fall back to Skills, as the nav entry used to disappear.
  if (!on && tab.value === "mods") tab.value = "skills";
});
</script>

<template>
  <Tabs
    :model-value="tab"
    data-testid="skills-tools-tabs"
    @update:model-value="tab = $event as Tab"
  >
    <TabsList
      variant="underline"
      class="mb-4"
    >
      <TabsTrigger value="skills">
        Skills
      </TabsTrigger>
      <TabsTrigger value="tools">
        Tools
      </TabsTrigger>
      <TabsTrigger
        v-if="showMods"
        value="mods"
      >
        Mods
      </TabsTrigger>
    </TabsList>
    <TabsContent value="skills">
      <SkillsSection />
    </TabsContent>
    <TabsContent value="tools">
      <ToolsSection />
    </TabsContent>
    <TabsContent
      v-if="showMods"
      value="mods"
    >
      <ModsSection />
    </TabsContent>
  </Tabs>
</template>
