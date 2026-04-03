"use client";

import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { ArrowUpDown } from "lucide-react";

interface SortControlProps {
  sort: "created" | "updated" | "comments";
  direction: "asc" | "desc";
  onChange: (sort: "created" | "updated" | "comments", direction: "asc" | "desc") => void;
}

type SortOption = {
  value: string;
  label: string;
  sort: "created" | "updated" | "comments";
  direction: "asc" | "desc";
};

const SORT_OPTIONS: SortOption[] = [
  { value: "updated-desc", label: "Recently updated", sort: "updated", direction: "desc" },
  { value: "updated-asc", label: "Least recently updated", sort: "updated", direction: "asc" },
  { value: "created-desc", label: "Newest", sort: "created", direction: "desc" },
  { value: "created-asc", label: "Oldest", sort: "created", direction: "asc" },
  { value: "comments-desc", label: "Most commented", sort: "comments", direction: "desc" },
];

export function SortControl({ sort, direction, onChange }: SortControlProps) {
  const currentValue = `${sort}-${direction}`;
  const currentLabel = SORT_OPTIONS.find((o) => o.value === currentValue)?.label ?? "Sort";

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="sm" className="gap-1.5 h-7 text-xs">
          <ArrowUpDown className="h-3 w-3" />
          {currentLabel}
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-48">
        <DropdownMenuRadioGroup
          value={currentValue}
          onValueChange={(value) => {
            const option = SORT_OPTIONS.find((o) => o.value === value);
            if (option) {
              onChange(option.sort, option.direction);
            }
          }}
        >
          {SORT_OPTIONS.map((option) => (
            <DropdownMenuRadioItem key={option.value} value={option.value}>
              {option.label}
            </DropdownMenuRadioItem>
          ))}
        </DropdownMenuRadioGroup>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
