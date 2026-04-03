"use client";

import { useState } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogFooter,
} from "@/components/ui/dialog";
import { Loader2, AlertCircle, CheckCircle2 } from "lucide-react";
import { useSkills } from "@/hooks/use-skills";

interface InstallSkillDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

export function InstallSkillDialog({
  open,
  onOpenChange,
}: InstallSkillDialogProps) {
  const { installSkill } = useSkills();
  const [url, setUrl] = useState("");
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);

  const handleInstall = async () => {
    if (!url.trim() || isLoading) return;

    try {
      setIsLoading(true);
      setError(null);
      setSuccess(null);

      const result = await installSkill({ url: url.trim() });
      setSuccess(`Installed: ${result.skill?.name ?? "skill"}`);
      setUrl("");

      // Close dialog after a short delay
      setTimeout(() => {
        onOpenChange(false);
        setSuccess(null);
      }, 1500);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Installation failed");
    } finally {
      setIsLoading(false);
    }
  };

  const handleClose = (open: boolean) => {
    if (!isLoading) {
      onOpenChange(open);
      if (!open) {
        setUrl("");
        setError(null);
        setSuccess(null);
      }
    }
  };

  return (
    <Dialog open={open} onOpenChange={handleClose}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Install Skill</DialogTitle>
        </DialogHeader>

        <div className="space-y-4 py-2">
          <div className="space-y-2">
            <label className="text-sm text-muted-foreground">
              SKILL.md URL
            </label>
            <Input
              value={url}
              onChange={(e) => setUrl(e.target.value)}
              placeholder="https://raw.githubusercontent.com/user/repo/main/skills/my-skill/SKILL.md"
              disabled={isLoading}
              onKeyDown={(e) => {
                if (e.key === "Enter") handleInstall();
              }}
            />
            <p className="text-[10px] text-muted-foreground">
              Paste the raw URL to a SKILL.md file. The file must contain YAML
              frontmatter with <code>name</code> and <code>description</code>{" "}
              fields.
            </p>
          </div>

          {error && (
            <div className="flex items-center gap-2 text-sm text-red-500">
              <AlertCircle className="h-4 w-4 shrink-0" />
              <span>{error}</span>
            </div>
          )}

          {success && (
            <div className="flex items-center gap-2 text-sm text-green-500">
              <CheckCircle2 className="h-4 w-4 shrink-0" />
              <span>{success}</span>
            </div>
          )}
        </div>

        <DialogFooter>
          <Button
            variant="outline"
            onClick={() => handleClose(false)}
            disabled={isLoading}
          >
            Cancel
          </Button>
          <Button
            onClick={handleInstall}
            disabled={!url.trim() || isLoading}
            className="gap-1.5"
          >
            {isLoading && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
            Install
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
