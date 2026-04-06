
import { Card, CardContent } from "@/components/ui/card";
import {
  Select,
  SelectTrigger,
  SelectValue,
  SelectContent,
  SelectItem,
} from "@/components/ui/select";
import { useHarnesses } from "@/hooks/use-harnesses";
import { usePersistedState } from "@/hooks/use-persisted-state";
import { Loader2, AlertCircle, CheckCircle2, XCircle } from "lucide-react";

/** localStorage key for the default harness preference. */
export const DEFAULT_HARNESS_KEY = "weave:default-harness";

export function HarnessesTab() {
  const { harnesses, isLoading, error } = useHarnesses();
  const [defaultHarness, setDefaultHarness] = usePersistedState<string>(
    DEFAULT_HARNESS_KEY,
    "opencode"
  );

  const availableHarnesses = harnesses.filter((h) => h.available);

  if (isLoading) {
    return (
      <div className="flex items-center gap-2 text-sm text-muted-foreground p-4">
        <Loader2 className="h-4 w-4 animate-spin" />
        Loading harnesses…
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
    <div className="space-y-6 max-w-xl">
      <Card>
        <CardContent className="p-4 space-y-3">
          <h4 className="text-sm font-semibold">Default Harness</h4>
          <p className="text-sm text-muted-foreground">
            Choose which harness powers new sessions by default.
          </p>
          {availableHarnesses.length > 0 ? (
            <Select value={defaultHarness} onValueChange={setDefaultHarness}>
              <SelectTrigger className="w-[200px]">
                <SelectValue placeholder="Select harness" />
              </SelectTrigger>
              <SelectContent>
                {harnesses.map((h) => (
                  <SelectItem
                    key={h.type}
                    value={h.type}
                    disabled={!h.available}
                  >
                    <span className="flex items-center gap-2">
                      {h.available ? (
                        <CheckCircle2 className="h-3 w-3 text-green-500" />
                      ) : (
                        <XCircle className="h-3 w-3 text-muted-foreground" />
                      )}
                      {h.displayName || h.type}
                      {!h.available && h.reason && (
                        <span className="text-muted-foreground text-xs">
                          ({h.reason})
                        </span>
                      )}
                    </span>
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          ) : (
            <p className="text-sm text-muted-foreground italic">
              No harnesses available.
            </p>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardContent className="p-4 space-y-3">
          <h4 className="text-sm font-semibold">Available Harnesses</h4>
          <p className="text-sm text-muted-foreground">
            Status of all registered harnesses.
          </p>
          <div className="space-y-2">
            {harnesses.map((h) => (
              <div key={h.type} className="flex items-center gap-2 text-sm">
                {h.available ? (
                  <CheckCircle2 className="h-4 w-4 text-green-500" />
                ) : (
                  <XCircle className="h-4 w-4 text-muted-foreground" />
                )}
                <span className={h.available ? "" : "text-muted-foreground"}>
                  {h.displayName || h.type}
                </span>
                {!h.available && h.reason && (
                  <span className="text-xs text-muted-foreground">
                    — {h.reason}
                  </span>
                )}
              </div>
            ))}
          </div>
        </CardContent>
      </Card>
    </div>
  );
}
