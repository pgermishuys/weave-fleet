
import { useState } from "react";
import { Key, Plus, Loader2, AlertCircle } from "lucide-react";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { CredentialCard } from "@/components/settings/credential-card";
import { useCredentials } from "@/hooks/use-credentials";
import type { StoreCredentialRequest } from "@/lib/api-types";

const COMMON_PROVIDERS: Array<{ label: string; namespace: string; kind: string }> = [
  { label: "Anthropic", namespace: "anthropic", kind: "api-key" },
  { label: "OpenAI", namespace: "openai", kind: "api-key" },
];

function AddCredentialForm({ onStore, onCancel }: {
  onStore: (req: StoreCredentialRequest) => Promise<void>;
  onCancel: () => void;
}) {
  const [label, setLabel] = useState("");
  const [namespace, setNamespace] = useState("");
  const [kind, setKind] = useState("api-key");
  const [value, setValue] = useState("");
  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState<string | undefined>();

  const applyPreset = (preset: typeof COMMON_PROVIDERS[0]) => {
    setNamespace(preset.namespace);
    setKind(preset.kind);
    if (!label.trim()) {
      setLabel(`My ${preset.label} API Key`);
    }
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!label.trim() || !namespace.trim() || !kind.trim() || !value.trim()) {
      setError("All fields are required.");
      return;
    }

    setIsSaving(true);
    setError(undefined);
    try {
      await onStore({ label: label.trim(), namespace: namespace.trim(), kind: kind.trim(), value: value.trim() });
    } catch (err) {
      setIsSaving(false);
      setError(err instanceof Error ? err.message : "Failed to save credential");
    }
  };

  return (
    <Card>
      <CardContent className="p-4 space-y-4">
        <h4 className="text-sm font-semibold">Add API Key</h4>

        <div className="space-y-1.5">
          <p className="text-xs text-muted-foreground">Quick select provider:</p>
          <div className="flex flex-wrap gap-2">
            {COMMON_PROVIDERS.map((preset) => (
              <Badge
                key={preset.namespace}
                variant="outline"
                className="cursor-pointer hover:bg-accent"
                onClick={() => applyPreset(preset)}
              >
                {preset.label}
              </Badge>
            ))}
          </div>
        </div>

        <form onSubmit={(e) => void handleSubmit(e)} className="space-y-3">
          <div className="space-y-1.5">
            <label className="text-xs font-medium" htmlFor="cred-label">
              Label
            </label>
            <Input
              id="cred-label"
              placeholder="e.g. My Anthropic API Key"
              value={label}
              onChange={(e) => setLabel(e.target.value)}
              disabled={isSaving}
            />
          </div>

          <div className="grid grid-cols-2 gap-2">
            <div className="space-y-1.5">
              <label className="text-xs font-medium" htmlFor="cred-provider">
                Provider
              </label>
              <Input
                id="cred-provider"
                placeholder="e.g. anthropic"
                value={namespace}
                onChange={(e) => setNamespace(e.target.value)}
                disabled={isSaving}
              />
            </div>
            <div className="space-y-1.5">
              <label className="text-xs font-medium" htmlFor="cred-type">
                Type
              </label>
              <Input
                id="cred-type"
                placeholder="e.g. api-key"
                value={kind}
                onChange={(e) => setKind(e.target.value)}
                disabled={isSaving}
              />
            </div>
          </div>

          <div className="space-y-1.5">
            <label className="text-xs font-medium" htmlFor="cred-value">
              API Key Value
            </label>
            <Input
              id="cred-value"
              type="password"
              placeholder="Paste your API key here"
              value={value}
              onChange={(e) => setValue(e.target.value)}
              disabled={isSaving}
            />
            <p className="text-xs text-muted-foreground">
              The value is encrypted at rest and never displayed after saving.
            </p>
          </div>

          {error ? (
            <div className="flex items-start gap-2 rounded-md border border-red-500/20 bg-red-500/10 px-3 py-2 text-xs text-red-600 dark:text-red-400">
              <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
              <span>{error}</span>
            </div>
          ) : null}

          <div className="flex gap-2">
            <Button type="submit" size="sm" className="weave-gradient-bg hover:opacity-90 border-0" disabled={isSaving}>
              {isSaving ? <Loader2 className="h-3 w-3 animate-spin mr-1" /> : null}
              Save API Key
            </Button>
            <Button type="button" size="sm" variant="outline" onClick={onCancel} disabled={isSaving}>
              Cancel
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>
  );
}

export function CredentialsTab() {
  const { credentials, isLoading, error, storeCredential, updateCredential, deleteCredential } = useCredentials();
  const [showAddForm, setShowAddForm] = useState(false);

  const handleStore = async (req: StoreCredentialRequest) => {
    await storeCredential(req);
    setShowAddForm(false);
  };

  if (isLoading) {
    return (
      <div className="flex items-center gap-2 text-sm text-muted-foreground p-4">
        <Loader2 className="h-4 w-4 animate-spin" />
        Loading API keys…
      </div>
    );
  }

  if (error) {
    return (
      <div className="flex items-center gap-2 text-sm text-red-600 dark:text-red-400 p-4">
        <AlertCircle className="h-4 w-4" />
        {error}
      </div>
    );
  }

  return (
    <div className="space-y-4 max-w-2xl">
      <div className="flex items-center justify-between">
        <div>
          <p className="text-sm text-muted-foreground">
            Store encrypted API keys for use with your sessions. Keys are encrypted at rest and never displayed after saving.
          </p>
        </div>
        {!showAddForm ? (
          <Button size="sm" onClick={() => setShowAddForm(true)} className="shrink-0 gap-1.5">
            <Plus className="h-3.5 w-3.5" />
            Add API Key
          </Button>
        ) : null}
      </div>

      {showAddForm ? (
        <AddCredentialForm
          onStore={handleStore}
          onCancel={() => setShowAddForm(false)}
        />
      ) : null}

      {credentials.length === 0 && !showAddForm ? (
        <div className="flex flex-col items-center gap-3 rounded-lg border border-dashed border-border p-8 text-center">
          <Key className="h-8 w-8 text-muted-foreground/50" />
          <div>
            <p className="text-sm font-medium">No API keys stored</p>
            <p className="text-xs text-muted-foreground mt-1">
              Add an API key to enable sessions that require provider access.
            </p>
          </div>
          <Button size="sm" onClick={() => setShowAddForm(true)} className="gap-1.5">
            <Plus className="h-3.5 w-3.5" />
            Add API Key
          </Button>
        </div>
      ) : null}

      {credentials.length > 0 ? (
        <div className="grid gap-3 sm:grid-cols-1 md:grid-cols-2">
          {credentials.map((credential) => (
            <CredentialCard
              key={credential.id}
              credential={credential}
              onUpdate={updateCredential}
              onDelete={deleteCredential}
            />
          ))}
        </div>
      ) : null}
    </div>
  );
}
