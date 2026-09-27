<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef, watch } from "vue";
import { useLocation } from "@tanstack/vue-router";
import { storeToRefs } from "pinia";
import { Check, Download, FileText, Image as ImageIcon, LoaderCircle, ShieldCheck, TriangleAlert, X } from "lucide-vue-next";
import { Dialog, DialogContent, DialogDescription, DialogTitle } from "@/components/ui/dialog";
import { describeSocketForReport } from "@/composables/use-signalr-socket";
import { getDesktopBridge } from "@/lib/desktop";
import { sessionRowStatus } from "@/lib/session-row-status";
import {
  REPORT_KINDS,
  clientPrivateValues,
  countLabels,
  describeApp,
  describeScreen,
  prepareReport,
  saveReportFile,
  sendReport,
  splitLabels,
  summarizeReplacements,
  type PreparedReport,
  type ReportFact,
  type ReportKind,
  type SendReportRequest,
} from "@/lib/problem-report";
import { useProblemReportStore } from "@/stores/problem-report";
import { useSessionsStore } from "@/stores/sessions";

type Stage = "describe" | "review" | "sent";
type ReviewFile = "report" | "log" | "screenshot";
type Item = "screenshot" | "environment" | "where" | "connection" | "log";

const ITEMS: readonly { id: Item; name: string; detail: string }[] = [
  { id: "screenshot", name: "Screenshot", detail: "Your screen when you clicked" },
  { id: "environment", name: "Fleet and system", detail: "Versions, app, operating system, harnesses" },
  { id: "where", name: "Where you were", detail: "Screen, session status as shown and as the server has it" },
  { id: "connection", name: "Connection", detail: "Live updates, right now" },
  { id: "log", name: "Fleet log", detail: "Last 15 minutes: this session's lines, plus warnings and errors" },
];

const store = useProblemReportStore();
const { open, opening, screenshot, capturing } = storeToRefs(store);
const sessionsStore = useSessionsStore();
const pathname = useLocation({ select: (location) => location.pathname });

const stage = shallowRef<Stage>("describe");
const kind = shallowRef<ReportKind>("bug");
const description = ref("");
const expected = ref("");
const contact = ref("");
const included = ref<Record<Item, boolean>>({ screenshot: true, environment: true, where: true, connection: true, log: true });
const prepared = shallowRef<PreparedReport | null>(null);
const title = ref("");
const reviewFile = shallowRef<ReviewFile>("report");
const showRemoved = shallowRef(false);
const busy = shallowRef<"prepare" | "send" | "save" | null>(null);
const error = shallowRef<string | null>(null);
const sentId = shallowRef<string | null>(null);
const copied = shallowRef(false);

const sessionId = computed(() => (opening.value?.sessionId !== undefined ? opening.value.sessionId : sessionsStore.activeSessionId) ?? null);
const session = computed(() => sessionsStore.sessions.find((item) => item.session.id === sessionId.value) ?? null);
const hasScreenshot = computed(() => screenshot.value !== null && included.value.screenshot);
const includedCount = computed(() => ITEMS.filter((item) => included.value[item.id] && (item.id !== "screenshot" || screenshot.value)).length);
const replacements = computed(() => new Map((prepared.value?.replacements ?? []).map((r) => [r.label, r.shown])));
const reviewText = computed(() => (reviewFile.value === "log" ? prepared.value?.log ?? "" : prepared.value?.body ?? ""));
const reviewParts = computed(() => splitLabels(reviewText.value));

// A new opening starts a new report; taking the screenshot again keeps what was typed.
watch(opening, (next, previous) => {
  if (!next || next === previous) return;
  stage.value = "describe";
  kind.value = "bug";
  description.value = next.description ?? "";
  expected.value = "";
  included.value = { screenshot: true, environment: true, where: true, connection: true, log: true };
  prepared.value = null;
  title.value = "";
  reviewFile.value = "report";
  showRemoved.value = false;
  busy.value = null;
  error.value = null;
  sentId.value = null;
  copied.value = false;
});

// The desktop app's Help → Report a Problem… opens the same report.
let stopDesktopMenu: (() => void) | undefined;
onMounted(() => {
  stopDesktopMenu = getDesktopBridge()?.onReportProblem?.(() => void store.show({ from: "desktop-menu" }));
});
onUnmounted(() => stopDesktopMenu?.());

function close(): void {
  if (busy.value === "send") return;
  store.hide();
}

function connectionFacts(): ReportFact[] {
  const socket = describeSocketForReport();
  const facts: ReportFact[] = [
    { name: "Live updates", value: `${socket.state}${socket.retrying > 0 ? ` · reconnect attempt ${socket.retrying}` : ""}` },
    { name: "Subscribed", value: `${socket.topics} topic${socket.topics === 1 ? "" : "s"}, ${socket.withSnapshot} with a snapshot` },
  ];
  if (!navigator.onLine) facts.push({ name: "Network", value: "the browser says it's offline" });
  return facts;
}

function whereFacts(): ReportFact[] {
  const facts: ReportFact[] = [
    { name: "Window", value: `${window.innerWidth}×${window.innerHeight} at ${window.devicePixelRatio || 1}×` },
  ];
  if (document.documentElement.dataset.theme) facts.push({ name: "Theme", value: document.documentElement.dataset.theme });
  return facts;
}

async function review(): Promise<void> {
  if (!description.value.trim() || busy.value) return;
  busy.value = "prepare";
  error.value = null;
  try {
    const item = session.value;
    prepared.value = await prepareReport({
      kind: kind.value,
      description: description.value,
      expected: expected.value.trim() || null,
      sessionId: sessionId.value,
      include: {
        environment: included.value.environment,
        where: included.value.where,
        connection: included.value.connection,
        log: included.value.log,
      },
      client: {
        app: describeApp(),
        screen: describeScreen(pathname.value),
        shownStatus: item ? statusShown() : null,
        where: whereFacts(),
        connection: connectionFacts(),
        privateValues: clientPrivateValues(),
      },
    });
    title.value = prepared.value.title;
    reviewFile.value = "report";
    stage.value = "review";
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : String(caught);
  } finally {
    busy.value = null;
  }
}

function statusShown(): string | null {
  const item = session.value;
  if (!item) return null;
  const status = sessionRowStatus(item, Date.now());
  return status.description || status.label || null;
}

function reviewedReport(): SendReportRequest | null {
  const report = prepared.value;
  if (!report) return null;
  return {
    kind: kind.value,
    title: title.value.trim() || report.title,
    body: report.body,
    log: report.log,
    screenshot: hasScreenshot.value ? screenshot.value!.dataUrl : null,
    contact: contact.value.trim() || null,
    labels: report.labels,
  };
}

async function send(): Promise<void> {
  const request = reviewedReport();
  if (!request || busy.value) return;
  busy.value = "send";
  error.value = null;
  try {
    sentId.value = await sendReport(request);
    stage.value = "sent";
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : String(caught);
  } finally {
    busy.value = null;
  }
}

async function saveFile(): Promise<void> {
  const request = reviewedReport();
  if (!request || busy.value) return;
  busy.value = "save";
  error.value = null;
  try {
    await saveReportFile(request);
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : String(caught);
  } finally {
    busy.value = null;
  }
}

async function copyId(): Promise<void> {
  if (!sentId.value) return;
  try {
    await navigator.clipboard.writeText(sentId.value);
    copied.value = true;
  } catch {
    copied.value = false;
  }
}

function removeScreenshot(): void {
  included.value = { ...included.value, screenshot: false };
  reviewFile.value = "report";
}

function formatTime(date: Date): string {
  return date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}
</script>

<template>
  <Dialog
    :open="open"
    @update:open="!$event && close()"
  >
    <DialogContent
      class="gap-0 border-0 bg-transparent p-0 sm:max-w-[820px]"
      :show-close-button="false"
      data-testid="problem-report"
      @escape-key-down="busy === 'send' && $event.preventDefault()"
    >
      <!-- The content is teleported, so this component's scoped styles start at this wrapper. -->
      <div class="report">
        <header class="report__head">
          <div class="report__heading">
            <DialogTitle class="report__title">
              Report a problem
            </DialogTitle>
            <DialogDescription class="report__sub">
              <ShieldCheck
                class="report__shield"
                aria-hidden="true"
              />
              <template v-if="stage === 'describe'">
                Nothing leaves your machine until you send it.
              </template>
              <template v-else-if="stage === 'review'">
                This is everything that will be sent.
              </template>
              <template v-else>
                Sent to the Fleet maintainers.
              </template>
            </DialogDescription>
          </div>
          <ol
            v-if="stage !== 'sent'"
            class="report__steps"
            aria-label="Steps"
          >
            <li :class="{ on: stage === 'describe' }">
              1 Describe
            </li>
            <li aria-hidden="true">
              ›
            </li>
            <li :class="{ on: stage === 'review' }">
              2 Review
            </li>
          </ol>
          <button
            type="button"
            class="report__close"
            aria-label="Close"
            data-testid="problem-report-close"
            @click="close"
          >
            <X :size="16" />
          </button>
        </header>

        <!-- 1. Describe -->
        <div
          v-if="stage === 'describe'"
          class="report__body"
        >
          <div class="report__left">
            <div
              class="report__kinds"
              role="radiogroup"
              aria-label="Kind"
            >
              <button
                v-for="option in REPORT_KINDS"
                :key="option.id"
                type="button"
                role="radio"
                class="report__kind"
                :class="{ on: kind === option.id }"
                :aria-checked="kind === option.id"
                @click="kind = option.id"
              >
                {{ option.label }}
              </button>
            </div>
            <label class="report__field">
              <span>What happened?</span>
              <textarea
                v-model="description"
                rows="4"
                maxlength="10000"
                placeholder="What you were doing, and what went wrong."
                data-testid="problem-report-description"
              />
            </label>
            <label class="report__field">
              <span>What did you expect? <em>(optional)</em></span>
              <input
                v-model="expected"
                type="text"
                maxlength="2000"
              >
            </label>
            <div class="report__field">
              <span>Screenshot</span>
              <div class="report__shot">
                <img
                  v-if="screenshot"
                  :src="screenshot.dataUrl"
                  alt="Screenshot of Fleet when you opened the report"
                  class="report__thumb"
                >
                <div
                  v-else
                  class="report__thumb report__thumb--none"
                >
                  <ImageIcon
                    :size="18"
                    aria-hidden="true"
                  />
                </div>
                <div class="report__shot-meta">
                  <template v-if="screenshot">
                    <span>Taken {{ formatTime(screenshot.takenAt) }} · {{ screenshot.width }}×{{ screenshot.height }}</span>
                    <span>Shows your screen as it is. You can look at it and remove it on the next step.</span>
                  </template>
                  <span v-else>Couldn't take a screenshot. The report works without one.</span>
                  <button
                    type="button"
                    class="report__button report__button--ghost"
                    :disabled="capturing"
                    data-testid="problem-report-retake"
                    @click="store.retake()"
                  >
                    {{ capturing ? "Taking…" : "Retake" }}
                  </button>
                </div>
              </div>
            </div>
            <label class="report__field">
              <span>Want a reply? <em>(optional: GitHub name or email)</em></span>
              <input
                v-model="contact"
                type="text"
                maxlength="200"
                placeholder="@your-name"
                data-testid="problem-report-contact"
              >
            </label>
          </div>

          <aside class="report__right">
            <div class="report__attached-head">
              <h3>Attached</h3>
              <span>{{ includedCount }} of {{ ITEMS.length }}</span>
            </div>
            <label
              v-for="item in ITEMS"
              :key="item.id"
              class="report__item"
              :class="{ off: !included[item.id] || (item.id === 'screenshot' && !screenshot) }"
            >
              <input
                v-model="included[item.id]"
                type="checkbox"
                :disabled="item.id === 'screenshot' && !screenshot"
                :data-testid="`problem-report-include-${item.id}`"
              >
              <span>
                <span class="report__item-name">{{ item.name }}</span>
                <span class="report__item-detail">{{ item.detail }}</span>
              </span>
            </label>
            <p class="report__guard">
              <ShieldCheck
                :size="14"
                aria-hidden="true"
              />
              <span>Folder paths, session names, branch names, tokens and email addresses in the text and the log are replaced before you review it. The screenshot isn't changed.</span>
            </p>
          </aside>
        </div>

        <!-- 2. Review -->
        <div
          v-else-if="stage === 'review' && prepared"
          class="report__review"
        >
          <nav
            class="report__files"
            aria-label="Report files"
          >
            <button
              type="button"
              class="report__file"
              :class="{ on: reviewFile === 'report' }"
              data-testid="problem-report-file-report"
              @click="reviewFile = 'report'"
            >
              <FileText
                :size="14"
                aria-hidden="true"
              />
              report.md
              <span
                v-if="countLabels(prepared.body)"
                class="report__count"
              >{{ countLabels(prepared.body) }}</span>
            </button>
            <button
              v-if="prepared.log"
              type="button"
              class="report__file"
              :class="{ on: reviewFile === 'log' }"
              data-testid="problem-report-file-log"
              @click="reviewFile = 'log'"
            >
              <FileText
                :size="14"
                aria-hidden="true"
              />
              fleet.log
              <span
                v-if="countLabels(prepared.log)"
                class="report__count"
              >{{ countLabels(prepared.log) }}</span>
            </button>
            <button
              v-if="hasScreenshot"
              type="button"
              class="report__file"
              :class="{ on: reviewFile === 'screenshot' }"
              data-testid="problem-report-file-screenshot"
              @click="reviewFile = 'screenshot'"
            >
              <ImageIcon
                :size="14"
                aria-hidden="true"
              />
              screenshot
            </button>
          </nav>

          <div class="report__main">
            <label class="report__title-field">
              <span>Title</span>
              <input
                v-model="title"
                type="text"
                maxlength="200"
                data-testid="problem-report-title"
              >
            </label>
            <div class="report__bar">
              <span v-if="prepared.replacements.length">
                <b>{{ prepared.replacements.length }} private detail{{ prepared.replacements.length === 1 ? "" : "s" }} replaced</b>
                · {{ summarizeReplacements(prepared.replacements) }}
              </span>
              <span v-else>No private details found.</span>
              <button
                v-if="prepared.replacements.length && reviewFile !== 'screenshot'"
                type="button"
                role="switch"
                class="report__switch"
                :aria-checked="showRemoved"
                data-testid="problem-report-show-removed"
                @click="showRemoved = !showRemoved"
              >
                <span
                  class="report__switch-track"
                  aria-hidden="true"
                />
                Show what was removed
              </button>
            </div>
            <ul
              v-if="prepared.problems.length"
              class="report__problems"
            >
              <li
                v-for="problem in prepared.problems"
                :key="problem.item"
              >
                <TriangleAlert
                  :size="13"
                  aria-hidden="true"
                />
                Left out: {{ problem.item }}. {{ problem.reason }}
              </li>
            </ul>

            <div
              v-if="reviewFile === 'screenshot' && screenshot"
              class="report__shotview"
            >
              <img
                :src="screenshot.dataUrl"
                alt="The screenshot that will be sent"
              >
              <div class="report__shotnote">
                <span>Not changed: anything on screen, such as session and folder names, is visible to whoever reads the report.</span>
                <button
                  type="button"
                  class="report__button"
                  data-testid="problem-report-remove-screenshot"
                  @click="removeScreenshot"
                >
                  <X
                    :size="13"
                    aria-hidden="true"
                  />
                  Remove screenshot
                </button>
              </div>
            </div>
            <pre
              v-else
              class="report__pre"
              data-testid="problem-report-preview"
            ><template
            v-for="(part, index) in reviewParts"
            :key="index"
          ><span
            v-if="part.label"
            class="report__label"
          ><del
            v-if="showRemoved && replacements.get(part.text)"
            class="report__was"
            >{{ replacements.get(part.text) }}</del>{{ part.text }}</span><template v-else>{{ part.text }}</template></template></pre>
          </div>
        </div>

        <!-- Sent -->
        <div
          v-else-if="stage === 'sent'"
          class="report__sent"
          data-testid="problem-report-sent"
        >
          <span class="report__ok"><Check
            :size="18"
            aria-hidden="true"
          /></span>
          <h3>Report sent</h3>
          <p>
            Only the Fleet maintainers can see it. If you talk to us about it, mention
            <code>{{ sentId }}</code>.
          </p>
          <p
            v-if="contact.trim()"
            class="report__dim"
          >
            You left a contact, so a reply goes to {{ contact.trim() }}.
          </p>
        </div>

        <footer class="report__foot">
          <p
            v-if="error"
            class="report__error"
            role="alert"
          >
            {{ error }}
          </p>
          <template v-if="stage === 'describe'">
            <span class="report__note">
              {{ session ? `About: ${session.session.title || "this session"}` : "Not about a session" }}
            </span>
            <button
              type="button"
              class="report__button report__button--ghost"
              @click="close"
            >
              Cancel
            </button>
            <button
              type="button"
              class="report__button report__button--primary"
              :disabled="!description.trim() || busy !== null"
              data-testid="problem-report-review"
              @click="review"
            >
              <LoaderCircle
                v-if="busy === 'prepare'"
                class="report__spin"
                :size="14"
                aria-hidden="true"
              />
              Review what's sent
            </button>
          </template>
          <template v-else-if="stage === 'review'">
            <button
              type="button"
              class="report__button report__button--ghost"
              :disabled="busy !== null"
              @click="stage = 'describe'"
            >
              Back
            </button>
            <span class="report__note">
              {{ prepared?.canSend ? "Only the Fleet maintainers will see this." : "Sending is off on this Fleet. Save it as a file instead." }}
            </span>
            <button
              type="button"
              class="report__button"
              :disabled="busy !== null"
              data-testid="problem-report-save"
              @click="saveFile"
            >
              <LoaderCircle
                v-if="busy === 'save'"
                class="report__spin"
                :size="14"
                aria-hidden="true"
              />
              <Download
                v-else
                :size="14"
                aria-hidden="true"
              />
              Save as file
            </button>
            <button
              type="button"
              class="report__button report__button--primary"
              :disabled="!prepared?.canSend || busy !== null"
              data-testid="problem-report-send"
              @click="send"
            >
              <LoaderCircle
                v-if="busy === 'send'"
                class="report__spin"
                :size="14"
                aria-hidden="true"
              />
              Send report
            </button>
          </template>
          <template v-else>
            <span class="report__note" />
            <button
              type="button"
              class="report__button"
              data-testid="problem-report-copy-id"
              @click="copyId"
            >
              {{ copied ? "Copied" : "Copy report id" }}
            </button>
            <button
              type="button"
              class="report__button report__button--primary"
              @click="close"
            >
              Done
            </button>
          </template>
        </footer>
      </div>
    </DialogContent>
  </Dialog>
</template>

<style scoped>
.report {
  display: flex;
  flex-direction: column;
  gap: 0;
  max-height: min(720px, calc(var(--visual-vh, 100dvh) - 32px));
  overflow: hidden;
  background: var(--panel-bg);
  border: 1px solid var(--border);
  border-radius: var(--radius-panel);
  color: var(--text);
}

.report__head {
  display: flex;
  align-items: flex-start;
  gap: 12px;
  padding: 16px 20px 12px;
  border-bottom: 1px solid var(--border);
}

.report__heading {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.report__title {
  margin: 0;
  font-size: 15px;
  font-weight: 600;
}

.report__sub {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--muted);
  font-size: 12.5px;
}

.report__shield {
  flex: none;
  width: 13px;
  height: 13px;
  color: var(--running);
}

.report__steps {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 0;
  padding: 0;
  list-style: none;
  color: var(--muted);
  font-size: 11.5px;
}

.report__steps li {
  padding: 2px 8px;
  border-radius: 999px;
}

.report__steps li.on {
  background: var(--accent-dim);
  color: var(--text);
}

.report__close {
  display: grid;
  place-items: center;
  width: 26px;
  height: 26px;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  cursor: pointer;
}

.report__close:hover {
  background: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

.report__body {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 290px;
  min-height: 0;
  overflow: auto;
}

.report__left {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 16px 20px;
  min-width: 0;
}

.report__kinds {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.report__kind {
  padding: 4px 10px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: transparent;
  color: var(--muted);
  font-size: 12px;
  cursor: pointer;
}

.report__kind.on {
  border-color: transparent;
  background: var(--accent-dim);
  color: var(--text);
}

.report__field {
  display: flex;
  flex-direction: column;
  gap: 5px;
  font-size: 11.5px;
  font-weight: 500;
  color: var(--muted);
}

.report__field em {
  font-style: normal;
  opacity: 0.75;
}

.report__field textarea,
.report__field input,
.report__title-field input {
  width: 100%;
  padding: 8px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--text);
  font: inherit;
  font-size: 13px;
  font-weight: 400;
  line-height: 1.45;
  resize: vertical;
}

.report__field textarea:focus,
.report__field input:focus,
.report__title-field input:focus {
  outline: none;
  border-color: color-mix(in srgb, var(--accent) 60%, transparent);
}

.report__shot {
  display: flex;
  align-items: stretch;
  gap: 12px;
}

.report__thumb {
  flex: none;
  width: 200px;
  height: 120px;
  object-fit: cover;
  object-position: top left;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--main-bg);
}

.report__thumb--none {
  display: grid;
  place-items: center;
  color: var(--muted);
}

.report__shot-meta {
  display: flex;
  flex-direction: column;
  justify-content: center;
  align-items: flex-start;
  gap: 6px;
  font-weight: 400;
  font-size: 12px;
  color: var(--muted);
}

.report__right {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 14px 14px 14px 16px;
  border-left: 1px solid var(--border);
  background: color-mix(in srgb, var(--main-bg) 45%, var(--panel-bg));
  min-width: 0;
}

.report__attached-head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  margin-bottom: 4px;
}

.report__attached-head h3 {
  margin: 0;
  font-size: 12.5px;
  font-weight: 600;
}

.report__attached-head span {
  color: var(--muted);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
}

.report__item {
  display: grid;
  grid-template-columns: 18px 1fr;
  gap: 8px;
  padding: 6px;
  border-radius: 7px;
  cursor: pointer;
}

.report__item:hover {
  background: color-mix(in srgb, var(--text) 4%, transparent);
}

.report__item input {
  width: 14px;
  height: 14px;
  margin: 2px 0 0;
  accent-color: var(--accent);
}

.report__item-name {
  display: block;
  font-size: 12.5px;
}

.report__item-detail {
  display: block;
  color: var(--muted);
  font-size: 11px;
  line-height: 1.35;
}

.report__item.off .report__item-name {
  color: var(--muted);
  text-decoration: line-through;
}

.report__guard {
  display: flex;
  align-items: flex-start;
  gap: 7px;
  margin: auto 0 0;
  padding-top: 10px;
  border-top: 1px solid var(--border);
  color: var(--muted);
  font-size: 11.5px;
  line-height: 1.45;
}

.report__guard svg {
  flex: none;
  margin-top: 1px;
  color: var(--running);
}

.report__review {
  display: grid;
  grid-template-columns: 180px minmax(0, 1fr);
  min-height: 0;
  flex: 1;
  overflow: hidden;
}

.report__files {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 12px 8px;
  border-right: 1px solid var(--border);
  background: color-mix(in srgb, var(--main-bg) 45%, var(--panel-bg));
}

.report__file {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 6px 8px;
  border: 0;
  border-radius: 7px;
  background: transparent;
  color: var(--muted);
  font-size: 12.5px;
  text-align: left;
  cursor: pointer;
}

.report__file.on {
  background: var(--card-bg);
  color: var(--text);
}

.report__count {
  margin-left: auto;
  padding: 0 6px;
  border-radius: 999px;
  background: var(--accent-dim);
  color: var(--text);
  font-size: 10.5px;
  font-variant-numeric: tabular-nums;
}

.report__main {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
}

.report__title-field {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 10px 16px 0;
  color: var(--muted);
  font-size: 11.5px;
}

.report__bar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 12px;
  padding: 10px 16px;
  border-bottom: 1px solid var(--border);
  color: var(--muted);
  font-size: 12px;
}

.report__bar b {
  color: var(--text);
  font-weight: 600;
}

.report__switch {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  margin-left: auto;
  border: 0;
  background: transparent;
  color: var(--muted);
  font-size: 12px;
  cursor: pointer;
}

.report__switch-track {
  position: relative;
  width: 28px;
  height: 16px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--text) 14%, transparent);
  transition: background var(--transition);
}

.report__switch-track::after {
  content: "";
  position: absolute;
  top: 2px;
  left: 2px;
  width: 12px;
  height: 12px;
  border-radius: 50%;
  background: #fff;
  transition: transform var(--transition);
}

.report__switch[aria-checked="true"] .report__switch-track {
  background: var(--accent);
}

.report__switch[aria-checked="true"] .report__switch-track::after {
  transform: translateX(12px);
}

.report__problems {
  display: flex;
  flex-direction: column;
  gap: 2px;
  margin: 0;
  padding: 8px 16px;
  border-bottom: 1px solid var(--border);
  list-style: none;
  color: var(--status-waiting);
  font-size: 12px;
}

.report__problems li {
  display: flex;
  align-items: center;
  gap: 6px;
}

.report__pre {
  flex: 1;
  min-height: 220px;
  margin: 0;
  padding: 14px 16px;
  overflow: auto;
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  line-height: 1.65;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.report__label {
  padding: 0 3px;
  border-radius: 3px;
  background: var(--accent-dim);
  color: var(--text);
}

.report__was {
  margin-right: 4px;
  color: var(--error);
  text-decoration-color: color-mix(in srgb, var(--error) 70%, transparent);
}

.report__shotview {
  display: flex;
  flex: 1;
  flex-direction: column;
  align-items: center;
  gap: 12px;
  padding: 16px;
  overflow: auto;
}

.report__shotview img {
  max-width: 100%;
  max-height: 340px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
}

.report__shotnote {
  display: flex;
  align-items: center;
  gap: 12px;
  max-width: 520px;
  color: var(--muted);
  font-size: 12px;
}

.report__sent {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  padding: 32px 24px 20px;
  text-align: center;
}

.report__sent h3 {
  margin: 0;
  font-size: 16px;
}

.report__sent p {
  max-width: 40ch;
  margin: 0;
  color: var(--muted);
  font-size: 13px;
}

.report__sent code {
  padding: 1px 6px;
  border-radius: 5px;
  background: var(--card-bg);
  color: var(--text);
  font-family: var(--font-mono-stack);
}

.report__ok {
  display: grid;
  place-items: center;
  width: 34px;
  height: 34px;
  border-radius: 50%;
  background: color-mix(in srgb, var(--running) 15%, transparent);
  color: var(--running);
}

.report__dim {
  font-size: 12px;
}

.report__foot {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  padding: 12px 20px;
  border-top: 1px solid var(--border);
}

.report__note {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--muted);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.report__error {
  flex-basis: 100%;
  margin: 0;
  color: var(--error);
  font-size: 12.5px;
}

.report__button {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 6px 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--text);
  font-size: 12.5px;
  font-weight: 500;
  white-space: nowrap;
  cursor: pointer;
}

.report__button:hover:not(:disabled) {
  background: color-mix(in srgb, var(--text) 8%, var(--card-bg));
}

.report__button:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.report__button--ghost {
  background: transparent;
}

.report__button--primary {
  border-color: transparent;
  background: var(--accent);
  color: var(--primary-foreground);
}

.report__button--primary:hover:not(:disabled) {
  background: color-mix(in srgb, #000 12%, var(--accent));
}

.report__spin {
  animation: report-spin 0.9s linear infinite;
}

@keyframes report-spin {
  to {
    transform: rotate(360deg);
  }
}

@media (prefers-reduced-motion: reduce) {
  .report__spin {
    animation: none;
  }
}

@media (max-width: 720px) {
  .report__steps {
    display: none;
  }

  .report__body,
  .report__review {
    grid-template-columns: minmax(0, 1fr);
  }

  .report__right {
    border-left: 0;
    border-top: 1px solid var(--border);
  }

  .report__files {
    flex-direction: row;
    overflow-x: auto;
    border-right: 0;
    border-bottom: 1px solid var(--border);
  }

  .report__thumb {
    width: 140px;
    height: 84px;
  }
}
</style>
