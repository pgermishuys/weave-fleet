
import { useState, useCallback } from "react";
import { useNavigate } from "react-router";
import {
  Dialog,
  DialogContent,
} from "@/components/ui/dialog";
import { WelcomeStep } from "@/components/onboarding/welcome-step";
import { CredentialStep } from "@/components/onboarding/credential-step";
import { ReadyStep } from "@/components/onboarding/ready-step";
import { apiFetch } from "@/lib/api-client";

type WizardStep = "welcome" | "credentials" | "ready";

interface OnboardingWizardProps {
  /** Whether the default harness supports built-in access (i.e. credentials are optional). */
  credentialsOptional: boolean;
  onComplete: () => void;
}

async function markOnboardingComplete() {
  await apiFetch("/api/user/me/complete-onboarding", { method: "POST" });
}

export function OnboardingWizard({ credentialsOptional, onComplete }: OnboardingWizardProps) {
  const navigate = useNavigate();
  const [step, setStep] = useState<WizardStep>("welcome");

  const handleWelcomeNext = useCallback(() => {
    setStep("credentials");
  }, []);

  const handleCredentialNext = useCallback(() => {
    setStep("ready");
  }, []);

  const handleFinish = useCallback(async () => {
    try {
      await markOnboardingComplete();
    } catch {
      // Best-effort — don't block the user from proceeding
    }
    onComplete();
    navigate("/");
  }, [navigate, onComplete]);

  return (
    <Dialog open modal>
      <DialogContent
        className="sm:max-w-md"
        // Prevent closing via Escape — must finish or dismiss deliberately
        onInteractOutside={(e) => e.preventDefault()}
        onEscapeKeyDown={(e) => e.preventDefault()}
      >
        <div className="min-h-[280px] flex flex-col justify-center">
          {step === "welcome" ? (
            <WelcomeStep onNext={handleWelcomeNext} />
          ) : step === "credentials" ? (
            <CredentialStep
              canSkip={credentialsOptional}
              onNext={() => handleCredentialNext()}
            />
          ) : (
            <ReadyStep onFinish={() => void handleFinish()} />
          )}
        </div>

        <div className="flex justify-center gap-1.5 pt-2">
          {(["welcome", "credentials", "ready"] as WizardStep[]).map((s) => (
            <span
              key={s}
              className={`h-1.5 w-6 rounded-full transition-colors ${
                s === step ? "bg-primary" : "bg-muted"
              }`}
            />
          ))}
        </div>
      </DialogContent>
    </Dialog>
  );
}
