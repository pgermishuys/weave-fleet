<script setup lang="ts">
import { computed } from "vue";
import { useNavigate } from "@tanstack/vue-router";
import { Check, ChevronDown, Layers } from "lucide-vue-next";
import { DropdownMenuItem, DropdownMenuSeparator } from "reka-ui";
import { NO_PROFILE, type HarnessProfile } from "@/api/client";
import { DropdownMenu, DropdownMenuContent, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { profileSummary } from "@/lib/harness-profile";

const props = defineProps<{
  profiles: readonly HarnessProfile[];
  disabled?: boolean;
}>();

const emit = defineEmits<{
  closeAutoFocus: [event: Event];
}>();

/** The profile's id, or {@link NO_PROFILE}. */
const profileId = defineModel<string>({ required: true });

const navigate = useNavigate();
const { setActiveSection } = useSettingsNav();

const selectedName = computed(() =>
  profileId.value === NO_PROFILE
    ? "No profile"
    : props.profiles.find((profile) => profile.id === profileId.value)?.name ?? "No profile",
);

function manageProfiles(): void {
  setActiveSection("harnesses");
  void navigate({ to: "/settings" });
}
</script>

<template>
  <DropdownMenu :modal="false">
    <DropdownMenuTrigger as-child>
      <button
        type="button"
        class="ns-chip"
        data-testid="new-session-profile"
        :aria-label="`Profile: ${selectedName}`"
        :disabled="disabled"
      >
        <Layers
          class="ns-chip__icon"
          aria-hidden="true"
        />
        <span class="ns-chip__label">{{ selectedName }}</span>
        <ChevronDown
          class="ns-chip__chevron"
          aria-hidden="true"
        />
      </button>
    </DropdownMenuTrigger>

    <DropdownMenuContent
      class="ns-pop ns-profile"
      side="top"
      align="start"
      :side-offset="6"
      :collision-padding="8"
      @close-auto-focus="emit('closeAutoFocus', $event)"
    >
      <div class="ns-pop__label">
        Profile
      </div>
      <DropdownMenuItem
        class="ns-option ns-profile__option"
        data-testid="new-session-profile-none"
        @select="profileId = NO_PROFILE"
      >
        <span class="ns-option__text">
          <span class="ns-option__title">No profile</span>
          <span class="ns-option__detail">Your opencode.json as it is</span>
        </span>
        <Check
          v-if="profileId === NO_PROFILE"
          class="ns-option__check"
          aria-hidden="true"
        />
      </DropdownMenuItem>
      <DropdownMenuItem
        v-for="profile in profiles"
        :key="profile.id"
        class="ns-option ns-profile__option"
        :data-testid="`new-session-profile-${profile.id}`"
        @select="profileId = profile.id"
      >
        <span class="ns-option__text">
          <span class="ns-option__title">
            {{ profile.name }}
            <span
              v-if="profile.isDefault"
              class="ns-option__tag"
            >default</span>
          </span>
          <span class="ns-option__detail">{{ profileSummary(profile.content) }}</span>
        </span>
        <Check
          v-if="profile.id === profileId"
          class="ns-option__check"
          aria-hidden="true"
        />
      </DropdownMenuItem>
      <DropdownMenuSeparator class="ns-pop__separator" />
      <p class="ns-pop__note">
        The session keeps this profile for as long as it lives.
      </p>
      <DropdownMenuItem
        class="ns-option ns-profile__manage"
        @select="manageProfiles"
      >
        <Layers
          class="ns-option__icon"
          aria-hidden="true"
        />
        <span class="ns-option__text">
          <span class="ns-option__title">Manage profiles</span>
        </span>
      </DropdownMenuItem>
    </DropdownMenuContent>
  </DropdownMenu>
</template>

<style>
.ns-profile {
  width: 320px;
}

.ns-profile__option {
  grid-template-columns: minmax(0, 1fr) 14px;
}

.ns-profile__manage {
  grid-template-columns: 16px minmax(0, 1fr);
}

.ns-pop__note {
  margin: 0;
  padding: 4px 8px 6px;
  color: var(--muted);
  font-size: 11.5px;
}
</style>
