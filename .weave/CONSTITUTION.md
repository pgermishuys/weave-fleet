# Weave Fleet — Constitution

> This document is the foundation for all design and implementation decisions.
> If a choice contradicts this constitution, the constitution wins.

## Purpose

Weave Fleet is an AI session orchestrator. It manages the lifecycle of many concurrent AI agent sessions across projects, teams, and domains. It is the control plane between people and AI agents — handling spawning, monitoring, messaging, and coordination so users can focus on their work, not on managing infrastructure.

## Principles

### 1. Sessions are the universal primitive
Everything in Weave Fleet revolves around sessions. A session is a conversation between a human and an AI agent. Sessions are created, prompted, observed, paused, resumed, and completed. The system exists to manage sessions well.

### 2. Projects are the organizing unit
A project is a container for sessions. It is a human concept — not tied to a directory, repository, or any filesystem artifact. Projects group related work: "Auth Rewrite", "Q4 Campaign", "Financial Analysis". Sessions without an explicit project go into an implicit "Scratch" project. Users think in projects; the system respects that.

### 3. Harnesses define the experience
A harness is the bridge between Weave Fleet and an AI agent. Each harness declares its capabilities — what it can do, how it communicates, what UI affordances it supports. The frontend adapts to the harness, not the other way around. Weave Fleet is harness-agnostic: OpenCode today, a direct OpenAI API wrapper tomorrow, a custom domain-specific agent next week. We may build our own harnesses that interact directly with provider APIs.

### 4. Local-first, cloud-ready
A single command starts a fully functional system on your machine. No accounts, no configuration, no internet required. The architecture supports multi-tenancy, authentication, and remote storage — but these are progressive enhancements, off by default. Local and cloud are the same codebase, the same binary, different configuration.

### 5. Authentication is a first-class citizen
Auth is not bolted on. The system is designed with auth boundaries from day one. Locally, auth is bypassed transparently. In cloud deployment, auth is enabled via configuration. There is no "add auth later" refactor.

### 6. Invisible infrastructure
The technical machinery — process management, health checks, event streaming, storage — should be invisible to the user. Zero-config defaults. It just works. Complexity is available for those who seek it, hidden from those who don't.

### 7. UI/UX is not a layer on top — it's the product
The backend exists to serve a polished user experience. API design starts from "what does the frontend need?" not "what does the backend expose?" Every capability ships with a considered interface. The interface should feel polished and intentional.

### 8. Data is a first-class output
Every session interaction generates data — tokens, costs, durations, prompts, responses, tool usage, success/failure patterns. Weave Fleet captures everything. This data enables analytics, cost management, usage insights, and future agent fine-tuning. We collect generously, categorize meaningfully, and make it queryable. Data should be categorizable by project, team, harness, domain, time period, and custom dimensions.

## Audience (Priority Order)

1. **Solo practitioners** — developers, analysts, writers, researchers who want to run multiple AI sessions without friction
2. **Teams** — small groups who share visibility into AI sessions across projects and domains
3. **Organizations** — departments (engineering, finance, marketing) using AI agents for domain-specific work

## Domain Model

```
Project (organizational container)
  └── Session (conversation with an AI agent)
       └── Message → MessagePart (the actual content)

Instance (running harness process — invisible to users)
Workspace (directory/isolation — invisible to users)
```

- A **Project** is a pure container. Not tied to a directory, repo, or filesystem. Groups related sessions.
- A **Session** belongs to a Project. Runs on an Instance. This is the core interaction unit.
- An **Instance** is a running harness process. Users never manage these directly.
- A **Workspace** provides filesystem isolation. An implementation detail.
- **Scratch** is the implicit project for sessions created without a project.

## Architecture Tenets

- **Clean separation of concerns** — Domain → Application → Infrastructure → API. No layer reaches down.
- **Harness capability model** — Harnesses declare what they can do. The system and UI adapt. No feature flags, no special cases for specific harnesses.
- **Runtime agnosticism** — Support different runtimes (local processes today, containers or remote machines tomorrow). The harness doesn't care where it runs.
- **Async all the way** — Every I/O operation is async with CancellationToken propagation. No blocking calls.
- **Storage abstraction** — Repository interfaces in Application layer. SQLite for local, swappable for cloud (Postgres, etc.) without touching business logic.
- **Auth abstraction** — Authentication/authorization interfaces defined early. Null implementation for local. Real implementations for cloud.
- **Real-time by default** — Session events stream to clients as they happen. SignalR with topic-based subscriptions. No polling.
- **Observable from day one** — Structured logging, metrics, and traces built in. Not added later.
- **Data capture by default** — Every meaningful interaction is recorded with categorization metadata. The data model supports arbitrary dimensions for future analysis.

## Data Philosophy

Data collection serves three purposes:

1. **Operational** — token usage, cost tracking, session health, system performance
2. **Behavioral** — prompt patterns, tool usage, success/failure rates, session duration, outcomes
3. **Audit** — who prompted what, when, in which project (essential for teams and cloud)

All data should be:
- **Categorizable** — by project, user, team, harness, domain, time period, and custom tags
- **Queryable** — APIs and UI for slicing data across any dimension
- **Exportable** — data belongs to the user, not the system

## What We Don't Do

- **We don't build AI agents.** We orchestrate them. The intelligence lives in the harness, not in Fleet.
- **We don't force a specific AI provider.** Any agent that speaks the harness interface can participate.
- **We don't require the cloud.** Local is a full experience, not a degraded one.
- **We don't sacrifice UX for architecture.** If a "clean" abstraction makes the user experience worse, the abstraction is wrong.
- **We don't discard data.** If an interaction happened, we have a record of it.
