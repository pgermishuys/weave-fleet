<script setup lang="ts">
import { shallowRef } from "vue";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { GLOBAL_TARGET, type InstallTarget } from "@/lib/install-target";
import InstalledToolsTab from "./tools/InstalledToolsTab.vue";
import CatalogTab from "./tools/CatalogTab.vue";
import CustomInstallTab from "./tools/CustomInstallTab.vue";

// Shared by the Catalog and Custom tabs, so switching tabs keeps the choice.
const installTarget = shallowRef<InstallTarget>(GLOBAL_TARGET);
</script>

<template>
  <section class="rounded-card border border-border bg-card-bg p-6 shadow-sm">
    <div class="flex flex-col gap-1">
      <h2 class="text-lg font-semibold text-text">
        Tools
      </h2>
      <p class="text-sm text-muted">
        Manage installed tools, browse the catalog, or install from a custom source.
      </p>
    </div>

    <Tabs default-value="installed" class="mt-5">
      <TabsList variant="underline" class="grid w-full grid-cols-3">
        <TabsTrigger value="installed">Installed</TabsTrigger>
        <TabsTrigger value="catalog">Catalog</TabsTrigger>
        <TabsTrigger value="custom">Custom</TabsTrigger>
      </TabsList>

      <TabsContent value="installed" class="mt-4">
        <InstalledToolsTab />
      </TabsContent>

      <TabsContent value="catalog" class="mt-4">
        <CatalogTab v-model:target="installTarget" />
      </TabsContent>

      <TabsContent value="custom" class="mt-4">
        <CustomInstallTab v-model:target="installTarget" />
      </TabsContent>
    </Tabs>
  </section>
</template>
