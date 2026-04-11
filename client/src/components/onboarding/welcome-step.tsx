
import { Sparkles } from "lucide-react";
import { Button } from "@/components/ui/button";

interface WelcomeStepProps {
  onNext: () => void;
}

export function WelcomeStep({ onNext }: WelcomeStepProps) {
  return (
    <div className="flex flex-col items-center gap-6 text-center py-4">
      <div className="flex h-16 w-16 items-center justify-center rounded-full bg-primary/10">
        <Sparkles className="h-8 w-8 text-primary" />
      </div>

      <div className="space-y-2">
        <h2 className="text-xl font-semibold">Welcome to Weave</h2>
        <p className="text-sm text-muted-foreground max-w-sm">
          Weave connects AI coding tools to your projects. Let's get you set up in just a few steps.
        </p>
      </div>

      <Button onClick={onNext} className="weave-gradient-bg hover:opacity-90 border-0 w-full max-w-xs">
        Get Started
      </Button>
    </div>
  );
}
