<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { KeyRound, LoaderCircle, RefreshCw, Search } from "lucide-vue-next";
import type { HarnessSignInConnection, HarnessSignInProvider } from "@/api/client";
import HarnessSignInFlow from "@/components/settings/HarnessSignInFlow.vue";
import { signInSummary } from "@/lib/harness-sign-in";
import { useHarnessSignInStore } from "@/stores/harness-sign-in";

/**
 * A harness's providers under its card in Settings → Harnesses: which are signed in (and which sign-in each uses),
 * with Sign in and Sign out. Listing them asks the harness, which starts it if it isn't running, so a harness the
 * user hasn't turned on waits for a click.
 */

const props = defineProps<{
  harnessType: string;
  harnessName: string;
  /** The terminal sign-in, still the way for methods Fleet doesn't run. */
  signInCommand: string | null;
  /** List the providers straight away; otherwise on request. */
  autoLoad: boolean;
}>();

const store = useHarnessSignInStore();

const requested = shallowRef(false);
const loading = shallowRef(false);
const error = shallowRef<string | null>(null);
const notice = shallowRef<string | null>(null);
const query = shallowRef("");
const openProvider = shallowRef<string | null>(null);
const confirmingSignOut = shallowRef<string | null>(null);
const working = shallowRef<string | null>(null);

const signIns = computed(() => store.signInsFor(props.harnessType));
const providers = computed(() => signIns.value?.providers ?? []);
const signedIn = computed(() => providers.value.filter((provider) => provider.connections.length > 0));
const others = computed(() => {
  const words = query.value.trim().toLowerCase();
  return providers.value.filter((provider) => provider.connections.length === 0
    && (!words || provider.name.toLowerCase().includes(words) || provider.id.toLowerCase().includes(words)));
});

async function load(): Promise<void> {
  requested.value = true;
  loading.value = true;
  error.value = null;
  try {
    await store.load(props.harnessType);
  } catch (e) {
    error.value = e instanceof Error ? e.message : `Couldn't list ${props.harnessName}'s providers.`;
  } finally {
    loading.value = false;
  }
}

// Whether the harness is turned on can arrive after this mounts (preferences load on their own).
watch(() => props.autoLoad, (auto) => {
  if (auto && !loading.value) void load();
}, { immediate: true });

function toggle(provider: HarnessSignInProvider): void {
  openProvider.value = openProvider.value === provider.id ? null : provider.id;
  notice.value = null;
}

async function done(message: string): Promise<void> {
  openProvider.value = null;
  query.value = "";
  notice.value = `${message} New sessions can use its models.`;
  try {
    await store.changed(props.harnessType);
  } catch (e) {
    error.value = e instanceof Error ? e.message : `Couldn't list ${props.harnessName}'s providers again.`;
  }
}

async function use(provider: HarnessSignInProvider, connection: HarnessSignInConnection): Promise<void> {
  working.value = connection.id;
  error.value = null;
  try {
    await store.use(props.harnessType, connection.id);
    notice.value = `${provider.name} now uses ${connection.label}.`;
  } catch (e) {
    error.value = e instanceof Error ? e.message : "Couldn't switch sign-ins.";
  } finally {
    working.value = null;
  }
}

async function signOut(provider: HarnessSignInProvider, connection: HarnessSignInConnection): Promise<void> {
  if (confirmingSignOut.value !== connection.id) {
    confirmingSignOut.value = connection.id;
    return;
  }

  confirmingSignOut.value = null;
  working.value = connection.id;
  error.value = null;
  try {
    await store.signOut(props.harnessType, connection.id);
    notice.value = `Signed out of ${connection.label} on ${provider.name}.`;
  } catch (e) {
    error.value = e instanceof Error ? e.message : "Couldn't sign out.";
  } finally {
    working.value = null;
  }
}
</script>

<template>
  <div
    class="sign-in-panel"
    data-testid="harness-sign-in"
  >
    <div class="sign-in-panel__head">
      <KeyRound
        :size="14"
        aria-hidden="true"
      />
      <p class="sign-in-panel__title">
        Providers
      </p>
      <button
        v-if="signIns"
        type="button"
        class="sign-in-panel__icon-btn"
        :disabled="loading"
        aria-label="Check the providers again"
        @click="void load()"
      >
        <RefreshCw
          :size="12"
          :class="loading ? 'animate-spin' : ''"
          aria-hidden="true"
        />
      </button>
    </div>

    <div
      v-if="!requested && !signIns"
      class="sign-in-panel__intro"
    >
      <p>Sign in to the providers {{ harnessName }} uses, with a key or in the browser.</p>
      <button
        type="button"
        class="sign-in-panel__btn"
        data-testid="harness-sign-in-show"
        @click="void load()"
      >
        Show providers
      </button>
    </div>

    <p
      v-else-if="loading && !signIns"
      class="sign-in-panel__muted"
    >
      <LoaderCircle
        :size="12"
        class="animate-spin"
        aria-hidden="true"
      />
      Asking {{ harnessName }} which providers it can sign in to…
    </p>

    <template v-if="signIns">
      <p
        v-if="signIns.note"
        class="sign-in-panel__muted"
        data-testid="harness-sign-in-note"
      >
        {{ signIns.note }}
      </p>

      <p
        v-if="notice"
        class="sign-in-panel__notice"
        role="status"
        data-testid="harness-sign-in-notice"
      >
        {{ notice }}
      </p>

      <section
        class="sign-in-panel__group"
        aria-label="Signed in"
      >
        <p class="sign-in-panel__group-title">
          Signed in
        </p>
        <p
          v-if="signedIn.length === 0"
          class="sign-in-panel__muted"
          data-testid="harness-sign-in-none"
        >
          No providers yet. Find one below to sign in.
        </p>
        <article
          v-for="provider in signedIn"
          :key="provider.id"
          class="sign-in-panel__provider"
          :data-testid="`harness-sign-in-provider-${provider.id}`"
        >
          <div class="sign-in-panel__row">
            <span class="sign-in-panel__name">{{ provider.name }}</span>
            <span class="sign-in-panel__chip">{{ signInSummary(provider) }}</span>
            <button
              type="button"
              class="sign-in-panel__btn sign-in-panel__btn--end"
              :aria-expanded="openProvider === provider.id"
              @click="toggle(provider)"
            >
              {{ openProvider === provider.id ? "Close" : "Add a sign-in" }}
            </button>
          </div>
          <ul class="sign-in-panel__connections">
            <li
              v-for="connection in provider.connections"
              :key="`${connection.kind}:${connection.id}`"
              class="sign-in-panel__connection"
              :data-testid="`harness-sign-in-connection-${connection.id}`"
            >
              <span>{{ connection.label }}</span>
              <span
                v-if="connection.kind === 'env'"
                class="sign-in-panel__muted"
              >from Fleet's environment</span>
              <span
                v-if="connection.active"
                class="sign-in-panel__chip sign-in-panel__chip--active"
              >In use</span>
              <span class="sign-in-panel__actions">
                <button
                  v-if="connection.kind === 'credential' && !connection.active"
                  type="button"
                  class="sign-in-panel__btn"
                  :disabled="working !== null"
                  @click="void use(provider, connection)"
                >
                  Use this one
                </button>
                <button
                  v-if="connection.kind === 'credential'"
                  type="button"
                  class="sign-in-panel__btn"
                  :class="confirmingSignOut === connection.id ? 'sign-in-panel__btn--danger' : ''"
                  :disabled="working !== null"
                  :data-testid="`harness-sign-out-${connection.id}`"
                  @click="void signOut(provider, connection)"
                >
                  <LoaderCircle
                    v-if="working === connection.id"
                    :size="12"
                    class="animate-spin"
                    aria-hidden="true"
                  />
                  {{ confirmingSignOut === connection.id ? "Sign out?" : "Sign out" }}
                </button>
              </span>
            </li>
          </ul>
          <HarnessSignInFlow
            v-if="openProvider === provider.id"
            :harness-type="harnessType"
            :harness-name="harnessName"
            :provider="provider"
            :sign-in-command="signInCommand"
            @done="void done($event)"
            @close="openProvider = null"
          />
        </article>
      </section>

      <section
        class="sign-in-panel__group"
        aria-label="Sign in to a provider"
      >
        <p class="sign-in-panel__group-title">
          Sign in to a provider
        </p>
        <label class="sign-in-panel__search">
          <Search
            :size="12"
            aria-hidden="true"
          />
          <input
            v-model="query"
            type="search"
            placeholder="Find a provider"
            aria-label="Find a provider"
            data-testid="harness-sign-in-search"
          >
        </label>
        <div class="sign-in-panel__list">
          <article
            v-for="provider in others"
            :key="provider.id"
            class="sign-in-panel__provider"
            :data-testid="`harness-sign-in-provider-${provider.id}`"
          >
            <div class="sign-in-panel__row">
              <span class="sign-in-panel__name">{{ provider.name }}</span>
              <span class="sign-in-panel__id">{{ provider.id }}</span>
              <button
                type="button"
                class="sign-in-panel__btn sign-in-panel__btn--end"
                :aria-expanded="openProvider === provider.id"
                :data-testid="`harness-sign-in-open-${provider.id}`"
                @click="toggle(provider)"
              >
                {{ openProvider === provider.id ? "Close" : "Sign in" }}
              </button>
            </div>
            <HarnessSignInFlow
              v-if="openProvider === provider.id"
              :harness-type="harnessType"
              :harness-name="harnessName"
              :provider="provider"
              :sign-in-command="signInCommand"
              @done="void done($event)"
              @close="openProvider = null"
            />
          </article>
          <p
            v-if="others.length === 0"
            class="sign-in-panel__muted"
          >
            No provider matches “{{ query }}”.
          </p>
        </div>
      </section>
    </template>

    <p
      v-if="error"
      class="sign-in-panel__error"
      role="alert"
      data-testid="harness-sign-in-error"
    >
      {{ error }}
      <button
        v-if="!signIns"
        type="button"
        class="sign-in-panel__btn"
        @click="void load()"
      >
        Try again
      </button>
    </p>
  </div>
</template>

<style scoped>
.sign-in-panel {
  display: grid;
  gap: 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  padding: 14px 16px;
  background: var(--main-bg);
  font-size: 12.5px;
}

.sign-in-panel__head {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--text);
}

.sign-in-panel__title {
  font-size: 13px;
  font-weight: 600;
}

.sign-in-panel__intro {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  color: var(--muted);
}

.sign-in-panel__group {
  display: grid;
  gap: 8px;
}

.sign-in-panel__group-title {
  color: var(--muted);
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.sign-in-panel__provider {
  display: grid;
  gap: 8px;
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 8px 10px;
  background: var(--card-bg);
}

.sign-in-panel__row,
.sign-in-panel__connection {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.sign-in-panel__name {
  color: var(--text);
  font-weight: 500;
}

.sign-in-panel__id {
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 11px;
  overflow-wrap: anywhere;
}

.sign-in-panel__chip {
  border: 1px solid var(--border);
  border-radius: 999px;
  padding: 0 7px;
  color: var(--muted);
  font-size: 11px;
}

.sign-in-panel__chip--active {
  border-color: color-mix(in srgb, var(--accent) 35%, transparent);
  background: color-mix(in srgb, var(--accent) 10%, transparent);
  color: var(--accent);
}

.sign-in-panel__connections {
  display: grid;
  gap: 6px;
  margin: 0;
  padding: 0;
  list-style: none;
  color: var(--text);
}

.sign-in-panel__actions,
.sign-in-panel__btn--end {
  margin-left: auto;
}

.sign-in-panel__actions {
  display: inline-flex;
  flex-wrap: wrap;
  gap: 6px;
}

.sign-in-panel__btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  padding: 3px 9px;
  background: var(--main-bg);
  color: var(--text);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
}

.sign-in-panel__btn:hover {
  border-color: color-mix(in srgb, var(--accent) 50%, transparent);
}

.sign-in-panel__btn:disabled {
  cursor: not-allowed;
  opacity: 0.6;
}

.sign-in-panel__btn--danger {
  border-color: color-mix(in srgb, var(--error, #f87171) 55%, transparent);
  color: var(--error, #f87171);
}

.sign-in-panel__icon-btn {
  margin-left: auto;
  color: var(--muted);
  cursor: pointer;
}

.sign-in-panel__search {
  display: flex;
  align-items: center;
  gap: 6px;
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 5px 8px;
  background: var(--card-bg);
  color: var(--muted);
}

.sign-in-panel__search input {
  flex: 1;
  min-width: 0;
  background: transparent;
  color: var(--text);
  outline: none;
}

.sign-in-panel__list {
  display: grid;
  gap: 6px;
  max-height: 22rem;
  overflow-y: auto;
  padding-right: 2px;
}

.sign-in-panel__muted {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--muted);
}

.sign-in-panel__notice {
  color: var(--accent);
}

.sign-in-panel__error {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  color: var(--error, #f87171);
}
</style>
