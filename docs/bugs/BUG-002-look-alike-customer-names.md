# BUG-002: The same customer name typed in two Unicode forms is accepted as two customers

- **Severity:** minor
- **Status:** closed (fixed, re-tested 2026-11-25, round 1)
- **Criterion affected:** AC-29.2 (customer name unique without regard to case); through it US-29's purpose ("every sales order names its customer the same way and sales can be totalled per customer") and AC-33.1 (one row per customer). The same behaviour exists in version 1 for unit names (AC-06.2) and item names (AC-07.1).
- **Found:** 2026-11-25, v1.1 test round 0 (exploratory pass)

## Steps to reproduce

1. Start the app with an empty database and sign in as a Seller (or Manager).
2. `POST /api/v1/customers` with `{ "name": "Café Sur" }`: "é" as one character, U+00E9, as a Windows keyboard types it.
3. `POST /api/v1/customers` with `{ "name": "Café Sur" }`: "e" followed by U+0301 COMBINING ACUTE ACCENT, as text pasted from macOS, a PDF or some web forms often arrives. On screen both read "Café Sur".
4. Put a sales order for each customer through to Shipped, then `GET /api/v1/reports/shipped-by-customer`.

## Expected

Step 3 is refused with `409` `customer-name-taken` "A customer named 'Café Sur' already exists.", as `"CAFÉ SUR"` (step 2's name in upper case) already is. To the person typing, it is the name another customer already has (AC-29.2).

## Actual

Both are created (`201`, ids 66 and 67 in the exploratory run). The customer list then holds two "Café Sur", a Seller cannot tell which to pick for a new sales order, and the per-customer report splits that customer's sales over two identical-looking rows:

```
report rows: [(67, 'Café Sur', 1.0), (66, 'Café Sur', 1.0)]
```

A name typed in upper case with the decomposed accent (`"CAFÉ SUR"`) is refused as a duplicate of the decomposed one only, so the check works on case but compares the code points as given, without Unicode normalisation.

## Notes

- Every time. Needs a client that sends decomposed text, which a Windows keyboard does not, so it is uncommon on the owner's machine today; more likely once names are pasted from other systems.
- No data is lost. Workaround: rename the duplicate to something distinct, or deactivate it. Its sales orders cannot be moved to the other customer (AC-24.7), so its shipped totals stay separate for good.
- Units and items (version 1) behave the same: `"m³ café"` / `"m³ café"` and `"Queso añejo"` / `"Queso añejo"` were each created twice.
- The PRD says "compared without regard to case" and does not mention Unicode forms, so this is a gap between the wording and the intent rather than a clear AC failure; hence minor. A likely fix is to normalise names to NFC (`string.Normalize()`) before trimming, checking and storing, in the one place names are normalised, but whether to do that (and for units and items too) is a scope choice for the delivery manager and product manager to record.
- Related, observed and not filed: a customer name made only of a zero-width space (U+200B) is accepted and stored, because it is a format character, not white space, so trimming leaves it. It shows as a blank name. Version 1 QA noted the same kind of input (tab, NUL) for item names and left it to the security review.
- Automated test: `tests/InventoryTracker.Api.Tests/Acceptance/CustomerNameAcceptanceTests.cs`, `AC_29_2_BUG_002_same_name_in_another_unicode_form_is_a_duplicate`. While the bug was open it was skipped; run without the `Skip` on 2026-11-25 (round 0) it failed with `Expected: Conflict`, `Actual: Created`. It now runs un-skipped and guards the fix.

## Fix

Decisions #170 to #174 (owner, Q82 to Q86, then the developer and the delivery manager after review). One shared rule, `Common/Validation/NameRules`, is used by every create and rename of units, items and customers:

- the name is converted to Unicode NFC, then trimmed, checked, compared and stored, so a look-alike of an existing name is refused with the list's name-taken message (#170); `NameNormalizer.Normalize` also applies NFC, so the database key of the two forms is the same;
- a name whose code points are all white space, control (Cc) or format (Cf) characters is `400` "Name is required."; such characters among visible ones are kept as sent (#171, #173);
- a name .NET cannot normalize (in practice U+FFFE) is `400` "Name contains a character that is not allowed.", checked first (#174);
- the length limit counts the NFC, trimmed text (#173);
- no data migration: names stored before the fix are left as they are (#172).

## Re-test (2026-11-25, round 1)

- The acceptance test above, un-skipped: passed. Its two accented literals are now written as `\u00E9` and `\u0301` escapes (the file is pure ASCII, checked by a byte scan), and its remarks no longer say it is skipped.
- Added by QA for a gap the developer's tests leave: `Acceptance/NameLengthAfterNfcAcceptanceTests` `Design_2_6_name_that_NFC_makes_longer_than_the_limit_is_refused` (units, items, customers). The developer's tests cover the direction where NFC shortens a name; this one covers a character NFC splits in two (U+0958 becomes U+0915 U+093C): at the limit after NFC `201`, stored in NFC; exactly the limit as sent but over it after NFC `400` on create and on rename, record unchanged. Passed.
- Developer tests, read and run in the full suite: `Integration/NameUnicodeTests` (look-alike create and rename, NFC storage, own-name rename in another form, invisible-only on create and rename, invisible characters kept, length after NFC, lone surrogate, U+FFFE on create and rename, for all three lists) and `Unit/Common/NameRulesTests`.
- Exploratory, on a throwaway instance (built into `TestResults/qa11-retest`, Development, port 5251, scratchpad database, `APPDATA`/`LOCALAPPDATA` in the scratchpad, throwaway secrets): a Python script of 287 checks, all passed. Every non-ASCII character was built with `chr()`; names were sent as JSON `\u` escapes and, for the look-alike and U+FFFE cases, also as raw UTF-8.
  - Steps 2 and 3 above, as a Seller: step 2 `201`; step 3 `409` `customer-name-taken` "A customer named 'Café Sur …' already exists." (quoted in NFC); the decomposed upper-case copy `409`; the customer list holds one "Café Sur". Step 4 no longer applies: there is only one customer to report on.
  - For units, items and customers alike, create: NFD look-alike of an NFC name `409` with the list's code and message; NFD in upper case, and NFD padded with NBSP and spaces, `409`; an NFD name created first is stored and read back in NFC, then its NFC form is `409`; singleton and composition look-alikes `409`: U+212B ANGSTROM SIGN vs "Å", conjoining Hangul jamo U+1100 U+1161 vs U+AC00, U+212A KELVIN SIGN + "elvin" vs "kelvin".
  - Rename: to an NFD look-alike of another record, plain and upper case, `409`; to the record's own name in NFD and in NFD upper case `200`, stored in NFC; to a new NFD name `200`, stored in NFC.
  - Invisible-only names, create and rename, 17 forms each: U+200B, spaces around three U+200B, U+FEFF, U+2060, U+00AD, U+200E + U+200F, U+200D, U+200C, tab, NUL, NUL + U+200B + tab, NBSP + U+200B + U+3000, U+180E, U+E0001 (a format character outside the BMP), U+061C, DEL, U+0085: all `400` "Name is required.".
  - U+FFFE, create and rename: alone, inside a name, between spaces, after U+200B, at the end of a name 20 over the limit (the not-allowed message wins), after an NFD name, and (on create) sent as raw UTF-8 bytes: all `400` "Name contains a character that is not allowed.". A lone surrogate `\ud800`: `400`.
  - Invisible characters among visible ones (ZWJ and ZWNJ between letters, a leading U+200B, soft hyphens, NUL inside, a leading U+200E): `201`, stored and read back exactly as sent; a rename with ZWJ inside `200`, kept.
  - Length after NFC: exactly the limit after NFC accepted and one over refused, for names made of e + U+0301 (2 code points sent, 1 stored), Hangul jamo pairs (2 to 1), U+0958 (1 to 2) and U+FB2C (1 to 3), on create, and the over-limit case also on rename; the limit in emoji `201`, one more `400`.
  - Race: 8 simultaneous creates of one new name in NFC, NFD, NFD upper and lower case, 6 times per list: always exactly one `201` and seven `409` with the list's name-taken code, never a `5xx`. The application log has no error or exception line.
- Observed, consistent with the decisions, not filed:
  - Decision #172 (no migration), simulated by inserting one decomposed customer row with its old-style key straight into the scratch database: the same decomposed name typed again is accepted once (`201`, stored in NFC, a look-alike of the old row); after that the NFC form and upper-case copies are `409`. So each name stored decomposed before the fix can gain one look-alike twin. The owner chose to leave stored names as they are, and Windows keyboards type NFC, so such rows should be rare; the v1.1 release notes should say it, as #172 already requires.
  - Other Unicode noncharacters that .NET can normalize (U+FFFF, U+FDD0, U+10FFFF) and the Hangul filler U+3164 alone are accepted as names, as decisions #173 and #174 define (only Cc, Cf and White_Space count as invisible; only what .NET cannot normalize is not allowed).
- Full suite: `dotnet test --artifacts-path TestResults/qa11-retest`: build 0 warnings, 0 errors; 2,503 total, 2,503 passed, 0 failed, 0 skipped (5 min 51 s). Acceptance namespace alone: 43 of 43 passed.

## History

- 2026-11-25 opened by qa-engineer
- 2026-11-25 fixed by backend-developer (decisions #170 to #174), approved by code-reviewer after two rounds; not yet committed
- 2026-11-25 re-tested by qa-engineer: closed
