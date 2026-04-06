
import { useState } from "react";
import { Loader2 } from "lucide-react";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import type { DeleteProjectMode } from "@/hooks/use-delete-project";

interface ConfirmDeleteProjectDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  projectName: string;
  onConfirm: (mode: DeleteProjectMode) => void;
  isDeleting?: boolean;
}

export function ConfirmDeleteProjectDialog({
  open,
  onOpenChange,
  projectName,
  onConfirm,
  isDeleting = false,
}: ConfirmDeleteProjectDialogProps) {
  const [mode, setMode] = useState<DeleteProjectMode>("move_to_scratch");

  // Reset to the safer default each time the dialog closes
  const handleOpenChange = (nextOpen: boolean) => {
    if (!nextOpen) setMode("move_to_scratch");
    onOpenChange(nextOpen);
  };

  return (
    <AlertDialog open={open} onOpenChange={handleOpenChange}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>Delete Project</AlertDialogTitle>
          <AlertDialogDescription>
            Delete &ldquo;{projectName}&rdquo;? Choose what happens to its sessions:
          </AlertDialogDescription>
        </AlertDialogHeader>

        {/* Mode selector */}
        <div className="space-y-2 py-2">
          <label className="flex items-start gap-3 cursor-pointer">
            <input
              type="radio"
              name="delete-mode"
              value="move_to_scratch"
              checked={mode === "move_to_scratch"}
              onChange={() => setMode("move_to_scratch")}
              className="mt-0.5 shrink-0"
              disabled={isDeleting}
            />
            <span className="space-y-0.5">
              <span className="block text-sm font-medium">Move sessions to Ungrouped</span>
              <span className="block text-xs text-muted-foreground">
                Sessions are preserved and moved to the Ungrouped bucket.
              </span>
            </span>
          </label>
          <label className="flex items-start gap-3 cursor-pointer">
            <input
              type="radio"
              name="delete-mode"
              value="delete_sessions"
              checked={mode === "delete_sessions"}
              onChange={() => setMode("delete_sessions")}
              className="mt-0.5 shrink-0"
              disabled={isDeleting}
            />
            <span className="space-y-0.5">
              <span className="block text-sm font-medium">Delete all sessions</span>
              <span className="block text-xs text-muted-foreground">
                The project and all its sessions are permanently deleted. This cannot be undone.
              </span>
            </span>
          </label>
        </div>

        <AlertDialogFooter>
          <AlertDialogCancel disabled={isDeleting}>Cancel</AlertDialogCancel>
          <AlertDialogAction
            variant="destructive"
            disabled={isDeleting}
            onClick={(e) => {
              e.preventDefault();
              onConfirm(mode);
            }}
          >
            {isDeleting ? (
              <>
                <Loader2 className="mr-2 size-4 animate-spin" />
                Deleting…
              </>
            ) : (
              "Delete"
            )}
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
