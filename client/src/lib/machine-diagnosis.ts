/** One check on the way to a machine; `notChecked` for the ones after the step that broke. */
export interface MachineStep {
  label: string;
  state: "ok" | "failed" | "notChecked";
  detail?: string | null;
}

/** Which step of reaching a machine broke, as the server (or this browser) worked it out. `code` is stable. */
export interface MachineDiagnosis {
  code: string;
  headline: string;
  steps: MachineStep[];
  try: string;
}

/** An error that carries the diagnosis behind its one-line message. */
export class DiagnosedError extends Error {
  readonly diagnosis: MachineDiagnosis;

  constructor(message: string, diagnosis: MachineDiagnosis) {
    super(message);
    this.name = "DiagnosedError";
    this.diagnosis = diagnosis;
  }
}

export function diagnosisOf(error: unknown): MachineDiagnosis | null {
  return error instanceof DiagnosedError ? error.diagnosis : null;
}

const STEP_LABELS = ["Name found", "Machine answers", "Port answers", "It's Fleet", "Version fits", "Key accepted"];

function stepsFailingAt(index: number, detail: string): MachineStep[] {
  return STEP_LABELS.map((label, position) => position < index
    ? { label, state: "ok" as const }
    : position === index ? { label, state: "failed" as const, detail } : { label, state: "notChecked" as const });
}

/**
 * A page served over https can't call an http address: the browser blocks it before anything is sent. Null when this
 * page can make the call.
 */
export function mixedContentDiagnosis(pageProtocol: string, baseUrl: string, name: string): MachineDiagnosis | null {
  if (pageProtocol !== "https:" || !baseUrl.toLowerCase().startsWith("http://")) return null;
  return {
    code: "https-page",
    headline: `This page can't call ${name}`,
    steps: stepsFailingAt(1, "This page is https and the address is http, so the browser blocks the call."),
    try: "This page is https, so the browser won't call an http address. Use the machine's tailscale serve address.",
  };
}

/** The headline for a saved SSH machine whose tunnel is down. */
export function tunnelDownDiagnosis(name: string, destination: string | null): MachineDiagnosis {
  return {
    code: "tunnel-down",
    headline: `The SSH tunnel to ${name} is down`,
    steps: stepsFailingAt(1, "The tunnel Fleet keeps open isn't up."),
    try: `Fleet opens it again by itself. If it stays down, check that ssh ${destination ?? name} works in a terminal.`,
  };
}

/** The details as plain text, for pasting into a message to whoever helps. */
export function diagnosisText(diagnosis: MachineDiagnosis): string {
  const steps = diagnosis.steps.map((step) => {
    const mark = step.state === "ok" ? "ok" : step.state === "failed" ? "failed" : "not checked";
    return `- ${step.label}: ${mark}${step.detail ? ` (${step.detail})` : ""}`;
  });
  return [diagnosis.headline, ...steps, `Try: ${diagnosis.try}`].join("\n");
}
