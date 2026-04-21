# Learnings: Port `@` Mentions and `/` Slash Command Autocomplete into Composer

## Task 2: Wire autocomplete into `Composer.vue`
- **Discrepancy**: The task acceptance listed keyboard delegation and popup rendering details, but the plan's broader definition of done also required no popup when `instanceId` is absent/empty.
- **Resolution**: Updated `Composer.vue` to gate popup rendering and autocomplete key interception behind a trimmed `instanceId` check.
- **Suggestion**: Add the `instanceId` disabled-state requirement directly to Task 2 acceptance so implementation and verification criteria stay aligned.
