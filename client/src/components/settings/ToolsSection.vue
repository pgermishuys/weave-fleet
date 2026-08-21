<script setup lang="ts">
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import InstalledToolsTab from "./tools/InstalledToolsTab.vue";
import CatalogTab from "./tools/CatalogTab.vue";
import CustomInstallTab from "./tools/CustomInstallTab.vue";
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
        <CatalogTab />
      </TabsContent>

      <TabsContent value="custom" class="mt-4">
        <CustomInstallTab />
      </TabsContent>
    </Tabs>
  </section>
</template>
