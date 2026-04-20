# E2E data-testid Audit

## Summary
- Total data-testid values expected by E2E tests: 40
- Already present in Vue components: 0
- Missing (need to be added): 40

## Missing data-testid Attributes

| data-testid value | E2E Page Object / Test | Vue Component to add it to |
|---|---|---|
| summary-bar | FleetDashboardPage.cs | client/src/components/board/KanbanBoard.vue |
| summary-{label}-count | FleetDashboardPage.cs | client/src/components/board/KanbanBoard.vue |
| new-session-button | FleetDashboardPage.cs; ErrorHandlingTests.cs | client/src/components/sessions/SessionsPanel.vue |
| empty-state | FleetDashboardPage.cs; FleetDashboardTests.cs | client/src/components/sessions/SessionsPanel.vue |
| session-card | FleetDashboardPage.cs; UiResponsivenessBenchmarkTests.cs; SubAgentDelegationTests.cs | client/src/components/sessions/SessionItem.vue |
| session-status-indicator | FleetDashboardPage.cs; SessionDetailPage.cs | client/src/components/sessions/SessionItem.vue; client/src/components/session/SessionDetailPanel.vue |
| session-title | FleetDashboardPage.cs; SubAgentDelegationTests.cs | client/src/components/sessions/SessionItem.vue |
| session-delete-button | FleetDashboardPage.cs; SessionDetailPage.cs | client/src/components/sessions/SessionItem.vue; client/src/components/session/SessionDetailPanel.vue |
| session-terminate-button | FleetDashboardPage.cs | client/src/components/sessions/SessionItem.vue |
| session-archive-button | FleetDashboardPage.cs | client/src/components/sessions/SessionItem.vue |
| session-unarchive-button | FleetDashboardPage.cs; SessionDetailPage.cs | client/src/components/sessions/SessionItem.vue; client/src/components/session/SessionDetailPanel.vue |
| session-card-archived-badge | FleetDashboardPage.cs | client/src/components/sessions/SessionItem.vue |
| retention-filter-trigger | FleetDashboardPage.cs | client/src/components/sessions/SessionsPanel.vue |
| retention-filter-option-active | FleetDashboardPage.cs; SessionLifecycleTests.cs | client/src/components/sessions/SessionsPanel.vue |
| retention-filter-option-archived | FleetDashboardPage.cs; SessionLifecycleTests.cs | client/src/components/sessions/SessionsPanel.vue |
| retention-filter-option-all | FleetDashboardPage.cs; SessionLifecycleTests.cs | client/src/components/sessions/SessionsPanel.vue |
| new-session-dialog | NewSessionDialog.cs; SessionLifecycleTests.cs | client/src/components/sessions/NewSessionDialog.vue |
| create-session-submit | NewSessionDialog.cs; ErrorHandlingTests.cs | client/src/components/sessions/NewSessionDialog.vue |
| activity-stream | SessionDetailPage.cs; UiResponsivenessBenchmarkTests.cs; MessagePersistenceTests.cs | client/src/components/session/ActivityStream.vue |
| prompt-input | SessionDetailPage.cs; UiResponsivenessBenchmarkTests.cs; SessionLifecycleTests.cs; ErrorHandlingTests.cs | client/src/components/session/Composer.vue |
| prompt-send-button | SessionDetailPage.cs | client/src/components/session/Composer.vue |
| abort-button | SessionDetailPage.cs | client/src/components/session/SessionDetailPanel.vue |
| message-item | SessionDetailPage.cs | client/src/components/session/MessageBubble.vue |
| message-sender-name | SessionDetailPage.cs; MessagePersistenceTests.cs | client/src/components/session/MessageBubble.vue |
| session-archived-banner | SessionDetailPage.cs; SessionLifecycleTests.cs | client/src/components/session/SessionDetailPanel.vue |
| session-archived-badge | SessionDetailPage.cs; SessionLifecycleTests.cs | client/src/components/session/SessionDetailPanel.vue; client/src/components/sessions/SessionItem.vue |
| session-unarchive-banner-button | SessionDetailPage.cs | client/src/components/session/SessionDetailPanel.vue |
| session-archive-banner-button | SessionDetailPage.cs; SessionLifecycleTests.cs | client/src/components/session/SessionDetailPanel.vue |
| session-stopped-banner | SessionDetailPage.cs | client/src/components/session/SessionDetailPanel.vue |
| session-stop-button | SessionDetailPage.cs | client/src/components/session/SessionDetailPanel.vue |
| session-stop-confirm-button | SessionDetailPage.cs | client/src/components/session/SessionDetailPanel.vue |
| session-resume-button | SessionDetailPage.cs | client/src/components/session/SessionDetailPanel.vue |
| session-archived-fork-button | SessionDetailPage.cs | client/src/components/session/SessionDetailPanel.vue |
| delete-dialog-confirm | SessionDetailPage.cs; SessionLifecycleTests.cs | client/src/components/sessions/ConfirmDeleteSessionDialog.vue |
| delete-dialog-cancel | SessionLifecycleTests.cs | client/src/components/sessions/ConfirmDeleteSessionDialog.vue |
| fork-session-dialog | SessionDetailPage.cs | client/src/components/session/SessionDetailPanel.vue |
| fork-session-dialog-title | SessionDetailPage.cs | client/src/components/session/SessionDetailPanel.vue |
| fork-session-source-title | SessionDetailPage.cs | client/src/components/session/SessionDetailPanel.vue |
| fork-session-submit | SessionDetailPage.cs | client/src/components/session/SessionDetailPanel.vue |
| fork-session-title-input | SessionDetailPage.cs | client/src/components/session/SessionDetailPanel.vue |

## Already Present

No matching `data-testid` attributes were found anywhere under `client/src/`.
