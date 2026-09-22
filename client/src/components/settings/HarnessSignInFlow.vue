<script setup lang="ts">
import { computed, onBeforeUnmount, shallowRef, watch } from "vue";
import { Check, Copy, ExternalLink, LoaderCircle } from "lucide-vue-next";
import type { HarnessSignInAttempt, HarnessSignInMethod, HarnessSignInProvider } from "@/api/client";
import {
  answersToSend,
  codeToEnter,
  initialAnswers,
  isRemoteBrowser,
  methodKey,
  methodsToOffer,
  missingAnswer,
  visibleFields,
  type SignInAnswers,
} from "@/lib/harness-sign-in";
import { useHarnessSignInStore } from "@/stores/harness-sign-in";

/**
 * Signs in to one provider: a key, a sign-in in the browser, or what the harness says about its command and
 * environment methods. A key lives in this component only until it's sent. A browser sign-in is followed until it
 * finishes, fails or runs out, and is cancelled when this closes.
 */

const props = defineProps<{
  harnessType: string;
  harnessName: string;
  provider: HarnessSignInProvider;
  /** The terminal sign-in, for methods Fleet doesn't run. */
  signInCommand: string | null;
}>();

const emit = defineEmits<{
  (e: "done", message: string): void;
  (e: "close"): void;
}>();

/** How often a browser sign-in's status is checked. */
const POLL_MS = 2000;

const store = useHarnessSignInStore();

const methods = computed(() => methodsToOffer(props.provider));
const selectedKey = shallowRef(methods.value[0] ? methodKey(methods.value[0]) : "");
const method = computed<HarnessSignInMethod | null>(() => methods.value.find((m) => methodKey(m) === selectedKey.value) ?? null);

const answers = shallowRef<SignInAnswers>({});
const key = shallowRef("");
const error = shallowRef<string | null>(null);
const busy = shallowRef(false);

const attempt = shallowRef<HarnessSignInAttempt | null>(null);
const code = shallowRef("");
const landedOn = shallowRef("");
const pasteOpen = shallowRef(false);
const codeCopied = shallowRef(false);
let poll: ReturnType<typeof setTimeout> | undefined;

const fields = computed(() => (method.value ? visibleFields(method.value.fields, answers.value) : []));
const remote = typeof window === "undefined" ? false : isRemoteBrowser(window.location.hostname);
const deviceCode = computed(() => (attempt.value ? codeToEnter(attempt.value.instructions) : null));
const expiresAt = computed(() =>
  attempt.value ? new Date(attempt.value.expiresAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" }) : "");

watch(method, (next) => {
  answers.value = next ? initialAnswers(next.fields) : {};
  error.value = null;
}, { immediate: true });

function setAnswer(fieldKey: string, value: SignInAnswers[string]): void {
  answers.value = { ...answers.value, [fieldKey]: value };
}

function toggleChoice(fieldKey: string, value: string, checked: boolean): void {
  const current = (answers.value[fieldKey] as string[] | undefined) ?? [];
  setAnswer(fieldKey, checked ? [...current, value] : current.filter((v) => v !== value));
}

function checkAnswers(): boolean {
  const missing = method.value ? missingAnswer(method.value.fields, answers.value) : null;
  error.value = missing ? `Fill in “${missing}”.` : null;
  return missing === null;
}

async function signInWithKey(): Promise<void> {
  if (!method.value || busy.value || !checkAnswers()) return;
  if (!key.value.trim()) {
    error.value = "Enter the key.";
    return;
  }

  busy.value = true;
  try {
    await store.signInWithKey(props.harnessType, props.provider.id, key.value, answersToSend(method.value.fields, answers.value));
    key.value = "";
    emit("done", `Signed in to ${props.provider.name}.`);
  } catch (e) {
    error.value = e instanceof Error ? e.message : "Couldn't sign in.";
  } finally {
    busy.value = false;
  }
}

async function startBrowserSignIn(): Promise<void> {
  if (!method.value?.id || busy.value || !checkAnswers()) return;

  busy.value = true;
  try {
    const started = await store.start(props.harnessType, props.provider.id, method.value.id, answersToSend(method.value.fields, answers.value));
    attempt.value = started;
    pasteOpen.value = remote;
    // Still within the click's grace for new tabs; the link below is there if the browser blocked it anyway. The
    // desktop app opens it in the system browser.
    window.open(started.url, "_blank", "noopener,noreferrer");
    schedulePoll();
  } catch (e) {
    error.value = e instanceof Error ? e.message : "Couldn't start the sign-in.";
  } finally {
    busy.value = false;
  }
}

function schedulePoll(): void {
  clearTimeout(poll);
  poll = setTimeout(() => void checkAttempt(), POLL_MS);
}

async function checkAttempt(): Promise<void> {
  const current = attempt.value;
  if (!current) return;

  try {
    const status = await store.status(props.harnessType, props.provider.id, current.id);
    if (attempt.value?.id !== current.id) return;
    if (status.status === "pending") {
      schedulePoll();
      return;
    }

    attempt.value = null;
    if (status.status === "complete") {
      await store.changed(props.harnessType);
      emit("done", `Signed in to ${props.provider.name}.`);
    } else if (status.status === "failed") {
      error.value = `The sign-in didn't work: ${status.message ?? "the provider refused it"}.`;
    } else {
      error.value = "The sign-in ran out of time. Start it again.";
    }
  } catch {
    // A missed check isn't the end of the sign-in; try again.
    if (attempt.value?.id === current.id) schedulePoll();
  }
}

async function submitCode(): Promise<void> {
  if (!attempt.value || busy.value) return;
  if (!code.value.trim()) {
    error.value = "Paste the code the provider showed you.";
    return;
  }

  busy.value = true;
  error.value = null;
  try {
    await store.submitCode(props.harnessType, props.provider.id, attempt.value.id, code.value);
    code.value = "";
    await checkAttempt();
  } catch (e) {
    error.value = e instanceof Error ? e.message : "Couldn't finish the sign-in.";
  } finally {
    busy.value = false;
  }
}

async function forwardAddress(): Promise<void> {
  if (!attempt.value || busy.value) return;

  busy.value = true;
  error.value = null;
  try {
    await store.forwardCallback(props.harnessType, props.provider.id, attempt.value.id, landedOn.value);
    landedOn.value = "";
    await checkAttempt();
  } catch (e) {
    error.value = e instanceof Error ? e.message : "Couldn't finish the sign-in.";
  } finally {
    busy.value = false;
  }
}

async function cancelAttempt(): Promise<void> {
  clearTimeout(poll);
  const current = attempt.value;
  attempt.value = null;
  if (current) await store.cancel(props.harnessType, props.provider.id, current.id).catch(() => undefined);
}

async function close(): Promise<void> {
  await cancelAttempt();
  emit("close");
}

async function copyCode(): Promise<void> {
  if (!deviceCode.value) return;
  try {
    await navigator.clipboard?.writeText(deviceCode.value);
    codeCopied.value = true;
    setTimeout(() => (codeCopied.value = false), 1500);
  } catch {
    // The code is on screen to type.
  }
}

onBeforeUnmount(() => {
  key.value = "";
  void cancelAttempt();
});
</script>

<template>
  <div
    class="sign-in-flow"
    :data-testid="`sign-in-flow-${provider.id}`"
  >
    <div
      v-if="methods.length > 1 && !attempt"
      class="sign-in-flow__methods"
      role="tablist"
      :aria-label="`Ways to sign in to ${provider.name}`"
    >
      <button
        v-for="m in methods"
        :key="methodKey(m)"
        type="button"
        role="tab"
        class="sign-in-flow__method"
        :aria-selected="selectedKey === methodKey(m)"
        :data-testid="`sign-in-method-${methodKey(m)}`"
        @click="selectedKey = methodKey(m)"
      >
        {{ m.label }}
      </button>
    </div>

    <!-- A browser sign-in under way -->
    <div
      v-if="attempt"
      class="sign-in-flow__attempt"
      data-testid="sign-in-attempt"
    >
      <p class="sign-in-flow__status">
        <LoaderCircle
          :size="14"
          class="animate-spin"
          aria-hidden="true"
        />
        Waiting for you to finish on {{ provider.name }}'s page. It runs out at {{ expiresAt }}.
      </p>
      <a
        :href="attempt.url"
        target="_blank"
        rel="noopener noreferrer"
        class="sign-in-flow__link"
        data-testid="sign-in-attempt-url"
      >
        Open {{ provider.name }}'s sign-in page
        <ExternalLink
          :size="12"
          aria-hidden="true"
        />
      </a>
      <p
        v-if="attempt.instructions"
        class="sign-in-flow__instructions"
        data-testid="sign-in-attempt-instructions"
      >
        {{ attempt.instructions }}
        <button
          v-if="deviceCode"
          type="button"
          class="sign-in-flow__btn"
          @click="void copyCode()"
        >
          <Check
            v-if="codeCopied"
            :size="12"
            aria-hidden="true"
          />
          <Copy
            v-else
            :size="12"
            aria-hidden="true"
          />
          {{ codeCopied ? "Copied" : "Copy code" }}
        </button>
      </p>

      <form
        v-if="attempt.needsCode"
        class="sign-in-flow__row"
        @submit.prevent="void submitCode()"
      >
        <input
          v-model="code"
          class="sign-in-flow__input"
          autocomplete="off"
          spellcheck="false"
          placeholder="Paste the code from the provider"
          aria-label="Code from the provider"
          data-testid="sign-in-code"
        >
        <button
          type="submit"
          class="sign-in-flow__btn sign-in-flow__btn--primary"
          :disabled="busy"
        >
          Finish
        </button>
      </form>

      <template v-if="attempt.callbackAddress">
        <div
          v-if="remote"
          class="sign-in-flow__remote"
          data-testid="sign-in-remote-note"
        >
          You're using Fleet from another device. When you approve, {{ provider.name }} sends your browser to
          <code>{{ attempt.callbackAddress }}</code>, which only opens on the computer Fleet runs on, so the page won't load
          here. Copy that page's address from the address bar and paste it below.
        </div>
        <button
          v-else-if="!pasteOpen"
          type="button"
          class="sign-in-flow__text-btn"
          data-testid="sign-in-paste-open"
          @click="pasteOpen = true"
        >
          Page didn't load after you approved?
        </button>
        <form
          v-if="pasteOpen"
          class="sign-in-flow__row"
          @submit.prevent="void forwardAddress()"
        >
          <input
            v-model="landedOn"
            class="sign-in-flow__input"
            autocomplete="off"
            spellcheck="false"
            :placeholder="`${attempt.callbackAddress}?code=…`"
            aria-label="Address of the page that didn't load"
            data-testid="sign-in-landed-on"
          >
          <button
            type="submit"
            class="sign-in-flow__btn sign-in-flow__btn--primary"
            :disabled="busy || !landedOn.trim()"
          >
            Finish sign-in
          </button>
        </form>
      </template>

      <div class="sign-in-flow__actions">
        <button
          type="button"
          class="sign-in-flow__btn"
          data-testid="sign-in-cancel"
          @click="void close()"
        >
          Cancel
        </button>
      </div>
    </div>

    <!-- Choosing and filling in a method -->
    <template v-else-if="method">
      <form
        v-if="method.type === 'key' || method.type === 'oauth'"
        class="sign-in-flow__form"
        @submit.prevent="void (method.type === 'key' ? signInWithKey() : startBrowserSignIn())"
      >
        <label
          v-for="field in fields"
          :key="field.key"
          class="sign-in-flow__field"
        >
          <span class="sign-in-flow__label">{{ field.title ?? field.key }}<span v-if="field.required"> *</span></span>
          <select
            v-if="field.type === 'string' && field.options?.length"
            class="sign-in-flow__input"
            :value="answers[field.key] as string"
            :data-testid="`sign-in-field-${field.key}`"
            @change="setAnswer(field.key, ($event.target as HTMLSelectElement).value)"
          >
            <option
              v-for="option in field.options"
              :key="option.value"
              :value="option.value"
            >
              {{ option.label }}{{ option.description ? ` (${option.description})` : "" }}
            </option>
          </select>
          <input
            v-else-if="field.type === 'boolean'"
            type="checkbox"
            :checked="answers[field.key] === true"
            :data-testid="`sign-in-field-${field.key}`"
            @change="setAnswer(field.key, ($event.target as HTMLInputElement).checked)"
          >
          <span
            v-else-if="field.type === 'multiselect'"
            class="sign-in-flow__choices"
          >
            <label
              v-for="option in field.options ?? []"
              :key="option.value"
            >
              <input
                type="checkbox"
                :checked="(answers[field.key] as string[] | undefined)?.includes(option.value)"
                @change="toggleChoice(field.key, option.value, ($event.target as HTMLInputElement).checked)"
              >
              {{ option.label }}
            </label>
          </span>
          <a
            v-else-if="field.type === 'external' && field.url"
            :href="field.url"
            target="_blank"
            rel="noopener noreferrer"
            class="sign-in-flow__link"
          >
            {{ field.url }}
          </a>
          <input
            v-else
            class="sign-in-flow__input"
            :type="field.type === 'number' || field.type === 'integer' ? 'number' : 'text'"
            :value="answers[field.key] as string"
            :placeholder="field.placeholder ?? ''"
            autocomplete="off"
            spellcheck="false"
            :data-testid="`sign-in-field-${field.key}`"
            @input="setAnswer(field.key, ($event.target as HTMLInputElement).value)"
          >
          <span
            v-if="field.description"
            class="sign-in-flow__hint"
          >{{ field.description }}</span>
        </label>

        <label
          v-if="method.type === 'key'"
          class="sign-in-flow__field"
        >
          <span class="sign-in-flow__label">{{ method.label }}</span>
          <input
            v-model="key"
            class="sign-in-flow__input"
            type="password"
            autocomplete="off"
            spellcheck="false"
            :placeholder="`Paste your ${provider.name} key`"
            data-testid="sign-in-key"
          >
        </label>

        <p
          v-if="method.type === 'oauth'"
          class="sign-in-flow__hint"
        >
          {{ provider.name }}'s sign-in page opens in a new tab.
        </p>

        <div class="sign-in-flow__actions">
          <button
            type="submit"
            class="sign-in-flow__btn sign-in-flow__btn--primary"
            :disabled="busy"
            data-testid="sign-in-submit"
          >
            <LoaderCircle
              v-if="busy"
              :size="12"
              class="animate-spin"
              aria-hidden="true"
            />
            {{ method.type === "key" ? "Sign in" : "Continue in the browser" }}
          </button>
          <button
            type="button"
            class="sign-in-flow__btn"
            @click="void close()"
          >
            Cancel
          </button>
        </div>
      </form>

      <div
        v-else-if="method.type === 'command'"
        class="sign-in-flow__advice"
        data-testid="sign-in-command"
      >
        <p>
          {{ harnessName }} signs in to {{ provider.name }} by running
          <code>{{ (method.command ?? []).join(" ") }}</code>. Fleet doesn't run commands for you: to use this way, sign in
          from a terminal on the computer Fleet runs on.
        </p>
        <code
          v-if="signInCommand"
          class="sign-in-flow__command"
        >{{ signInCommand }}</code>
      </div>

      <div
        v-else-if="method.type === 'env'"
        class="sign-in-flow__advice"
        data-testid="sign-in-env"
      >
        <p>
          {{ harnessName }} also reads {{ provider.name }}'s key from
          <template
            v-for="(name, index) in method.environmentVariables ?? []"
            :key="name"
          >
            <span v-if="index > 0">{{ index === (method.environmentVariables?.length ?? 0) - 1 ? " or " : ", " }}</span><code>{{ name }}</code>
          </template>.
          Set it where Fleet starts, then restart Fleet.
        </p>
      </div>
    </template>

    <p
      v-if="error"
      class="sign-in-flow__error"
      role="alert"
      data-testid="sign-in-error"
    >
      {{ error }}
    </p>
  </div>
</template>

<style scoped>
.sign-in-flow {
  display: grid;
  gap: 10px;
  border-top: 1px solid var(--border);
  padding-top: 10px;
  font-size: 12.5px;
}

.sign-in-flow__methods {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.sign-in-flow__method {
  border: 1px solid var(--border);
  border-radius: 999px;
  padding: 3px 10px;
  background: var(--card-bg);
  color: var(--muted);
  font-size: 12px;
  cursor: pointer;
}

.sign-in-flow__method[aria-selected="true"] {
  border-color: color-mix(in srgb, var(--accent) 45%, transparent);
  background: color-mix(in srgb, var(--accent) 10%, transparent);
  color: var(--accent);
}

.sign-in-flow__form,
.sign-in-flow__attempt {
  display: grid;
  gap: 10px;
}

.sign-in-flow__field {
  display: grid;
  gap: 4px;
}

.sign-in-flow__label {
  color: var(--text);
  font-weight: 500;
}

.sign-in-flow__choices {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  color: var(--text);
}

.sign-in-flow__input {
  width: 100%;
  min-width: 0;
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 6px 8px;
  background: var(--card-bg);
  color: var(--text);
  font-size: 12.5px;
}

.sign-in-flow__input:focus {
  border-color: color-mix(in srgb, var(--accent) 60%, transparent);
  outline: none;
}

.sign-in-flow__row {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.sign-in-flow__row .sign-in-flow__input {
  flex: 1 1 220px;
  width: auto;
}

.sign-in-flow__actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.sign-in-flow__btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  padding: 5px 10px;
  background: var(--card-bg);
  color: var(--text);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
}

.sign-in-flow__btn:hover {
  border-color: color-mix(in srgb, var(--accent) 50%, transparent);
}

.sign-in-flow__btn:disabled {
  cursor: not-allowed;
  opacity: 0.6;
}

.sign-in-flow__btn--primary {
  border-color: color-mix(in srgb, var(--accent) 50%, transparent);
  background: color-mix(in srgb, var(--accent) 14%, transparent);
  color: var(--accent);
}

.sign-in-flow__text-btn {
  justify-self: start;
  color: var(--accent);
  font-size: 12px;
  cursor: pointer;
}

.sign-in-flow__status {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--text);
}

.sign-in-flow__instructions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 6px 8px;
  background: var(--card-bg);
  color: var(--text);
  font-family: var(--font-mono-stack);
  font-size: 12px;
}

.sign-in-flow__remote,
.sign-in-flow__advice {
  border: 1px solid color-mix(in srgb, var(--accent) 25%, transparent);
  border-radius: 6px;
  padding: 8px 10px;
  background: color-mix(in srgb, var(--accent) 6%, transparent);
  color: var(--text);
  line-height: 1.5;
}

.sign-in-flow__advice {
  display: grid;
  gap: 8px;
}

.sign-in-flow code {
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  overflow-wrap: anywhere;
}

.sign-in-flow__command {
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 5px 8px;
  background: var(--card-bg);
}

.sign-in-flow__link {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  justify-self: start;
  color: var(--accent);
  font-weight: 500;
  overflow-wrap: anywhere;
}

.sign-in-flow__hint {
  color: var(--muted);
}

.sign-in-flow__error {
  color: var(--error, #f87171);
}
</style>
