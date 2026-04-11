
import { CheckCircle2, Rocket } from "lucide-react";
import { Button } from "@/components/ui/button";

interface ReadyStepProps {
  onFinish: () => void;
}

export function ReadyStep({ onFinish }: ReadyStepProps) {
  return (
    <div className="flex flex-col items-center gap-6 text-center py-4">
      <div className="flex h-16 w-16 items-center justify-center rounded-full bg-green-500/10">
        <CheckCircle2 className="h-8 w-8 text-green-600 dark:text-green-400" />
      </div>

      <div className="space-y-2">
        <h2 className="text-xl font-semibold">You're all set!</h2>
        <p className="text-sm text-muted-foreground max-w-sm">
          Start your first session and let Weave handle the rest. You can manage your API keys anytime in Settings.
        </p>
      </div>

      <Button onClick={onFinish} className="weave-gradient-bg hover:opacity-90 border-0 w-full max-w-xs gap-1.5">
        <Rocket className="h-3.5 w-3.5" />
        Start a Session
      </Button>
    </div>
  );
}
