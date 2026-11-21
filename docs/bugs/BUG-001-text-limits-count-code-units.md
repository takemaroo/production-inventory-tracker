# BUG-001: Text length limits count UTF-16 code units, so an emoji counts as two characters

- **Severity:** minor
- **Status:** closed (fixed, re-tested 2026-11-21, round 1)
- **Criterion affected:** AC-06.1 (unit name 1 to 50 characters); also every "1 to 500 characters" reason or note (AC-09.1, AC-12.1, AC-16.1, AC-19.1, AC-21.1, AC-22.1) and the item name and description limits of design 2.6
- **Found:** 2026-11-21, test round 0 (exploratory pass)

## Steps to reproduce

1. Start the app in Development with an empty database and sign in as the first Manager.
2. `POST /api/v1/units` with `{ "name": "🧀🧀🧀🧀🧀🧀🧀🧀🧀🧀🧀🧀🧀🧀🧀🧀🧀🧀🧀🧀🧀🧀🧀🧀🧀🧀" }` (26 cheese emoji, U+1F9C0).
3. Create a raw material, receive 10 of it, then `POST /api/v1/adjustments` with `{ "itemId": <id>, "location": "Warehouse", "quantity": -1, "reason": "<260 cheese emoji>" }`.

## Expected

Both are accepted (`201`). A unit name may have 1 to 50 characters (AC-06.1) and a reason 1 to 500 characters (AC-12.1); 26 and 260 characters are within those limits. Design 2.6 states the same limits in characters.

## Actual

Both are refused with `400` `validation`:

```json
{ "title": "Invalid request", "status": 400, "detail": "Name must be 50 characters or fewer.", "code": "validation",
  "errors": { "name": ["Name must be 50 characters or fewer."] } }
```

and `"detail": "Reason must be 500 characters or fewer."` for the adjustment. The limit check uses the string's UTF-16 length: every character outside the Basic Multilingual Plane (emoji, some CJK and historic scripts) counts as two. 25 emoji are accepted as a unit name, 26 are not; 250 emoji are accepted as a reason, 251 are not. Accented Latin letters (á, ñ, é typed as one character) are counted correctly.

## Notes

- Happens every time. Nothing is saved wrongly; a user only loses part of the allowance, and gets a message that says the text is too long when it is not.
- Workaround: shorter text.
- Automated test: `tests/InventoryTracker.Api.Tests/Acceptance/TextLengthAcceptanceTests.cs` (`AC_06_1_BUG_001_unit_name_of_26_emoji_is_accepted`, `AC_12_1_BUG_001_reason_of_260_emoji_is_accepted`). While the bug was open they were skipped; run without the `Skip` on 2026-11-21 (round 0) they failed with `400` instead of `201`. They now run un-skipped and guard the fix.
- Before the fix, the open question was one meaning of "character" and record it in `docs/decisions.md`: Unicode code points (`string.EnumerateRunes().Count()`, which matches how PostgreSQL's `varchar(n)` counts, relevant to the Later list) or characters as a person sees them (`StringInfo.LengthInTextElements`, where "é" typed as e + combining accent counts as one). The emoji in the tests count as one under both.

## Fix

Decision #116: every text length limit (names, descriptions, reasons, notes, email) counts Unicode code points (C# `Rune`s) through one shared helper, `Common/Validation/Text.Length`, used by every service that checks a limit and by the stock ledger's own re-check. An emoji counts as one character. A letter typed as e + combining accent counts as two (two code points); that is the chosen definition, not a defect.

Related, found by `code-reviewer` while reviewing the fix: the password minimum of 8 characters (PRD section 5) also counted UTF-16 code units, so "Aa1🧀🧀🧀" (6 characters, 9 code units) was accepted. Decision #118 fixed it with a custom Identity validator, `Auth/PasswordLengthValidator`, that counts code points through the same helper. The message and Identity's other password rules are unchanged.

## Re-test (2026-11-21, round 1)

- The two acceptance tests above, un-skipped: 2 of 2 passed.
- New developer tests, read and run: `Integration/TextLengthLimitTests` (every call site at its limit in emoji, and one more refused), `Unit/Common/TextLengthTests`, `Integration/PasswordLengthValidatorTests`, plus emoji cases added to `Integration/UsersTests` and `Integration/StartupTests`.
- Exploratory, on the running app (Development, throwaway environment variables, a scratchpad database):
  - Unit name of 26 emoji: `201`. 50 emoji: `201`. 51 characters: `400` "Name must be 50 characters or fewer."
  - Adjustment reason of 250, 251 and 500 emoji: `201`, stored exactly as sent. 501 emoji: `400` "Reason must be 500 characters or fewer."
  - 25 × (e + combining accent) as a unit name, 50 code points: `201`. 26 of them, 52 code points: `400`, as decision #116 defines.
  - #118: `POST /users` with password "Aa1🧀🧀🧀" (6 characters) and "Aa1🧀🧀🧀🧀" (7): `400` `weak-password`. "Aa1🧀🧀🧀🧀🧀" (8): `201`, and sign-in with it succeeds. "Short1a" was refused and "Abcdefg1" accepted, as before the change. Missing a digit ("aaa🧀🧀🧀🧀🧀") or a lower-case letter ("AAA1🧀🧀🧀🧀"): still `400`. Changing one's own password and a Manager's reset both refuse the 6-character emoji password and accept the 8-character one (`204`, then sign-in `200`).
- Full suite: `dotnet test --artifacts-path TestResults/qa-art`: 1,695 total, 1,695 passed, 0 failed, 0 skipped.

## History

- 2026-11-21 opened by qa-engineer
- 2026-11-21 fixed by backend-developer (decisions #116, #118), approved by code-reviewer; not yet committed
- 2026-11-21 re-tested by qa-engineer: closed
