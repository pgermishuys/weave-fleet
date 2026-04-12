
import { Button } from "@/components/ui/button";
import { Avatar, AvatarFallback } from "@/components/ui/avatar";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { apiUrl } from "@/lib/api-client";
import { ChevronDown, Loader2, LogOut, Menu, Plus } from "lucide-react";
import { NewSessionDialog } from "@/components/session/new-session-dialog";
import { useCurrentSessionDirectory } from "@/hooks/use-current-session-directory";
import { useSidebar } from "@/contexts/sidebar-context";
import { useAppShell } from "@/contexts/app-shell-context";
import { useState } from "react";

interface HeaderProps {
  title: string;
  subtitle?: string;
  actions?: React.ReactNode;
}

export function Header({ title, subtitle, actions }: HeaderProps) {
  const { isMobileNav, mobileDrawerOpen, setMobileDrawerOpen } = useSidebar();
  const { clientConfig, currentUser } = useAppShell();

  return (
    <header className="flex h-14 items-center justify-between border-b border-border px-4 sm:px-6">
      <div className="flex items-center gap-2 min-w-0">
        {isMobileNav && (
          <Button
            variant="ghost"
            size="icon"
            aria-label="Open menu"
            aria-expanded={mobileDrawerOpen}
            onClick={() => setMobileDrawerOpen(true)}
            className="shrink-0 fold:hidden"
          >
            <Menu className="h-5 w-5" />
          </Button>
        )}
        <div className="min-w-0">
          <h2 className="text-base sm:text-lg font-semibold truncate">{title}</h2>
          {subtitle && (
            <p className="text-xs text-muted-foreground truncate">{subtitle}</p>
          )}
        </div>
      </div>
      <div className="flex items-center gap-2 shrink-0">
        {actions}
        <UserMenu authEnabled={clientConfig.authEnabled} currentUser={currentUser} />
      </div>
    </header>
  );
}

interface UserMenuProps {
  authEnabled: boolean;
  currentUser: { displayName: string | null; email: string | null } | null;
}

function getUserLabel(currentUser: UserMenuProps["currentUser"]): string {
  return currentUser?.displayName?.trim()
    || currentUser?.email?.trim()
    || "Signed in";
}

function getUserSecondaryLabel(currentUser: UserMenuProps["currentUser"]): string | null {
  const name = currentUser?.displayName?.trim();
  const email = currentUser?.email?.trim();

  if (name && email && name !== email) {
    return email;
  }

  return null;
}

function getUserInitials(currentUser: UserMenuProps["currentUser"]): string {
  const basis = currentUser?.displayName?.trim() || currentUser?.email?.trim() || "U";
  const parts = basis.split(/\s+/).filter(Boolean);

  if (parts.length >= 2) {
    return `${parts[0][0] ?? ""}${parts[1][0] ?? ""}`.toUpperCase();
  }

  return basis.slice(0, 2).toUpperCase();
}

function UserMenu({ authEnabled, currentUser }: UserMenuProps) {
  const [isSigningOut, setIsSigningOut] = useState(false);

  const userLabel = getUserLabel(currentUser);
  const secondaryLabel = getUserSecondaryLabel(currentUser);

  const handleSignOut = async () => {
    if (!authEnabled || isSigningOut) {
      return;
    }

    setIsSigningOut(true);

    try {
      const form = document.createElement("form");
      form.method = "POST";
      form.action = apiUrl(`/auth/logout?returnUrl=${encodeURIComponent("/login")}`);
      form.style.display = "none";

      const csrfMatch = document.cookie
        .split(";")
        .map((cookie) => cookie.trim())
        .find((cookie) => cookie.startsWith(".WeaveFleet.CSRF="));

      if (csrfMatch) {
        const csrfInput = document.createElement("input");
        csrfInput.type = "hidden";
        csrfInput.name = "__RequestVerificationToken";
        csrfInput.value = decodeURIComponent(csrfMatch.substring(".WeaveFleet.CSRF=".length));
        form.appendChild(csrfInput);
      }

      document.body.appendChild(form);
      form.submit();
    } catch {
      setIsSigningOut(false);
    }
  };

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="sm" className="max-w-[220px] gap-2 px-2" aria-label="Current user menu">
          <Avatar size="sm">
            <AvatarFallback>{getUserInitials(currentUser)}</AvatarFallback>
          </Avatar>
          <span className="hidden max-w-[140px] truncate text-sm sm:inline">{userLabel}</span>
          <ChevronDown className="hidden h-3.5 w-3.5 text-muted-foreground sm:inline" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-64">
        <DropdownMenuLabel className="space-y-1">
          <div className="truncate text-sm font-medium">{userLabel}</div>
          {secondaryLabel ? (
            <div className="truncate text-xs font-normal text-muted-foreground">{secondaryLabel}</div>
          ) : null}
          {!authEnabled ? (
            <div className="text-xs font-normal text-muted-foreground">Local mode</div>
          ) : null}
        </DropdownMenuLabel>
        {authEnabled ? (
          <>
            <DropdownMenuSeparator />
            <DropdownMenuItem onSelect={() => void handleSignOut()} disabled={isSigningOut} className="gap-2">
              {isSigningOut ? <Loader2 className="h-4 w-4 animate-spin" /> : <LogOut className="h-4 w-4" />}
              Sign out
            </DropdownMenuItem>
          </>
        ) : null}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

export function NewSessionButton() {
  const currentDirectory = useCurrentSessionDirectory();
  return (
    <NewSessionDialog
      defaultDirectory={currentDirectory ?? undefined}
      trigger={
        <Button size="sm" data-testid="new-session-button" className="gap-1.5 weave-gradient-bg hover:opacity-90 border-0">
          <Plus className="h-3.5 w-3.5" />
          <span className="hidden xs:inline">New Session</span>
          <span className="xs:hidden">New</span>
        </Button>
      }
    />
  );
}
