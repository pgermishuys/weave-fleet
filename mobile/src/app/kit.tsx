// Every kit component in every state, for checking the look on a device. Opened by long-pressing the Fleet mark.
import { useState, type ReactNode } from "react";
import { Pressable, ScrollView, View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import {
  Button, Card, Chip, Composer, EmptyState, FleetMark, glyphStatusOf, Markdown, PageCard, PageChoiceBar, PagePickCard,
  PageReplyBar, PageRow, PermissionCard, Pill, QuestionCard, SectionHeader, Segments, SessionRow, Sheet, StatusGlyph, Text,
  ToolBox, ToolRow, UserBubble, type GlyphStatus,
} from "~/kit";
import { ThemeProvider, useTheme, type Scheme } from "~/theme";

const STATUSES: GlyphStatus[] = ["working", "retry", "needs-you", "idle", "error", "done", "running"];
const MARKDOWN = `## Why the burst got through

The reset ran on the **wall clock**, so a burst across the window's edge saw a fresh budget. I moved it to \`SlidingWindow.start\`. See [the limiter notes](https://example.com/limiter).

- Reset at the window's start
- Keep \`used\` per window
- Add a test for the edge

\`\`\`ts
take(now: number) {
  while (now >= this.end) this.start += this.size;
}
\`\`\``;

function Section({ label, children }: { label: string; children: ReactNode }) {
  return (
    <View style={{ gap: 10 }}>
      <Text size={11.5} weight="semibold" tone="muted" style={{ letterSpacing: 0.6, textTransform: "uppercase" }}>{label}</Text>
      {children}
    </View>
  );
}

const Row = ({ children }: { children: ReactNode }) => (
  <View style={{ flexDirection: "row", flexWrap: "wrap", gap: 8, alignItems: "center" }}>{children}</View>
);

function Toggle({ value, onChange }: { value: Scheme | null; onChange: (v: Scheme | null) => void }) {
  const { color } = useTheme();
  const options: [string, Scheme | null][] = [["Light", "light"], ["Dark", "dark"], ["System", null]];
  return (
    <View style={{ flexDirection: "row", gap: 2, padding: 2, borderRadius: 9, backgroundColor: color.tint5, alignSelf: "flex-start" }}>
      {options.map(([label, v]) => (
        <Pressable
          key={label} onPress={() => onChange(v)} accessibilityRole="button" accessibilityState={{ selected: value === v }}
          style={{ minWidth: 72, height: 40, borderRadius: 7, alignItems: "center", justifyContent: "center", backgroundColor: value === v ? color.panel : "transparent", borderWidth: 1, borderColor: value === v ? color.border : "transparent" }}
        >
          <Text size={14} weight="medium" tone={value === v ? "default" : "muted"}>{label}</Text>
        </Pressable>
      ))}
    </View>
  );
}

function Gallery({ override, setOverride }: { override: Scheme | null; setOverride: (v: Scheme | null) => void }) {
  const { color } = useTheme();
  const [text, setText] = useState("Add a retry budget to the webhooks");
  const [sheet, setSheet] = useState(false);
  const [segment, setSegment] = useState<"changes" | "pages">("pages");
  const noop = () => {};
  const variants = ["primary", "secondary", "ghost", "danger"] as const;
  const insets = useSafeAreaInsets();
  return (
    // The status bar keeps its own strip of chrome; the gallery scrolls below it.
    <View style={{ flex: 1, backgroundColor: color.chrome, paddingTop: insets.top }}>
      <ScrollView style={{ flex: 1 }} contentContainerStyle={{ padding: 16, paddingTop: 16, paddingBottom: 64 + insets.bottom, gap: 28 }}>
        <View style={{ gap: 14 }}>
          <View style={{ flexDirection: "row", alignItems: "center", gap: 10 }}>
            <FleetMark height={26} />
            <Text size={26} weight="semibold" accessibilityRole="header">Kit</Text>
          </View>
          <Toggle value={override} onChange={setOverride} />
        </View>

        <Section label="Glyphs">
          {[10, 18].map((size) => (
            <View key={size} style={{ flexDirection: "row", gap: 4, justifyContent: "space-between" }}>
              {STATUSES.map((s) => (
                <View key={s} style={{ alignItems: "center", gap: 6, minWidth: 48 }}>
                  <StatusGlyph status={s} size={size} /><Text size={10} tone="muted">{s}</Text>
                </View>
              ))}
            </View>
          ))}
          <Text size={12} tone="muted">{`glyphStatusOf: active+retry = ${glyphStatusOf("retry", "active")}, waiting_input = ${glyphStatusOf(null, "waiting_input")}`}</Text>
        </Section>

        <Section label="Buttons">
          {(["md", "sm"] as const).map((size) => variants.map((v) => (
            <Row key={size + v}>
              <Button label={v} variant={v} size={size} onPress={noop} icon={v === "primary" ? "plus" : undefined} />
              <Button label="Disabled" variant={v} size={size} onPress={noop} disabled />
              <Button label="Loading" variant={v} size={size} onPress={noop} loading />
            </Row>
          )))}
        </Section>

        <Section label="Chips and pills">
          <Row>
            <Chip label="#612" icon="pr" tone="waiting" />
            <Chip label="1 failing" tone="error" />
            <Chip label="fix/burst" icon="branch" />
            <Chip label="hangar" tone="muted" />
            <Chip label="Loom" tone="accent" />
            <Chip label="checks passed" tone="running" />
            <Chip label="sonnet-5" mono onPress={noop} />
          </Row>
          <Row>
            {(["running", "error", "waiting", "accent", "muted"] as const).map((t) => <Pill key={t} label={t} tone={t} />)}
          </Row>
        </Section>

        <Section label="Cards">
          <Card><Text weight="semibold">Default card</Text><Text size={14} tone="muted">Raised on the panel, hairline border.</Text></Card>
          <Card tone="waiting"><Text weight="semibold">Waiting card</Text><Text size={14} tone="muted">Amber edge and a warm tint.</Text></Card>
        </Section>

        <Section label="Permission card">
          <PermissionCard title="Run a command" session="Add a retry budget to webhooks" machine="hangar" age="2m"
            command="bun run db:migrate --to 0034_retry_budget" alwaysLabel="Don't ask again for bun run db:migrate" onAllow={noop} onDeny={noop} onAlways={noop} />
          <PermissionCard title="Edit a file outside the folder" session="Speed up the ledger export" onAllow={noop} onDeny={noop} />
          <PermissionCard title="Run a command" machine="falcon" age="now" command="git push origin perf/ledger-stream" onAllow={noop} onDeny={noop} onAlways={noop} busy />
        </Section>

        <Section label="Question card">
          <QuestionCard question="Which window should the limiter use?" onAnswer={noop}
            options={[{ label: "Sliding window", description: "Smoother, a little more memory" }, { label: "Fixed window" }, { label: "Token bucket", description: "Allows short bursts" }]} />
        </Section>

        <Section label="Tool box">
          <ToolBox summary="Read 4 files · searched 2">
            <ToolRow tool="read" label="Read" detail="src/limiter/window.ts" status="running" />
            <ToolRow tool="search" label="Search" detail="reset( in src" status="done" />
            <ToolRow tool="edit" label="Edit" detail="src/limiter/window.ts" status="done" adds={12} dels={3} />
            <ToolRow tool="web" label="Fetch" detail="rfc 6585" status="done" />
            <ToolRow tool="bash" label="Bash" detail="bun test" status="done" chips={[{ label: "41 passed", tone: "running" }, { label: "1 failed", tone: "error" }]} />
            <ToolRow tool="bash" label="Bash" detail="bun test limiter" status="failed"
              output={"✗ limiter > burst at the window edge\n  expected 429, got 200\n  at limiter.test.ts:88:14"} />
          </ToolBox>
        </Section>

        <Section label="Composer">
          <Composer value="" onChangeText={noop} onSend={noop} placeholder="Message hangar" machine="hangar" model="sonnet-5" />
          <Composer value={text} onChangeText={setText} onSend={noop} machine="hangar" model="sonnet-5" />
          <Composer value="Keep going" onChangeText={noop} onSend={noop} working machine="falcon" model="sonnet-5" />
          <Composer value="Send this one" onChangeText={noop} onSend={noop} sending machine="falcon" model="sonnet-5" />
        </Section>

        <Section label="Session rows">
          <View style={{ marginHorizontal: -16 }}>
            <SectionHeader label="Needs you" count={1} />
            <SessionRow title="Add a retry budget to webhooks" detail="Run a command" status="needs-you" age="2m" machine="hangar" onPress={noop} />
            <SectionHeader label="Working" count={3} />
            <SessionRow title="Fix the burst window in the limiter" detail="Step 3 of 5 · Run the tests" status="working" age="4m" machine="hangar" onPress={noop} />
            <SessionRow title="Speed up the ledger export" detail="Rate limited, retrying" status="retry" age="38s" machine="falcon" onPress={noop} />
            <SessionRow title="Run the nightly benchmarks" detail="bun bench" status="running" age="1m" onPress={noop} />
            <SectionHeader label="Recent" />
            <SessionRow title="Redesign the invoice email" status="done" age="12m" machine="falcon" newPages={2} onPress={noop} />
            <SessionRow title="Paginate the audit log" detail="#608 checks passed" status="done" age="1h" machine="hangar" onPress={noop} />
            <SessionRow title="Rename the shuttle config keys" status="idle" age="3h" machine="falcon" onPress={noop} />
            <SessionRow title="Upgrade the queue client" detail="Stopped with an error" status="error" age="1d" onPress={noop} />
          </View>
        </Section>

        <Section label="Conversation">
          <UserBubble text="A burst of requests gets 200 where it should get 429. Find out why and fix it." />
          <Markdown text={MARKDOWN} />
        </Section>

        <Section label="Pages">
          <Segments value={segment} onChange={setSegment} options={[{ value: "changes", label: "Changes", count: 4 }, { value: "pages", label: "Pages", count: 3 }]} />
          <View>
            <PageRow title="Three ways to page the ledger" detail="Waiting for your pick" age="now" waiting onPress={noop} />
            <PageRow title="Walkthrough: streaming the export" detail="Page" age="10:36" isNew onPress={noop} />
            <PageRow title="Export, before and after" detail="Page" age="10:31" onPress={noop} />
          </View>
          <PageCard title="Export, before and after" detail="Page · 10:31" onPress={noop} />
          <PageCard title="Three ways to page the ledger" detail="Waiting for your pick" waiting onPress={noop} />
          <PagePickCard title="Three ways to page the ledger" onOpen={noop} />
          <View style={{ marginHorizontal: -16 }}>
            <PageChoiceBar text="The agent is waiting for your pick." canSend={false} onSend={noop} onSkip={noop} onReply={noop} />
            <PageChoiceBar text="You picked " strong="Paged table" after="." canSend onSend={noop} onSkip={noop} onReply={noop} />
            <PageChoiceBar text="Sent: " strong="Paged table" after="." canSend={false} />
            <PageReplyBar onPress={noop} />
          </View>
        </Section>

        <Section label="Empty state">
          <EmptyState title="No workflows yet" body="Workflows you run on a machine show up here." action={{ label: "Pair a machine", onPress: noop }} />
        </Section>

        <Section label="Sheet">
          <Row><Button label="Open sheet" icon="chevron-up" onPress={() => setSheet(true)} /></Row>
        </Section>
        <Sheet open={sheet} onClose={() => setSheet(false)} title="Changes">
          <Text tone="muted">Changes, Files and Pages come here.</Text>
        </Sheet>
      </ScrollView>
    </View>
  );
}

export default function KitScreen() {
  const [override, setOverride] = useState<Scheme | null>(null);
  return <ThemeProvider override={override}><Gallery override={override} setOverride={setOverride} /></ThemeProvider>;
}
