# EDP numbers as QuickBooks item names for wiper inserts — design

**Date:** 2026-07-09
**Status:** Approved pending user review
**Scope:** `QuoteItemResolution` shared library (linked into `QuickBooksIPCServiceLibrary`, `QuoteUtility` Excel add-in, and `QuickbooksIPCUnitTests` — no csproj changes; no files added or renamed)

## Problem

A recent quote created auto-generated items (e.g. `1-1014`) for wiper inserts that
already exist in QuickBooks under their EDP number (e.g. item `3700`). The resolver
extracts the EDP from the quote line ("…, EDP#3700") but matches it only against item
**descriptions**. EDP-named items usually do not repeat the EDP in their description,
so the match misses and the resolver falls through to first-free `1-XXXX` generation,
creating a duplicate.

## Decision (user-confirmed)

For a **wiper insert** line whose description carries an EDP#, the EDP number is the
item's one and only QuickBooks identity:

1. If an active item is **named** exactly the EDP number (and does not describe a
   wiper die), resolve to it — regardless of whether its description mentions the EDP.
2. Otherwise **create** a non-inventory item named the EDP number (same defaults as
   today: Sales Income account, active, line description as description). Add it to
   the working catalog so later lines in the same upload with the same EDP reuse it.
3. Description-based matching is **skipped entirely** for these lines. Explicitly:
   an existing item that carries the matching EDP# only in its *description*
   (e.g. `1-1014` "…EDP#3700", or `T010411` "4484, WI-…") is **not** reused — the
   EDP-named item is used or created instead. Old duplicates will be deactivated
   manually by the user; no automated cleanup.

### Anomaly fallback

If the EDP name is already taken by an **inactive** item or by an item that
**describes a wiper die**, do not risk a name collision on the QuickBooks write:
fall back to today's full behavior for that line (description matching, then
die-set numbers, then `1-XXXX` generation).

### Line classification

A line qualifies for the rule when all three hold:

- leading part number is WI/WD-prefixed (existing `ItemLookupKey.IsWiperItem`), and
- description contains a labeled EDP# (existing `FindEdpNumber` — `EDP#1234` forms
  only; a bare leading number like "4484," is not treated as an EDP), and
- the line is an **insert**, not a die: it is a die if the part number starts with
  `WD` or the description contains the "wiper die(s)" phrase (same rule as
  `ItemLookupCandidateSelector.DescribesWiperDie`).

### Unchanged behavior

- Pinned overrides (`OverrideNumber`) always win, exactly as today.
- Wiper **dies** keep first-free `1-XXXX` numbers (an insert and its die share an
  EDP; naming both by the EDP would collide).
- Wiper lines with no EDP#, die-set prefixes (BB/, CI/, CD, PD), and all non-wiper
  lines are untouched.
- The hidden audit sheet needs no changes — it reads the resolver's output.

## Implementation shape

All logic lives in existing files (`QuoteUploadItemResolver.cs`, `ItemLookupKey.cs`,
`ItemLookupCandidateSelector.cs`) so the three linking csprojs need no edits. The
"is this line a wiper insert with an EDP" classification goes in `ItemLookupKey`
alongside the existing EDP extraction; the die-phrase regex is shared with the
candidate selector rather than duplicated.

## Testing

Pure unit tests in `QuickbooksIPCUnitTests` (no live QuickBooks required):

- Regression: insert line with EDP#3700 resolves to existing item `3700` whose
  description never mentions the EDP (the `1-1014` bug).
- EDP-named item wins even when a `1-XXXX` item matches by description.
- No EDP-named item → creates item named the EDP, not `1-XXXX`.
- Item with matching EDP only in its description is not reused; EDP item is created.
- Two lines with the same EDP in one upload → one created item, both lines use it.
- Die line with an EDP# → unchanged behavior; never matches or creates the bare
  EDP-named item.
- EDP name held by an inactive or die-describing item → falls back to old behavior.
- Overrides still win; non-wiper and EDP-less wiper lines unchanged.

Final confirmation via the project `/verify` skill (live-QB pipeline check) before
publishing a new ClickOnce version.

## Risk notes

This is a financial write path (which items get created in QuickBooks). The strict
rule intentionally creates new EDP-named items the first time each EDP-bearing
insert is quoted after the change; existing non-EDP-named wiper items stop being
selected for such lines and can be retired manually.
