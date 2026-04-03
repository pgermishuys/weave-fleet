"use client";

import { useState } from "react";
import { Button } from "@/components/ui/button";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import {
  Command,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
} from "@/components/ui/command";
import { Check, Cpu } from "lucide-react";
import type { AvailableProvider } from "@/lib/api-types";
import { cn } from "@/lib/utils";

export interface SelectedModel {
  providerID: string;
  modelID: string;
}

interface ModelSelectorProps {
  providers: AvailableProvider[];
  selectedModel: SelectedModel | null;
  onSelect: (model: SelectedModel | null) => void;
  disabled?: boolean;
}

const DEFAULT_VALUE = "__default__";

function modelValue(providerID: string, modelID: string): string {
  return `${providerID}::${modelID}`;
}

export function parseModelValue(value: string): SelectedModel | null {
  if (value === DEFAULT_VALUE) return null;
  const sep = value.indexOf("::");
  if (sep === -1) return null;
  return { providerID: value.slice(0, sep), modelID: value.slice(sep + 2) };
}

export function composeSearchValue(
  providerName: string,
  modelName: string,
  modelId: string
): string {
  return `${providerName} ${modelName} ${modelId}`;
}

export function ModelSelector({
  providers,
  selectedModel,
  onSelect,
  disabled,
}: ModelSelectorProps) {
  const [open, setOpen] = useState(false);

  const currentValue = selectedModel
    ? modelValue(selectedModel.providerID, selectedModel.modelID)
    : DEFAULT_VALUE;

  // Build a human-readable label for the trigger button
  let label = "Model";
  if (selectedModel) {
    const provider = providers.find((p) => p.id === selectedModel.providerID);
    const model = provider?.models.find((m) => m.id === selectedModel.modelID);
    if (provider && model) {
      label = `${provider.name} / ${model.name}`;
    } else {
      label = selectedModel.modelID;
    }
  }

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button
          variant="outline"
          size="icon"
          disabled={disabled}
          className="h-9 w-9 shrink-0"
          title={label}
        >
          <Cpu className="h-3.5 w-3.5 text-muted-foreground shrink-0" />
        </Button>
      </PopoverTrigger>
      <PopoverContent className="w-64 p-0" align="start">
        {/* Default option — outside Command so it's never filtered by search */}
        <div
          role="option"
          aria-selected={currentValue === DEFAULT_VALUE}
          className="flex items-center gap-2 px-2 py-1.5 text-xs cursor-pointer hover:bg-accent rounded-sm mx-1 mt-1"
          onClick={() => {
            onSelect(null);
            setOpen(false);
          }}
        >
          <Check
            className={cn(
              "h-3 w-3 shrink-0",
              currentValue === DEFAULT_VALUE ? "opacity-100" : "opacity-0"
            )}
          />
          Default
        </div>
        <div className="bg-border -mx-1 h-px my-1" />
        <Command>
          <CommandInput placeholder="Search models…" />
          <CommandList className="max-h-72 thin-scrollbar">
            <CommandEmpty>No matching models</CommandEmpty>
            {providers.map((provider) => (
              <CommandGroup key={provider.id} heading={provider.name}>
                {provider.models.map((model) => {
                  const value = modelValue(provider.id, model.id);
                  return (
                    <CommandItem
                      key={model.id}
                      value={composeSearchValue(
                        provider.name,
                        model.name,
                        model.id
                      )}
                      onSelect={() => {
                        onSelect({
                          providerID: provider.id,
                          modelID: model.id,
                        });
                        setOpen(false);
                      }}
                      className="text-xs"
                    >
                      <Check
                        className={cn(
                          "h-3 w-3 shrink-0",
                          currentValue === value ? "opacity-100" : "opacity-0"
                        )}
                      />
                      {model.name}
                    </CommandItem>
                  );
                })}
              </CommandGroup>
            ))}
          </CommandList>
        </Command>
      </PopoverContent>
    </Popover>
  );
}
