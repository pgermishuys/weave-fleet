import { createContext, useContext, type ReactNode } from "react";
import type { ClientConfigResponse, UserMeResponse } from "@/lib/api-types";

interface AppShellContextValue {
  clientConfig: ClientConfigResponse;
  currentUser: UserMeResponse | null;
}

const AppShellContext = createContext<AppShellContextValue | null>(null);

interface AppShellProviderProps extends AppShellContextValue {
  children: ReactNode;
}

export function AppShellProvider({ children, clientConfig, currentUser }: AppShellProviderProps) {
  return (
    <AppShellContext.Provider value={{ clientConfig, currentUser }}>
      {children}
    </AppShellContext.Provider>
  );
}

export function useAppShell(): AppShellContextValue {
  const context = useContext(AppShellContext);
  if (!context) {
    throw new Error("useAppShell must be used within an AppShellProvider");
  }

  return context;
}
