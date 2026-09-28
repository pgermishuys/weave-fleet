<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { AlertCircle, LoaderCircle } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import ProfileConfigEditor from "@/components/settings/ProfileConfigEditor.vue";
import SkillDiff from "@/components/skills/SkillDiff.vue";
import { getSkill, saveSkillVersion, type BuiltInSkillDetail } from "@/lib/skill-versions";
import { useBuiltInSkillsStore } from "@/stores/built-in-skills";

/**
 * Edit a built-in skill by hand. Saving makes the text the user's next version, which the sessions they start
 * afterwards get in place of Fleet's. It starts from the version sessions get now, or from `initialContent`.
 */
const props = defineProps<{
  name: string;
  /** Text to start from instead of the current version, such as a change Improve proposed. */
  initialContent?: string;
  initialNote?: string;
  /** The session the change came from, which the history names. */
  sessionId?: string | null;
}>();

const emit = defineEmits<{
  saved: [detail: BuiltInSkillDetail];
}>();

const open = defineModel<boolean>("open", { required: true });

const store = useBuiltInSkillsStore();
const detail = shallowRef<BuiltInSkillDetail | null>(null);
const draft = shallowRef("");
const note = shallowRef("");
const showChanges = shallowRef(false);
const isLoading = shallowRef(false);
const isSaving = shallowRef(false);
const error = shallowRef<string | null>(null);

const current = computed(() => detail.value?.yourContent ?? detail.value?.fleetContent ?? "");
const nextVersion = computed(() => (detail.value?.versions[0]?.number ?? 0) + 1);
const changed = computed(() => detail.value !== null && draft.value !== current.value);

watch(open, async (isOpen) => {
  if (!isOpen) return;
  detail.value = null;
  error.value = null;
  showChanges.value = false;
  note.value = props.initialNote ?? "";
  isLoading.value = true;
  try {
    detail.value = await getSkill(props.name);
    draft.value = props.initialContent ?? current.value;
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : `Couldn't load ${props.name}.`;
  } finally {
    isLoading.value = false;
  }
}, { immediate: true });

function startFromFleets(): void {
  if (detail.value) draft.value = detail.value.fleetContent;
}

async function save(): Promise<void> {
  if (!changed.value || isSaving.value) return;
  isSaving.value = true;
  error.value = null;
  try {
    const saved = await saveSkillVersion(props.name, draft.value, note.value.trim() || null, props.sessionId ?? null);
    store.update(saved);
    emit("saved", saved);
    open.value = false;
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : `Couldn't save your version of ${props.name}.`;
  } finally {
    isSaving.value = false;
  }
}
</script>

<template>
  <Dialog v-model:open="open">
    <DialogContent
      class="max-h-[88dvh] overflow-y-auto sm:max-w-3xl"
      data-testid="skill-editor-dialog"
    >
      <DialogHeader>
        <DialogTitle>
          Edit <span class="font-mono">{{ name }}</span>
        </DialogTitle>
        <DialogDescription>
          Saving makes this your version {{ nextVersion }}. Sessions you start afterwards use it in place of Fleet's,
          and Fleet's updates never overwrite it.
        </DialogDescription>
      </DialogHeader>

      <div
        v-if="isLoading"
        class="flex items-center gap-2 text-sm text-muted"
      >
        <LoaderCircle
          class="h-4 w-4 animate-spin"
          aria-hidden="true"
        />
        Loading the skill…
      </div>

      <template v-else-if="detail">
        <ProfileConfigEditor
          v-model="draft"
          :label="`${name} SKILL.md`"
          filename="SKILL.md"
          @save="save"
        />

        <div class="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm">
          <button
            type="button"
            class="text-accent hover:underline disabled:opacity-50"
            :disabled="!changed"
            data-testid="skill-editor-show-changes"
            @click="showChanges = !showChanges"
          >
            {{ showChanges ? "Hide changes" : "Show changes" }}
          </button>
          <button
            v-if="detail.yourContent !== null && draft !== detail.fleetContent"
            type="button"
            class="text-accent hover:underline"
            @click="startFromFleets"
          >
            Start from Fleet's version
          </button>
        </div>

        <SkillDiff
          v-if="showChanges && changed"
          :before="current"
          :after="draft"
          :label="`${name}/SKILL.md`"
        />

        <div class="space-y-2">
          <label
            for="skill-editor-note"
            class="text-sm font-medium"
          >What did you change?</label>
          <Input
            id="skill-editor-note"
            v-model="note"
            placeholder="e.g. Don't report naming or style as findings"
            maxlength="2000"
          />
          <p class="text-xs text-muted">
            Optional. The skill's history shows it next to this version.
          </p>
        </div>
      </template>

      <div
        v-if="error"
        class="flex items-start gap-3 border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive"
        role="alert"
        data-testid="skill-editor-error"
      >
        <AlertCircle class="mt-0.5 h-4 w-4 shrink-0" />
        <p>{{ error }}</p>
      </div>

      <DialogFooter>
        <Button
          type="button"
          variant="outline"
          :disabled="isSaving"
          @click="open = false"
        >
          Cancel
        </Button>
        <Button
          type="button"
          :disabled="!changed || isSaving"
          data-testid="skill-editor-save"
          @click="save"
        >
          <LoaderCircle
            v-if="isSaving"
            class="h-4 w-4 animate-spin"
          />
          Save as version {{ nextVersion }}
        </Button>
      </DialogFooter>
    </DialogContent>
  </Dialog>
</template>
