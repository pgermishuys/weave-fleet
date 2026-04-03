# Monorepo Bootstrap Plan

## TL;DR
> **Summary**: Bootstrap the weave-fleet monorepo with a .NET 10 backend skeleton that serves the existing frontend (copied from weave-agent-fleet) as a static SPA, with stub API endpoints, health checks, and dev workflow for both halves.
> **Estimated Effort**: Large

## Context

### Original Request
Set up the weave-fleet monorepo combining a new .NET 10 backend with the existing Next.js frontend from weave-agent-fleet. The backend serves the frontend's static export, provides the REST API the frontend expects, and handles real-time communication via SignalR (replacing raw WebSocket).

### Key Findings

**Frontend (weave-agent-fleet):**
- Next.js 16.1.7 / React 19.2.4 / TypeScript 5.9.3 / Tailwind CSS 4 / shadcn/ui (new-york style)
- Static export mode: `output: 'export'`, `distDir: 'dist'` → produces `dist/` with `index.html` + JS/CSS
- `src/app/api/` contains 15 subdirectories with ~48 Node.js API route handlers — these are server-side only and must be **excluded** from the static build (handled by `scripts/build-spa.mjs` which renames `api/` → `_api/` during build)
- `src/lib/api-client.ts`: All API calls go through `apiUrl(path)` / `apiFetch(path)` which prepend `NEXT_PUBLIC_API_BASE_URL` if set, otherwise use relative URLs. WebSocket URLs derived via `wsUrl(path)` with `ws://` scheme conversion.
- `src/hooks/use-weave-socket.ts`: Singleton WebSocket connecting to `/ws` with topic-based subscribe/unsubscribe JSON protocol
- `src/lib/server/` (25 files): Node.js server-side logic (process manager, DB, workspace manager, etc.) — not needed in static export
- `src/lib/tauri.ts`: Tauri detection utilities — gracefully no-ops outside Tauri
- Dynamic routes (`sessions/[id]`, `github/[owner]/[repo]`) use `generateStaticParams()` with placeholder entries for RSC payload generation
- `public/` contains only SVG icons and `weave_logo.png`
- Node 22.16.0 (per `.node-version`)
- Dependencies include `@opencode-ai/sdk`, `better-sqlite3` (server-only), `@tauri-apps/api` (Tauri-only)

**Backend vision (from CONSTITUTION + dotnet-rewrite-plan):**
- .NET 10 / C# 14, Clean Architecture (Domain → Application → Infrastructure → Api)
- WeaveFleet.sln with 5 src projects + 4 test projects
- EF Core + SQLite, SignalR hub at `/ws`, OpenTelemetry
- Minimal APIs with typed endpoints under `/api/*`
- Static file serving + SPA fallback (`index.html` for non-API routes)
- CLI entry point via System.CommandLine (`weave serve --port 3000`)

**Critical insight — the frontend has two modes:**
1. **Standalone mode** (Go/Next.js server): API routes in `src/app/api/` handle requests server-side
2. **SPA mode** (static export): API routes are excluded; frontend makes relative fetch calls to whatever backend serves it

We want SPA mode. The .NET backend replaces both the Go backend AND the Next.js API routes.

## Objectives

### Core Objective
Create a working monorepo where `dotnet run` serves the frontend SPA and responds to health checks, and the frontend dev server can proxy API calls to the backend.

### Deliverables
- [x] Monorepo directory structure with frontend and backend coexisting
- [x] Frontend source copied and adapted from weave-agent-fleet
- [x] .NET backend skeleton with static file serving and SPA fallback
- [x] Stub API endpoints returning mock data matching frontend types
- [x] Dev workflow: concurrent frontend + backend with hot reload
- [x] Production build: single `dotnet publish` artifact containing the SPA

### Definition of Done
- [x] `cd client && npm install && npm run build:spa` produces `client/dist/` with valid SPA
- [x] `dotnet build` succeeds with 0 warnings
- [x] `dotnet run --project src/WeaveFleet.Api` serves the SPA at `http://localhost:3000/`
- [x] `GET /healthz` returns 200
- [x] `GET /api/sessions` returns `[]` (empty array stub)
- [x] `GET /api/fleet/summary` returns mock `FleetSummaryResponse`
- [ ] Frontend loads in browser, sidebar renders, no console errors about missing API

### Guardrails (Must NOT)
- Do NOT implement real business logic yet — stubs only (this is a bootstrap)
- Do NOT copy the Go backend code (`go/` directory)
- Do NOT copy `src/lib/server/` (Node.js server-side code) — it's replaced by .NET
- Do NOT copy `src-tauri/` (Tauri desktop support deferred)
- Do NOT remove Tauri detection code from frontend (`src/lib/tauri.ts`) — it gracefully no-ops
- Do NOT break the existing `src/app/api/` routes — copy them but they won't be used in SPA mode
- Do NOT implement SignalR yet — use a minimal WebSocket echo/stub at `/ws`

---

## TODOs

### Phase 1: Monorepo Structure

- [x] 1. **Create directory layout**
  **What**: Establish the monorepo directory structure
  **Files**: Create the following directories:
  ```
  C:\source\weave-fleet\
  ├── .weave/                          # (existing) Plans, constitution
  ├── client/                          # Frontend (Next.js SPA)
  │   ├── src/                         # Copied from weave-agent-fleet
  │   ├── public/                      # Static assets
  │   ├── scripts/                     # Build scripts
  │   ├── package.json
  │   ├── next.config.ts
  │   ├── tsconfig.json
  │   ├── postcss.config.mjs
  │   ├── vitest.config.ts
  │   ├── eslint.config.mjs
  │   ├── components.json
  │   └── .node-version
  ├── src/                             # .NET backend
  │   ├── WeaveFleet.Domain/
  │   ├── WeaveFleet.Application/
  │   ├── WeaveFleet.Infrastructure/
  │   ├── WeaveFleet.Api/
  │   └── WeaveFleet.Cli/
  ├── tests/                           # .NET tests
  │   ├── WeaveFleet.Domain.Tests/
  │   ├── WeaveFleet.Application.Tests/
  │   ├── WeaveFleet.Infrastructure.Tests/
  │   └── WeaveFleet.Api.Tests/
  ├── WeaveFleet.sln
  ├── Directory.Build.props
  ├── Directory.Packages.props
  ├── global.json
  ├── .editorconfig
  ├── .gitignore
  └── API_CONTRACT_QUICK_REFERENCE.md  # (existing)
  ```
  **Acceptance**: `ls` shows the expected top-level structure

- [x] 2. **Create .gitignore**
  **What**: Root `.gitignore` covering both .NET and Node.js artifacts
  **Files**: `C:\source\weave-fleet\.gitignore`
  **Content should include**:
  ```
  # .NET
  bin/
  obj/
  *.user
  *.suo
  .vs/
  
  # Node.js (client/)
  client/node_modules/
  client/.next/
  client/dist/
  client/out/
  
  # Build artifacts
  publish/
  
  # Environment
  .env*
  *.pem
  
  # OS
  .DS_Store
  Thumbs.db
  
  # IDE
  .idea/
  .vscode/
  
  # Weave internal
  .weave/state/
  ```
  **Acceptance**: `git status` doesn't show build artifacts after build

- [x] 3. **Create `global.json`**
  **What**: Pin .NET SDK version
  **Files**: `C:\source\weave-fleet\global.json`
  ```json
  {
    "sdk": {
      "version": "10.0.100",
      "rollForward": "latestFeature"
    }
  }
  ```
  **Acceptance**: `dotnet --version` matches the pinned SDK

- [x] 4. **Create `WeaveFleet.sln`**
  **What**: Solution file with all projects. Create with `dotnet new sln` then add projects.
  **Files**: `C:\source\weave-fleet\WeaveFleet.sln`
  **Acceptance**: `dotnet sln list` shows all 9 projects (5 src + 4 test)

- [x] 5. **Create `Directory.Build.props`**
  **What**: Shared build properties for all .NET projects
  **Files**: `C:\source\weave-fleet\Directory.Build.props`
  ```xml
  <Project>
    <PropertyGroup>
      <TargetFramework>net10.0</TargetFramework>
      <LangVersion>14</LangVersion>
      <Nullable>enable</Nullable>
      <ImplicitUsings>enable</ImplicitUsings>
      <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
      <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
      <AnalysisLevel>latest-recommended</AnalysisLevel>
    </PropertyGroup>
  </Project>
  ```
  **Acceptance**: All projects inherit these properties

- [x] 6. **Create `Directory.Packages.props`**
  **What**: Central package management for NuGet. Use versions from dotnet-rewrite-plan but only include packages needed for bootstrap.
  **Files**: `C:\source\weave-fleet\Directory.Packages.props`
  **Packages for bootstrap phase**:
  ```xml
  <Project>
    <PropertyGroup>
      <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    </PropertyGroup>
    <ItemGroup>
      <!-- Core -->
      <PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0" />
      <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.0" />
      <!-- Testing -->
      <PackageVersion Include="xunit" Version="2.9.0" />
      <PackageVersion Include="xunit.runner.visualstudio" Version="2.8.2" />
      <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    </ItemGroup>
  </Project>
  ```
  **Acceptance**: `dotnet restore` resolves all packages

- [x] 7. **Create `.editorconfig`**
  **What**: C# coding style rules (consistent naming, formatting)
  **Files**: `C:\source\weave-fleet\.editorconfig`
  **Key rules**: 4-space indentation, file-scoped namespaces, `sealed` preference, no optional parameters
  **Acceptance**: `dotnet format --verify-no-changes` passes

### Phase 2: Frontend Bootstrap

- [x] 8. **Copy frontend source from weave-agent-fleet**
  **What**: Copy the frontend source files into `client/` directory. Use direct file copy (not git submodule).
  **Files**: Copy from `C:\source\weave-agent-fleet` to `C:\source\weave-fleet\client\` (see list below)
  **Files to copy** (from weave-agent-fleet root):
  ```
  src/                    → client/src/
  public/                 → client/public/
  scripts/build-spa.mjs   → client/scripts/build-spa.mjs
  package.json            → client/package.json
  next.config.ts          → client/next.config.ts
  tsconfig.json           → client/tsconfig.json
  postcss.config.mjs      → client/postcss.config.mjs
  vitest.config.ts        → client/vitest.config.ts
  eslint.config.mjs       → client/eslint.config.mjs
  components.json         → client/components.json
  .node-version           → client/.node-version
  ```
  **Do NOT copy**:
  - `go/` (Go backend — replaced by .NET)
  - `src-tauri/` (Tauri desktop support — deferred)
  - `node_modules/` (regenerated by `npm install`)
  - `.next/` (build cache)
  - `dist/` (build output)
  - `bun.lock` (not using bun)
  - `cli.js` (standalone CLI — replaced)
  - `scripts/assemble-standalone.sh`, `scripts/assemble-standalone.ps1` (standalone assembly — replaced)
  - `scripts/install.sh`, `scripts/install.ps1` (installer scripts — replaced)
  - `scripts/launcher.sh`, `scripts/launcher.cmd` (launcher scripts — replaced)
  - `scripts/tauri-prebuild.mjs` (Tauri-only)
  - `.github/` (CI — will create new)
  - `RELEASE.md`, `README.md`, `FRONTEND_BACKEND_ARCHITECTURE.md` (docs — will create new)
  - `COMPLETE_API_CONTRACT.md`, `API_CONTRACT_QUICK_REFERENCE.md` (already in weave-fleet root)
  **Acceptance**: `ls client/src/app/` shows the app structure

- [x] 9. **Modify `client/package.json`**
  **What**: Update package name, remove Go/Tauri-specific scripts and dependencies
  **Files**: `C:\source\weave-fleet\client\package.json`
  **Changes**:
  - Rename `"name"` from `"opencode-orchestrator"` to `"weave-fleet-client"`
  - Remove scripts: `build:cli`, `build:standalone`, `tauri`, `tauri:dev`, `tauri:build`, `tauri:build:stable`, `tauri:build:dev`
  - Keep scripts: `dev`, `dev:ui`, `build`, `build:spa`, `start`, `lint`, `typecheck`, `test`, `test:watch`
  - Add script: `"dev:split": "NEXT_PUBLIC_API_BASE_URL=http://localhost:3000 next dev --port 3001"` (for split-mode dev against .NET backend)
  - Remove dependencies: `better-sqlite3`, `@tauri-apps/api` (server-only and Tauri-only)
  - Remove devDependencies: `@tauri-apps/cli`, `@types/better-sqlite3`, `esbuild`
  - Keep `@opencode-ai/sdk` — it's used by client-side hooks for type definitions
  **Acceptance**: `cd client && npm install` succeeds, `npm run typecheck` passes

- [x] 10. **Modify `client/next.config.ts`**
  **What**: Remove Go-specific comments, keep static export config unchanged
  **Files**: `C:\source\weave-fleet\client\next.config.ts`
  **Changes**:
  - Update comment: "Served by the .NET backend" instead of "Served by the Go binary via go:embed"
  - Keep `output: 'export'`, `distDir: 'dist'`, `compress: true`, `images: { unoptimized: true }`
  - Keep `NEXT_PUBLIC_API_BASE_URL` env var — it works as-is for split-mode dev
  - Keep `turbopack.root` setting
  **Acceptance**: `npm run build:spa` produces `client/dist/index.html`

- [x] 11. **Modify `client/src/lib/api-client.ts` for runtime base URL**
  **What**: Make the API base URL runtime-configurable instead of build-time only. This enables the future multi-backend vision.
  **Files**: `C:\source\weave-fleet\client\src\lib\api-client.ts`
  **Strategy**: Add a `setApiBase(url)` function and a `getApiBase()` that checks:
  1. Runtime override (set via `setApiBase()` or `window.__WEAVE_API_BASE__`)
  2. Build-time `NEXT_PUBLIC_API_BASE_URL` (existing behavior, fallback)
  3. Empty string (relative URLs, default)
  
  ```typescript
  // Runtime-configurable base URL
  let runtimeBase: string | null = null;
  
  export function setApiBase(url: string): void {
    runtimeBase = url.replace(/\/$/, "");
  }
  
  function getApiBase(): string {
    if (runtimeBase !== null) return runtimeBase;
    // Check window global (can be injected by backend via <script> tag)
    if (typeof window !== "undefined" && (window as any).__WEAVE_API_BASE__) {
      return ((window as any).__WEAVE_API_BASE__ as string).replace(/\/$/, "");
    }
    // Fallback to build-time env var
    return (process.env.NEXT_PUBLIC_API_BASE_URL ?? "").replace(/\/$/, "");
  }
  
  export function apiUrl(path: string): string {
    const base = getApiBase();
    return base ? `${base}${path}` : path;
  }
  ```
  Keep `wsUrl`, `sseUrl`, `apiFetch` working as before but using `getApiBase()`.
  **Acceptance**: `apiUrl("/api/sessions")` returns `/api/sessions` by default, `setApiBase("http://other:3000")` changes it

- [x] 12. **Add `client/.gitignore`**
  **What**: Frontend-specific gitignore within the client directory
  **Files**: `C:\source\weave-fleet\client\.gitignore`
  ```
  node_modules/
  .next/
  dist/
  out/
  *.tsbuildinfo
  next-env.d.ts
  ```
  **Acceptance**: `git status` inside client/ doesn't show build artifacts

- [x] 13. **Create `client/.env.development.split`**
  **What**: Environment file for split-mode development (frontend dev server + .NET backend)
  **Files**: `C:\source\weave-fleet\client\.env.development.split`
  ```
  # Split-mode development: frontend dev server proxies API calls to .NET backend
  NEXT_PUBLIC_API_BASE_URL=http://localhost:3000
  ```
  **Acceptance**: `cp .env.development.split .env.local && npm run dev` connects to localhost:3000

- [x] 14. **Verify frontend builds independently**
  **What**: Run the frontend build to verify it produces valid output
  **Files**: `C:\source\weave-fleet\client\dist\index.html` (build output — verified, not created manually)
  **Commands**:
  ```bash
  cd client
  npm install
  npm run build:spa    # Should produce dist/index.html
  npm run typecheck    # Should pass with 0 errors
  npm run lint         # Should pass
  ```
  **Acceptance**: All three commands succeed. `dist/index.html` exists.

### Phase 3: Backend Skeleton

- [x] 15. **Create WeaveFleet.Domain project**
  **What**: Minimal domain project with just enough types for stub endpoints
  **Files**:
  ```
  src/WeaveFleet.Domain/WeaveFleet.Domain.csproj
  src/WeaveFleet.Domain/Common/Result.cs
  src/WeaveFleet.Domain/Common/Error.cs
  src/WeaveFleet.Domain/Common/Unit.cs
  ```
  **csproj**: Class library, no additional NuGet references
  **Result.cs**: The `Result<T>`, `Unit`, and `Error` types from dotnet-rewrite-plan §2.3
  **Acceptance**: `dotnet build src/WeaveFleet.Domain` succeeds

- [x] 16. **Create WeaveFleet.Application project**
  **What**: Minimal application layer with configuration types
  **Files**:
  ```
  src/WeaveFleet.Application/WeaveFleet.Application.csproj
  src/WeaveFleet.Application/Configuration/FleetOptions.cs
  ```
  **csproj**: Reference WeaveFleet.Domain, add `Microsoft.Extensions.Options` (from framework)
  **FleetOptions.cs**: Port, Host, DatabasePath, Debug settings (from dotnet-rewrite-plan §1.6)
  **Acceptance**: `dotnet build src/WeaveFleet.Application` succeeds

- [x] 17. **Create WeaveFleet.Infrastructure project**
  **What**: Minimal infrastructure project (placeholder for future DB, messaging, etc.)
  **Files**:
  ```
  src/WeaveFleet.Infrastructure/WeaveFleet.Infrastructure.csproj
  src/WeaveFleet.Infrastructure/DependencyInjection.cs
  ```
  **csproj**: Reference WeaveFleet.Application. Add EF Core SQLite for future use.
  **DependencyInjection.cs**: `AddFleetInfrastructure()` extension method (empty for now)
  **Acceptance**: `dotnet build src/WeaveFleet.Infrastructure` succeeds

- [x] 18. **Create WeaveFleet.Api project — the main deliverable**
  **What**: ASP.NET Core Minimal API project that serves the SPA and provides stub endpoints
  **Files**:
  ```
  src/WeaveFleet.Api/WeaveFleet.Api.csproj
  src/WeaveFleet.Api/Program.cs
  src/WeaveFleet.Api/appsettings.json
  src/WeaveFleet.Api/appsettings.Development.json
  ```

  **WeaveFleet.Api.csproj**:
  - Web SDK (`Microsoft.NET.Sdk.Web`)
  - Reference WeaveFleet.Infrastructure
  - Reference WeaveFleet.Application
  - No additional NuGet packages needed for bootstrap (SignalR, health checks, static files are all in-box)

  **Program.cs** — the heart of the bootstrap:
  ```csharp
  var builder = WebApplication.CreateBuilder(args);
  
  // Configure services
  builder.Services.AddHealthChecks();
  builder.Services.AddCors(options =>
  {
      options.AddDefaultPolicy(policy =>
      {
          policy.AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader();
      });
  });
  
  var app = builder.Build();
  
  // Middleware pipeline
  app.UseCors();
  
  // Health checks
  app.MapHealthChecks("/healthz");
  app.MapHealthChecks("/readyz");
  
  // API stub endpoints
  app.MapFleetEndpoints();
  
  // Static file serving (SPA)
  app.UseDefaultFiles();     // Serves index.html for "/"
  app.UseStaticFiles();      // Serves files from wwwroot/
  
  // SPA fallback — any unmatched route serves index.html
  app.MapFallbackToFile("index.html");
  
  await app.RunAsync();
  ```

  **appsettings.json**:
  ```json
  {
    "Fleet": {
      "Port": 3000,
      "Host": "127.0.0.1"
    },
    "Urls": "http://127.0.0.1:3000",
    "Logging": {
      "LogLevel": {
        "Default": "Information"
      }
    }
  }
  ```

  **appsettings.Development.json**:
  ```json
  {
    "Logging": {
      "LogLevel": {
        "Default": "Debug"
      }
    }
  }
  ```
  **Acceptance**: `dotnet run --project src/WeaveFleet.Api` starts Kestrel on port 3000

- [x] 19. **Create stub API endpoints**
  **What**: Minimal stub endpoints that return empty/mock data so the frontend doesn't error on load
  **Files**:
  ```
  src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs
  src/WeaveFleet.Api/Endpoints/InstanceEndpoints.cs
  src/WeaveFleet.Api/Endpoints/FleetEndpoints.cs
  src/WeaveFleet.Api/Endpoints/ConfigEndpoints.cs
  src/WeaveFleet.Api/Endpoints/DirectoryEndpoints.cs
  src/WeaveFleet.Api/Endpoints/HarnessEndpoints.cs
  src/WeaveFleet.Api/Endpoints/WorkspaceRootEndpoints.cs
  src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs
  ```

  **Stubs to implement** (minimum for frontend to load without errors):
  ```
  GET  /api/sessions              → 200 [] (empty array)
                                    Headers: X-Total-Count: 0, X-Limit: 100, X-Offset: 0
  GET  /api/sessions/{id}         → 404 { error: "Not found" }
  POST /api/sessions              → 501 { error: "Not implemented" }
  GET  /api/fleet/summary         → 200 { activeSessions: 0, idleSessions: 0, totalTokens: 0, totalCost: 0, queuedTasks: 0 }
  GET  /api/config                → 200 {} (empty config)
  GET  /api/version               → 200 { version: "0.1.0-dev", commit: "bootstrap" }
  GET  /api/profile               → 200 { profile: "default" }
  GET  /api/directories           → 200 { entries: [], currentPath: "/", parentPath: null, roots: [] }
  GET  /api/harnesses             → 200 [] (empty array)
  GET  /api/workspace-roots       → 200 { roots: [] }
  GET  /api/repositories          → 200 { repositories: [], scannedAt: 0 }
  GET  /api/integrations          → 200 { "integrations": [] }
  GET  /api/skills                → 200 [] (empty array)
  GET  /api/available-tools       → 200 [] (empty array)
  ```

  **EndpointExtensions.cs**: `MapFleetEndpoints()` extension method that calls all `Map*Endpoints()` methods.

  **Acceptance**: All stub endpoints return valid JSON matching the frontend's expected shapes (from `src/lib/api-types.ts`)

- [x] 20. **Create minimal WebSocket stub at `/ws`**
  **What**: Minimal WebSocket endpoint that accepts connections and responds to subscribe/unsubscribe messages (matching the protocol in `use-weave-socket.ts`)
  **Files**: `src/WeaveFleet.Api/Endpoints/WebSocketEndpoints.cs`
  **Protocol to implement**:
  ```
  Client → Server: { "type": "subscribe", "topics": ["session:abc", "activity"] }
  Server → Client: { "type": "subscribed", "topics": ["session:abc", "activity"] }
  
  Client → Server: { "type": "unsubscribe", "topics": ["session:abc"] }
  (no response)
  ```
  Use raw `WebSocket` middleware (not SignalR yet) to match the existing protocol the frontend expects. SignalR will replace this later but requires frontend changes.
  **Acceptance**: Frontend's `useWeaveSocket` hook connects without errors, receives `subscribed` acknowledgments

- [x] 21. **Create WeaveFleet.Cli project (placeholder)**
  **What**: Minimal CLI project that delegates to the API host
  **Files**:
  ```
  src/WeaveFleet.Cli/WeaveFleet.Cli.csproj
  src/WeaveFleet.Cli/Program.cs
  ```
  **Program.cs**: Just a placeholder that prints version info. Full CLI implementation comes later (dotnet-rewrite-plan §1.11).
  **Acceptance**: `dotnet run --project src/WeaveFleet.Cli` prints "Weave Fleet v0.1.0-dev"

- [x] 22. **Create test project skeletons**
  **What**: Empty test projects with correct references
  **Files**:
  ```
  tests/WeaveFleet.Domain.Tests/WeaveFleet.Domain.Tests.csproj
  tests/WeaveFleet.Application.Tests/WeaveFleet.Application.Tests.csproj
  tests/WeaveFleet.Infrastructure.Tests/WeaveFleet.Infrastructure.Tests.csproj
  tests/WeaveFleet.Api.Tests/WeaveFleet.Api.Tests.csproj
  ```
  Each test project:
  - References its corresponding src project
  - Uses xunit + Microsoft.NET.Test.Sdk
  - Has at least one `[Fact]` test (e.g., `Assert.True(true)`) so `dotnet test` has something to run
  **Acceptance**: `dotnet test` discovers and runs ≥4 tests, all pass

### Phase 4: SPA Serving Pipeline

- [x] 23. **Configure static file serving from `client/dist/`**
  **What**: The .NET backend's `wwwroot/` must contain the frontend build output. Two approaches — choose MSBuild integration:
  **Files**: `C:\source\weave-fleet\src\WeaveFleet.Api\WeaveFleet.Api.csproj`, `C:\source\weave-fleet\src\WeaveFleet.Api\.gitignore`
  
  **Approach: MSBuild `<Target>` that copies `client/dist/` → `wwwroot/` on build**
  
  Add to `src/WeaveFleet.Api/WeaveFleet.Api.csproj`:
  ```xml
  <PropertyGroup>
    <!-- Tell ASP.NET Core where to find static files -->
    <StaticWebAssetBasePath>/</StaticWebAssetBasePath>
  </PropertyGroup>
  
  <!-- Copy frontend build output to wwwroot before build -->
  <Target Name="CopyFrontendDist" BeforeTargets="Build" Condition="Exists('$(MSBuildThisFileDirectory)..\..\client\dist\index.html')">
    <ItemGroup>
      <FrontendFiles Include="$(MSBuildThisFileDirectory)..\..\client\dist\**\*" />
    </ItemGroup>
    <Copy SourceFiles="@(FrontendFiles)" 
          DestinationFolder="$(MSBuildThisFileDirectory)wwwroot\%(RecursiveDir)" 
          SkipUnchangedFiles="true" />
  </Target>
  ```
  
  The `Condition` means: if `client/dist/` doesn't exist, skip the copy (backend still builds, just without frontend). This allows backend-only development.
  
  Also add `wwwroot/` to `.gitignore` in `src/WeaveFleet.Api/` since it's a build artifact.
  **Acceptance**: After `cd client && npm run build:spa`, then `dotnet run --project src/WeaveFleet.Api`, navigating to `http://localhost:3000/` shows the Weave Fleet UI

- [x] 24. **Configure SPA fallback routing**
  **What**: Non-API routes must serve `index.html` for client-side routing. The frontend has routes like `/sessions/[id]`, `/pipelines`, `/queue`, `/templates`, `/settings`, `/welcome`, `/github/[owner]/[repo]`, `/repositories`, `/integrations`.
  **Files**: `src/WeaveFleet.Api/Program.cs`
  **Implementation**: `app.MapFallbackToFile("index.html")` handles this — any request not matched by an API endpoint or static file serves `index.html`. The Next.js client-side router then handles the route.
  **Key detail**: API routes under `/api/*` must be registered BEFORE the fallback, and health check routes (`/healthz`, `/readyz`) must also be registered before the fallback.
  **Acceptance**: Navigating to `http://localhost:3000/sessions/test-id` in the browser serves the SPA, which then renders the session page client-side

- [x] 25. **Create production publish profile**
  **What**: `dotnet publish` should produce a self-contained artifact with the frontend baked in
  **Files**: `src/WeaveFleet.Api/Properties/PublishProfiles/Production.pubxml` (or just use CLI args)
  **Command**:
  ```bash
  cd client && npm run build:spa
  dotnet publish src/WeaveFleet.Api -c Release -o publish/
  ```
  The publish output should contain `wwwroot/` with the frontend files.
  **Acceptance**: Running `publish/WeaveFleet.Api.exe` (or `dotnet publish/WeaveFleet.Api.dll`) serves the full app

### Phase 5: Dev Experience

- [x] 26. **Create dev workflow documentation**
  **What**: Document how to run both frontend and backend in development
  **Files**: `C:\source\weave-fleet\src\WeaveFleet.Api\Program.cs` (add dev workflow comments at top of file)
  
  **Two dev modes**:
  
  **Mode A: Integrated (backend serves built SPA)**
  ```bash
  # Terminal 1: Build frontend once
  cd client && npm run build:spa
  
  # Terminal 2: Run backend (serves SPA + API)
  dotnet run --project src/WeaveFleet.Api
  # → http://localhost:3000
  ```
  - No hot reload on frontend changes (must rebuild)
  - Good for testing production-like behavior
  
  **Mode B: Split (frontend dev server + backend API server)**
  ```bash
  # Terminal 1: Run backend API server
  dotnet run --project src/WeaveFleet.Api
  # → http://localhost:3000 (API only, no SPA)
  
  # Terminal 2: Run frontend dev server pointing at backend
  cd client && npm run dev:split
  # → http://localhost:3001 (SPA with hot reload, API calls proxied to :3000)
  ```
  - Full hot reload on frontend changes
  - Frontend at :3001, backend at :3000
  - `NEXT_PUBLIC_API_BASE_URL=http://localhost:3000` makes all API/WS calls go to the backend
  
  **Acceptance**: Both modes work end-to-end

- [x] 27. **Configure CORS for split-mode development**
  **What**: Backend must allow cross-origin requests from `localhost:3001` (frontend dev server)
  **Files**: `src/WeaveFleet.Api/Program.cs` (already has CORS in task 18, but verify it's permissive enough)
  **Requirements**:
  - Allow `http://localhost:3001` origin
  - Allow WebSocket upgrade from different origin
  - In development, use `AllowAnyOrigin()` for simplicity
  - In production, restrict to same-origin only
  **Acceptance**: Frontend at :3001 can fetch `/api/sessions` from :3000 without CORS errors

- [x] 28. **Add `dotnet watch` support**
  **What**: Ensure `dotnet watch run --project src/WeaveFleet.Api` provides hot reload for backend changes
  **Files**: `src/WeaveFleet.Api/Properties/launchSettings.json`
  ```json
  {
    "profiles": {
      "WeaveFleet.Api": {
        "commandName": "Project",
        "dotnetRunMessages": true,
        "launchBrowser": false,
        "applicationUrl": "http://localhost:3000",
        "environmentVariables": {
          "ASPNETCORE_ENVIRONMENT": "Development"
        }
      }
    }
  }
  ```
  **Acceptance**: `dotnet watch run --project src/WeaveFleet.Api` restarts on C# file changes

- [x] 29. **Verify the WebSocket connection in split mode**
  **What**: Ensure the frontend's WebSocket connection works when frontend is at :3001 and backend at :3000
  **Files**: None (verification task — no file changes needed)
  **Note**: `wsUrl("/ws")` with `NEXT_PUBLIC_API_BASE_URL=http://localhost:3000` produces `ws://localhost:3000/ws`
  **Verification**: Open browser console at :3001, check that WebSocket connection to `ws://localhost:3000/ws` succeeds
  **Acceptance**: No WebSocket connection errors in browser console

## Verification

- [x] `cd client && npm install && npm run build:spa` succeeds
- [x] `cd client && npm run typecheck` passes with 0 errors
- [x] `dotnet build` (from repo root) succeeds with 0 warnings
- [x] `dotnet test` (from repo root) runs ≥4 tests, all pass
- [x] `dotnet run --project src/WeaveFleet.Api` starts on port 3000
- [x] `GET http://localhost:3000/healthz` returns 200
- [x] `GET http://localhost:3000/readyz` returns 200
- [x] `GET http://localhost:3000/api/sessions` returns `[]`
- [x] `GET http://localhost:3000/api/fleet/summary` returns valid JSON
- [x] `GET http://localhost:3000/` serves the Weave Fleet SPA
- [x] `GET http://localhost:3000/sessions/test` serves `index.html` (SPA fallback)
- [ ] `ws://localhost:3000/ws` accepts WebSocket connection
- [ ] Split-mode dev: frontend at :3001 can call API at :3000

## Appendix: Files NOT Copied from weave-agent-fleet

These files exist in `src/app/api/` and `src/lib/server/` — they are Node.js server-side implementations that the .NET backend replaces. They are included in the copy (task 8) because they exist alongside client code in `src/`, but they are **inert** in SPA mode because:
1. `build-spa.mjs` renames `src/app/api/` → `src/app/_api/` during static export build
2. `src/lib/server/` files are never imported by client components (they're server-only)

Keeping them in the repo preserves the API contract as reference documentation. They can be removed in a follow-up cleanup once the .NET endpoints fully implement the contract.

**Server-only files (inert in SPA mode, kept as reference):**
- `src/app/api/**/*.ts` — 48 API route handlers
- `src/lib/server/**/*.ts` — 25 server-side modules (process-manager, db, workspace-manager, etc.)
- `src/cli/` — CLI entry point (replaced by WeaveFleet.Cli)
- `src/proxy.ts` — Node.js proxy server
- `src/instrumentation.ts` — Next.js instrumentation hook (server-only)

## Appendix: Dependency on `@opencode-ai/sdk`

The frontend imports `@opencode-ai/sdk` in several hooks for type definitions (session types, event types). In SPA mode, these imports are fine — the SDK types are used for TypeScript inference only. The actual SDK client calls happen in `src/lib/server/opencode-client.ts` (server-side, not bundled in SPA mode).

If `@opencode-ai/sdk` causes issues during static build (e.g., Node.js-only imports), it may need to be moved to `devDependencies` or stubbed. Monitor the `build:spa` output for errors.

## Appendix: Future SignalR Migration

The current frontend uses raw WebSocket at `/ws` with a JSON text protocol. The .NET bootstrap uses a raw WebSocket stub to match this protocol. Later phases (from dotnet-rewrite-plan §3.6-3.7) will:

1. Add SignalR hub at `/ws` (or `/hub`)
2. Update the frontend's `use-weave-socket.ts` to use `@microsoft/signalr` client
3. The SignalR client handles reconnection, automatic transport negotiation (WebSocket → SSE → long-polling), and group management natively

This is NOT part of the bootstrap — it's a Phase 3+ concern from the dotnet-rewrite-plan.
