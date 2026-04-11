
import { useState } from "react";
import { Key, Check, Loader2, AlertCircle, ArrowRight } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { useCredentials } from "@/hooks/use-credentials";
import type { StoreCredentialRequest } from "@/lib/api-types";

const QUICK_PROVIDERS = [
  { label: "Anthropic", namespace: "anthropic", kind: "api-key", placeholder: "sk-ant-..." },
  { label: "OpenAI", namespace: "openai", kind: "api-key", placeholder: "sk-proj-..." },
];

interface CredentialStepProps {
  /** If true, the user can skip this step (harness supports built-in access). */
  canSkip: boolean;
  onNext: () => void;
}

export function CredentialStep({ canSkip, onNext }: CredentialStepProps) {
  const { storeCredential } = useCredentials();
  const [selected, setSelected] = useState(QUICK_PROVIDERS[0]);
  const [value, setValue] = useState("");
  const [isSaving, setIsSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<string | undefined>();

  const handleSave = async () => {
    if (!value.trim()) {
      setError("Enter an API key value.");
      return;
    }

    setIsSaving(true);
    setError(undefined);
    try {
      const req: StoreCredentialRequest = {
        label: `My ${selected.label} API Key`,
        namespace: selected.namespace,
        kind: selected.kind,
        value: value.trim(),
      };
      await storeCredential(req);
      setSaved(true);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save API key");
    } finally {
      setIsSaving(false);
    }
  };

  return (
    <div className="flex flex-col gap-5 py-2">
      <div className="space-y-1.5">
        <h2 className="text-lg font-semibold">Connect your API keys</h2>
        <p className="text-sm text-muted-foreground">
          Add API keys to use AI providers in your sessions. You can add or change these later in Settings.
        </p>
        {canSkip ? (
          <p className="text-xs text-muted-foreground italic">
            The default tool includes built-in access — you can skip this step.
          </p>
        ) : null}
      </div>

      {!saved ? (
        <>
          <div className="flex gap-2 flex-wrap">
            {QUICK_PROVIDERS.map((provider) => (
              <Badge
                key={provider.namespace}
                variant={selected.namespace === provider.namespace ? "default" : "outline"}
                className="cursor-pointer"
                onClick={() => { setSelected(provider); setValue(""); setError(undefined); }}
              >
                {provider.label}
              </Badge>
            ))}
          </div>

          <div className="space-y-2">
            <label className="text-xs font-medium" htmlFor="onboard-api-key">
              {selected.label} API Key
            </label>
            <Input
              id="onboard-api-key"
              type="password"
              placeholder={selected.placeholder}
              value={value}
              onChange={(e) => setValue(e.target.value)}
              disabled={isSaving}
            />
          </div>

          {error ? (
            <div className="flex items-start gap-2 rounded-md border border-red-500/20 bg-red-500/10 px-3 py-2 text-xs text-red-600 dark:text-red-400">
              <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
              <span>{error}</span>
            </div>
          ) : null}

          <div className="flex gap-2">
            <Button
              onClick={() => void handleSave()}
              disabled={isSaving}
              className="weave-gradient-bg hover:opacity-90 border-0"
            >
              {isSaving ? <Loader2 className="h-3.5 w-3.5 animate-spin mr-1.5" /> : <Key className="h-3.5 w-3.5 mr-1.5" />}
              Save API Key
            </Button>
            {canSkip ? (
              <Button variant="outline" onClick={onNext}>
                Skip for now
              </Button>
            ) : null}
          </div>
        </>
      ) : (
        <div className="space-y-4">
          <div className="flex items-center gap-2 rounded-md border border-green-500/30 bg-green-500/10 px-3 py-2 text-sm text-green-700 dark:text-green-400">
            <Check className="h-4 w-4 shrink-0" />
            <span>{selected.label} API key saved.</span>
          </div>
          <Button onClick={onNext} className="weave-gradient-bg hover:opacity-90 border-0 w-full">
            Continue
            <ArrowRight className="h-3.5 w-3.5 ml-1.5" />
          </Button>
        </div>
      )}
    </div>
  );
}
