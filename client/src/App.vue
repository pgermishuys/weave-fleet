<script setup lang="ts">
import { RouterProvider } from "@tanstack/vue-router";
import { useUnsavedFilesGuard } from "@/composables/use-unsaved-files-guard";
import { usePluginPreferenceSync } from "@/plugins/use-plugin-switch";
import { router } from "./router";

useUnsavedFilesGuard();
usePluginPreferenceSync();
</script>

<template>
  <RouterProvider :router="router" />
</template>
