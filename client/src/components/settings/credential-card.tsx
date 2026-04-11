
import { useState } from "react";
import { Key, Trash2, Pencil, Check, X, Loader2 } from "lucide-react";
import { Card, CardContent } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import type { CredentialSummary, StoreCredentialRequest } from "@/lib/api-types";

interface CredentialCardProps {
  credential: CredentialSummary;
  onUpdate: (id: string, req: StoreCredentialRequest) => Promise<void>;
  onDelete: (id: string) => Promise<void>;
}

export function CredentialCard({ credential, onUpdate, onDelete }: CredentialCardProps) {
  const [isEditing, setIsEditing] = useState(false);
  const [isDeleting, setIsDeleting] = useState(false);
  const [isSaving, setIsSaving] = useState(false);
  const [newValue, setNewValue] = useState("");
  const [error, setError] = useState<string | undefined>();

  const handleSave = async () => {
    if (!newValue.trim()) {
      setError("API key value is required.");
      return;
    }

    setIsSaving(true);
    setError(undefined);
    try {
      await onUpdate(credential.id, {
        label: credential.label,
        namespace: credential.namespace,
        kind: credential.kind,
        value: newValue.trim(),
      });
      setIsEditing(false);
      setNewValue("");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to update credential");
    } finally {
      setIsSaving(false);
    }
  };

  const handleDelete = async () => {
    setIsDeleting(true);
    setError(undefined);
    try {
      await onDelete(credential.id);
    } catch (err) {
      setIsDeleting(false);
      setError(err instanceof Error ? err.message : "Failed to remove credential");
    }
  };

  const handleCancelEdit = () => {
    setIsEditing(false);
    setNewValue("");
    setError(undefined);
  };

  return (
    <Card>
      <CardContent className="p-4 space-y-3">
        <div className="flex items-start justify-between gap-2">
          <div className="flex items-center gap-2 min-w-0">
            <Key className="h-4 w-4 shrink-0 text-muted-foreground" />
            <div className="min-w-0">
              <p className="text-sm font-semibold truncate">{credential.label}</p>
              <p className="text-xs text-muted-foreground truncate">
                {credential.namespace} · {credential.kind}
              </p>
            </div>
          </div>
          <Badge
            variant="secondary"
            className="shrink-0 text-[10px] bg-green-500/10 text-green-600 dark:text-green-400"
          >
            <Check className="h-3 w-3 mr-1" />
            Connected
          </Badge>
        </div>

        <p className="text-xs text-muted-foreground font-mono">
          ···· {credential.displayHint}
        </p>

        {isEditing ? (
          <div className="space-y-2">
            <Input
              type="password"
              placeholder="Enter new API key value"
              value={newValue}
              onChange={(e) => setNewValue(e.target.value)}
              autoFocus
              disabled={isSaving}
            />
            {error ? (
              <p className="text-xs text-red-600 dark:text-red-400">{error}</p>
            ) : null}
            <div className="flex gap-2">
              <Button size="sm" onClick={() => void handleSave()} disabled={isSaving}>
                {isSaving ? <Loader2 className="h-3 w-3 animate-spin mr-1" /> : <Check className="h-3 w-3 mr-1" />}
                Save
              </Button>
              <Button size="sm" variant="outline" onClick={handleCancelEdit} disabled={isSaving}>
                <X className="h-3 w-3 mr-1" />
                Cancel
              </Button>
            </div>
          </div>
        ) : (
          <div className="flex gap-2">
            <Button
              size="sm"
              variant="outline"
              onClick={() => { setIsEditing(true); setError(undefined); }}
              disabled={isDeleting}
            >
              <Pencil className="h-3 w-3 mr-1" />
              Update key
            </Button>
            <Button
              size="sm"
              variant="outline"
              className="text-red-600 dark:text-red-400 hover:bg-red-500/10"
              onClick={() => void handleDelete()}
              disabled={isDeleting}
            >
              {isDeleting ? <Loader2 className="h-3 w-3 animate-spin mr-1" /> : <Trash2 className="h-3 w-3 mr-1" />}
              Remove
            </Button>
          </div>
        )}

        {!isEditing && error ? (
          <p className="text-xs text-red-600 dark:text-red-400">{error}</p>
        ) : null}
      </CardContent>
    </Card>
  );
}
