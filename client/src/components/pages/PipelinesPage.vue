<script setup lang="ts">
import { Plus } from "lucide-vue-next";
import AutomationCard, { type Automation } from "@/components/automations/AutomationCard.vue";
import { Button } from "@/components/ui/button";

const mockAutomations: Automation[] = [
  {
    id: "1",
    title: "Daily Standup Report",
    enabled: true,
    prompt: "Generate a standup report summarizing yesterday's merged PRs, open issues, and CI failures across all tracked repositories.",
    trigger: {
      type: "schedule",
      value: "0 9 * * 1-5",
    },
    policy: {
      maxConcurrent: 1,
      maxPerHour: 2,
      timeoutMinutes: 10,
    },
  },
  {
    id: "2",
    title: "PR Merge Notification",
    enabled: true,
    prompt: "When a pull request is merged, post a summary to the team Slack channel with the PR title, author, and linked issues.",
    trigger: {
      type: "event",
      value: "pull_request.merged",
    },
    policy: {
      maxConcurrent: 5,
      maxPerHour: 50,
      timeoutMinutes: 5,
    },
  },
  {
    id: "3",
    title: "Weekly Dependency Audit",
    enabled: false,
    prompt: "Scan all repositories for outdated dependencies and create a summary report with security advisories and recommended updates.",
    trigger: {
      type: "schedule",
      value: "0 10 * * 1",
    },
    policy: {
      maxConcurrent: 2,
      maxPerHour: 3,
      timeoutMinutes: 30,
    },
  },
];

function handlePlay(id: string) {
  console.log("Play automation:", id);
}

function handlePause(id: string) {
  console.log("Pause automation:", id);
}

function handleEdit(id: string) {
  console.log("Edit automation:", id);
}

function handleDelete(id: string) {
  console.log("Delete automation:", id);
}

function handleNewAutomation() {
  console.log("New automation");
}
</script>

<template>
  <section class="flex h-full flex-col gap-6 overflow-auto p-6">
    <header class="flex items-center justify-between">
      <h1 class="text-2xl font-semibold tracking-tight text-foreground">
        Automations
      </h1>
      <Button
        variant="default"
        size="default"
        @click="handleNewAutomation"
      >
        <Plus :size="16" />
        New Automation
      </Button>
    </header>

    <div class="space-y-0">
      <AutomationCard
        v-for="automation in mockAutomations"
        :key="automation.id"
        :automation="automation"
        @play="handlePlay"
        @pause="handlePause"
        @edit="handleEdit"
        @delete="handleDelete"
      />
    </div>
  </section>
</template>
