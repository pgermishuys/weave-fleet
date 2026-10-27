<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { AlertCircle, LoaderCircle } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { parseLabels, type JobMachine, type JobRepository } from "@/lib/jobs";
import { useJobsStore } from "@/stores/jobs";

const props = defineProps<{ machines: readonly JobMachine[] }>();
const open = defineModel<boolean>("open", { default: false });

const store = useJobsStore();
const selectClass = "w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none focus:border-accent";

const title = shallowRef("");
const ask = shallowRef("");
const repository = shallowRef("");
const labelText = shallowRef("");
const pinnedMachineId = shallowRef("");
const repositories = shallowRef<JobRepository[]>([]);
const isPosting = shallowRef(false);
const errorMessage = shallowRef<string | null>(null);

const canPost = computed(() => !isPosting.value && title.value.trim() !== "" && ask.value.trim() !== "" && repository.value !== "");

watch(open, async (isOpen) => {
  if (!isOpen) {
    title.value = ask.value = repository.value = labelText.value = pinnedMachineId.value = "";
    errorMessage.value = null;
    return;
  }

  try {
    repositories.value = await store.loadRepositories();
    repository.value ||= repositories.value[0]?.repository ?? "";
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : "Couldn't list the repositories.";
  }
});

async function submit(): Promise<void> {
  if (!canPost.value) return;
  isPosting.value = true;
  errorMessage.value = null;
  try {
    await store.post({
      title: title.value.trim(),
      ask: ask.value.trim(),
      repository: repository.value,
      labels: parseLabels(labelText.value),
      pinnedMachineId: pinnedMachineId.value || null,
    });
    open.value = false;
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : "Couldn't post the job.";
  } finally {
    isPosting.value = false;
  }
}
</script>

<template>
  <Dialog v-model:open="open">
    <DialogContent class="sm:max-w-lg">
      <DialogHeader>
        <DialogTitle>New job</DialogTitle>
        <DialogDescription>
          Fleet starts it on a machine with a free seat. The ask goes to the agent as you write it.
        </DialogDescription>
      </DialogHeader>

      <form
        class="space-y-4"
        data-testid="new-job-form"
        @submit.prevent="submit"
      >
        <div class="space-y-1.5">
          <label
            for="new-job-title"
            class="text-sm font-medium text-text"
          >Title</label>
          <Input
            id="new-job-title"
            v-model="title"
            placeholder="Add rate limits"
          />
        </div>
        <div class="space-y-1.5">
          <label
            for="new-job-ask"
            class="text-sm font-medium text-text"
          >Ask</label>
          <Textarea
            id="new-job-ask"
            v-model="ask"
            rows="4"
            placeholder="What the agent should do"
          />
        </div>
        <div class="space-y-1.5">
          <label
            for="new-job-repository"
            class="text-sm font-medium text-text"
          >Repository</label>
          <select
            id="new-job-repository"
            v-model="repository"
            :class="selectClass"
            data-testid="new-job-repository"
          >
            <option
              v-for="found in repositories"
              :key="found.repository"
              :value="found.repository"
            >
              {{ found.name }} · {{ found.machines.join(", ") }}
            </option>
          </select>
          <p
            v-if="repositories.length === 0"
            class="text-xs text-muted"
          >
            No machine that takes jobs has a repository yet. Give a machine seats in Settings → Machines.
          </p>
        </div>
        <div class="grid grid-cols-2 gap-3">
          <div class="space-y-1.5">
            <label
              for="new-job-labels"
              class="text-sm font-medium text-text"
            >Needs</label>
            <Input
              id="new-job-labels"
              v-model="labelText"
              placeholder="mac, gpu"
            />
          </div>
          <div class="space-y-1.5">
            <label
              for="new-job-machine"
              class="text-sm font-medium text-text"
            >Run on</label>
            <select
              id="new-job-machine"
              v-model="pinnedMachineId"
              :class="selectClass"
            >
              <option value="">
                Any machine that fits
              </option>
              <option
                v-for="machine in props.machines"
                :key="machine.id"
                :value="machine.id"
              >
                {{ machine.name }}
              </option>
            </select>
          </div>
        </div>

        <p
          v-if="errorMessage"
          class="flex items-center gap-2 text-sm text-red-400"
          role="alert"
        >
          <AlertCircle :size="16" />
          {{ errorMessage }}
        </p>

        <DialogFooter>
          <Button
            type="button"
            variant="outline"
            @click="open = false"
          >
            Cancel
          </Button>
          <Button
            type="submit"
            :disabled="!canPost"
            data-testid="new-job-post"
          >
            <LoaderCircle
              v-if="isPosting"
              :size="16"
              class="animate-spin"
            />
            Post job
          </Button>
        </DialogFooter>
      </form>
    </DialogContent>
  </Dialog>
</template>
