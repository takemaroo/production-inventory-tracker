# Architecture: [product name]

Based on `docs/01-prd.md` and `docs/02-design.md`.

## 1. Stack

| Layer | Choice | Version | Why |
|-------|--------|---------|-----|
| Back end | | | |
| Front end | | | |
| Database | | | |
| Tests | | | |

Departures from the owner's preferred stack, and the reason: [or "none"]

## 2. Project structure

```
[folder tree, with a few words on what belongs in each folder]
```

## 3. Data model

### [Entity]

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| | | | |

Relationships: [...]

## 4. Interface contract

Developers on both sides build against this section. Change it here before changing code.

### [METHOD] [path]

- **Purpose:** [and the user stories served]
- **Auth:** [who may call it]
- **Request:** [fields, types, validation rules]
- **Response [status]:** [shape]
- **Errors:** [status and body for each failure case]

Standard error shape: [...]

## 5. Cross-cutting decisions

- **Authentication and authorisation:**
- **Validation:**
- **Error handling:**
- **Logging:**
- **Configuration:** [environment variable names]

## 6. Testing strategy

- Unit and integration tests: [framework, location]
- Acceptance tests: [framework, location]
- Run everything: `[one command]`

## 7. Running locally

```
[exact commands: install, set up the database, run, test]
```

Prerequisites: [tools and versions, confirmed present on this machine]

## 8. Risks and open points

[What might not work as planned, and what was decided with low confidence.]
