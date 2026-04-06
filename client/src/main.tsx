import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter } from "react-router";
import { App } from "./app";
import "./app/globals.css";

// Dynamic page title based on profile (mirrors old layout.tsx generateMetadata)
const profile = import.meta.env.VITE_WEAVE_PROFILE;
if (profile && profile !== "default") {
  document.title = `Weave Agent Fleet [${profile}]`;
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <BrowserRouter>
      <App />
    </BrowserRouter>
  </StrictMode>
);
