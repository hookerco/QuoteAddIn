# EDP Numbers as Item Names for Wiper Inserts — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wiper insert quote lines carrying a labeled EDP# resolve to (or create) the QuickBooks item *named* that EDP number, instead of fuzzy description matching and auto-generated 1-XXXX numbers.

**Architecture:** All changes live in the shared `QuoteItemResolution` linked-source library (compiled into the IPC service, the Excel add-in, and the unit test project). `ItemLookupKey` gains an insert-vs-die line classifier that returns the EDP for qualifying insert lines; `QuoteUploadItemResolver` gains a strict find-or-create-by-EDP-name path that runs before the legacy description-matching chain and falls back to it on anomalies.

**Tech Stack:** C# / .NET Framework 4.7.2, NUnit 3, VS2022 MSBuild + vstest.console (no `dotnet` CLI).

**Spec:** `docs/superpowers/specs/2026-07-09-edp-item-names-design.md`

## Global Constraints

- .NET Framework 4.7.2; match existing C# style (no expression-bodied members, explicit braces).
- Build with `/p:Configuration=Debug` and Platform **AnyCPU** — never `/p:Platform=x86` (referenced `QuickBooksIPCContracts.csproj` has only AnyCPU and x86 fails with a BaseOutputPath error).
- Do NOT create, rename, or move any file under `QuoteItemResolution\` — the folder has no csproj; its files are compiled by `<Link>` into three projects (`QuickBooksIPCService\QuickBooksIPCServiceLibrary.csproj`, `ExcelAddIn1\QuoteUtility.csproj`, `QuickbooksIPCUnitTests\QuickBooksServiceLibrary.Tests.csproj`). All logic goes into the three existing files.
- This is a financial write path (it decides which items get created in QuickBooks). Do not change any behavior not called out in this plan; the die-set mappings, override handling, and 1-XXXX generation must be bit-for-bit identical for non-qualifying lines.
- String-literal style is per-file: `ItemLookupKey.cs` uses `""`, `QuoteUploadItemResolver.cs` uses `string.Empty`. Match the file you are editing.
- MSBuild path: `C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe`
- vstest path: `C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\Extensions\TestPlatform\vstest.console.exe`
- Unit test run filter (skips live-QuickBooks fixtures): `/TestCaseFilter:"FullyQualifiedName~ItemLookupCandidateSelectorTests|FullyQualifiedName~ItemLookupKeyTests|FullyQualifiedName~QuoteUploadItemResolverTests|FullyQualifiedName~SalesOrderItemResolutionTests|FullyQualifiedName~AuditRecordTests"`
- All commands below are PowerShell, run from the repo root `C:\Users\chooker\Documents\Projects\QuoteAddIn`.

**Build command (used repeatedly below, referred to as BUILD):**

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" QuickbooksIPCUnitTests\QuickBooksServiceLibrary.Tests.csproj /p:Configuration=Debug /v:m
```

**Test command (referred to as TEST):**

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\Extensions\TestPlatform\vstest.console.exe" QuickbooksIPCUnitTests\bin\Debug\QuickbooksIPCUnitTests.dll /TestCaseFilter:"FullyQualifiedName~ItemLookupCandidateSelectorTests|FullyQualifiedName~ItemLookupKeyTests|FullyQualifiedName~QuoteUploadItemResolverTests|FullyQualifiedName~SalesOrderItemResolutionTests|FullyQualifiedName~AuditRecordTests"
```

---

### Task 1: Insert-line EDP classifier in `ItemLookupKey`

Adds `ItemLookupKey.GetInsertEdpNumber(description, quotePartNumber)`: returns the labeled EDP number for wiper **insert** lines, `""` otherwise. Also promotes the "wiper die" phrase check into `ItemLookupKey` so the resolver and the candidate selector share one definition.

**Files:**
- Modify: `QuoteItemResolution\ItemLookupKey.cs`
- Modify: `QuoteItemResolution\ItemLookupCandidateSelector.cs` (delegate its private `DescribesWiperDie` to the shared one)
- Test: `QuickbooksIPCUnitTests\ItemLookupKeyTests.cs`

**Interfaces:**
- Consumes: existing private `ItemLookupKey.IsWiperItem(string)` and `ItemLookupKey.FindEdpNumber(string)`.
- Produces (used by Task 2):
  - `internal static string ItemLookupKey.GetInsertEdpNumber(string description, string quotePartNumber)` — EDP digits or `""`.
  - `internal static bool ItemLookupKey.DescribesWiperDie(string description)` — true iff description contains the phrase "wiper die(s)".

- [ ] **Step 1: Write the failing tests**

Append inside the `ItemLookupKeyTests` class in `QuickbooksIPCUnitTests\ItemLookupKeyTests.cs` (note: this fixture is in namespace `QuoteItemResolution.Tests` and tests the linked-source internal class directly):

```csharp
        [TestCase("WI-2000A-04000, 2 x 4 wiper insert, alum-bronze, standard cut, EDP#3700", "WI-2000A-04000", "3700")]
        [TestCase("wi-abc, wiper insert edp#1234", "wi-abc", "1234")]
        [TestCase("WI123, Wiper Insert EDP # : 1234", "WI123", "1234")]
        [TestCase("WI-2500A-03000, Wiper Insert for Die Set EDP#3819", "WI-2500A-03000", "3819")]
        public void GetInsertEdpNumber_ReturnsEdpNumberForWiperInsertLines(
            string description, string quotePartNumber, string expected)
        {
            Assert.AreEqual(expected, ItemLookupKey.GetInsertEdpNumber(description, quotePartNumber));
        }

        [TestCase("WI-2500A-03000, Inserted Wiper Die EDP#3819", "WI-2500A-03000")]
        [TestCase("WI-2500A-03000, INSERTED WIPER DIES EDP#3819", "WI-2500A-03000")]
        [TestCase("wd-2500a-03000, inserted wiper edp#3819", "wd-2500a-03000")]
        [TestCase("WD987, Wiper Die EDP#5678", "WD987")]
        [TestCase("WI-ABC, Wiper Insert without EDP", "WI-ABC")]
        [TestCase("WI-ABC, Wiper Insert EDP# ", "WI-ABC")]
        [TestCase("RB-ABC, Radius Block EDP#1234", "RB-ABC")]
        [TestCase("WIPER-ABC, Wiper Insert EDP#1234", "WIPER-ABC")]
        public void GetInsertEdpNumber_ReturnsEmptyWhenLineIsNotAnEdpWiperInsert(
            string description, string quotePartNumber)
        {
            Assert.AreEqual("", ItemLookupKey.GetInsertEdpNumber(description, quotePartNumber));
        }
```

Rationale pins: the "Wiper Insert for Die Set" case pins that only the product-type phrase "wiper die" (not the bare word "die") marks a die; the `wd-…` case pins that a WD part-number prefix marks a die even when the description doesn't say "wiper die".

- [ ] **Step 2: Run to verify failure (compile error)**

Run: BUILD
Expected: **build FAILS** with CS0117 `'ItemLookupKey' does not contain a definition for 'GetInsertEdpNumber'`.

- [ ] **Step 3: Implement the classifier**

In `QuoteItemResolution\ItemLookupKey.cs`, add these three methods inside the `ItemLookupKey` class (after `GetLookupPartNumber`, before `IsWiperItem`):

```csharp
        // The strict EDP-naming rule applies only to wiper INSERT lines: an insert and its
        // paired die share an EDP number, so a die line must never claim the bare EDP as an
        // item name. A line counts as a die when its part number leads with WD or its
        // description uses the product-type phrase "wiper die"; the bare word "die" is not
        // enough, because an insert may merely reference one ("wiper insert for die set").
        internal static string GetInsertEdpNumber(string description, string quotePartNumber)
        {
            if (!IsWiperItem(quotePartNumber))
            {
                return "";
            }

            if (IsWiperDiePartNumber(quotePartNumber) || DescribesWiperDie(description))
            {
                return "";
            }

            return FindEdpNumber(description);
        }

        internal static bool DescribesWiperDie(string description)
        {
            return Regex.IsMatch(description ?? "", @"\bWIPER\s+DIES?\b", RegexOptions.IgnoreCase);
        }

        private static bool IsWiperDiePartNumber(string quotePartNumber)
        {
            return Regex.IsMatch(quotePartNumber ?? "", @"^\s*WD(?=$|[^A-Z])", RegexOptions.IgnoreCase);
        }
```

In `QuoteItemResolution\ItemLookupCandidateSelector.cs`, delete its private `DescribesWiperDie` method (lines with the `\bWIPER\s+DIES?\b` regex) and change its one call site in `GetCandidateWiperKind` from `DescribesWiperDie(description)` to `ItemLookupKey.DescribesWiperDie(description)`.

- [ ] **Step 4: Run tests to verify pass**

Run: BUILD then TEST
Expected: build succeeds; all tests PASS (the new `GetInsertEdpNumber_*` cases plus every pre-existing test — the selector refactor must not change any `ItemLookupCandidateSelectorTests` result).

- [ ] **Step 5: Commit**

```powershell
git add QuoteItemResolution\ItemLookupKey.cs QuoteItemResolution\ItemLookupCandidateSelector.cs QuickbooksIPCUnitTests\ItemLookupKeyTests.cs
git commit -m @'
feat(resolve): classify wiper-insert lines with labeled EDP numbers

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

---

### Task 2: Strict find-or-create-by-EDP-name path in the resolver

For qualifying insert lines the resolver now uses the item **named** the EDP (or creates it), skipping description matching. Four existing tests pin the old description-matching behavior for insert lines and must be re-pinned to the new behavior (two are converted to die lines so the legacy path keeps its coverage).

**Files:**
- Modify: `QuoteItemResolution\QuoteUploadItemResolver.cs`
- Test: `QuickbooksIPCUnitTests\QuoteUploadItemResolverTests.cs`

**Interfaces:**
- Consumes (from Task 1): `internal static string ItemLookupKey.GetInsertEdpNumber(string description, string quotePartNumber)`; `internal static bool ItemLookupKey.DescribesWiperDie(string description)`.
- Produces: no new public surface. `QuoteUploadItemResolver.Resolve(IEnumerable<QBQuoteUploadLine>, IEnumerable<QBItem>)` signature unchanged; behavior change only for wiper-insert lines with a labeled EDP#.

- [ ] **Step 1: Re-pin the four legacy tests to the new behavior**

In `QuickbooksIPCUnitTests\QuoteUploadItemResolverTests.cs`:

**(a)** Replace `Resolve_WiperDescriptionsUseEdpNumberAsLookupKey` (the description-EDP lookup still exists, but for insert lines it is superseded; convert the pin to a die line, where the path still applies):

```csharp
        [Test]
        public void Resolve_WiperDieDescriptionsUseEdpNumberAsLookupKey()
        {
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine
                    {
                        Description = "WI-2500A-03000, Inserted Wiper Die EDP#3819",
                        Quantity = 1,
                        Rate = 7
                    }
                },
                new[]
                {
                    new QBItem { Number = "1-5160", Description = "WI-2500A-03000, Inserted Wiper Die EDP#3819", Active = true }
                });

            Assert.AreEqual("1-5160", result.ResolvedLines[0].Number);
            Assert.AreEqual(0, result.ItemsToCreate.Count);
        }
```

**(b)** Replace the body of `Resolve_DoesNotMatchWiLineToDieCatalogItemWhoseDescriptionLeadsWithWiToken`'s assertions (the insert line now creates the EDP-named item instead of a 1-XXXX number — the die item still must not be matched):

```csharp
            Assert.AreEqual("3819", result.ResolvedLines[0].Number);
            Assert.IsTrue(result.ResolvedLines[0].CreatedItem);
            Assert.AreEqual(1, result.ItemsToCreate.Count);
            Assert.AreEqual("3819", result.ItemsToCreate[0].Number);
```

(Keep the arrange section — line `"WI-2500A-03000, Inserted Wiper EDP#3819"`, catalog item `1-5160` with die description — exactly as it is.)

**(c)** Replace `Resolve_EdpLookupMatchesWholeNumberNotDigitSubstring` (convert to a die line so the whole-number description-match pin survives; insert lines no longer exercise this code):

```csharp
        [Test]
        public void Resolve_DieEdpLookupMatchesWholeNumberNotDigitSubstring()
        {
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine
                    {
                        Description = "WI-2500A-03000, Inserted Wiper Die EDP#3819",
                        Quantity = 1,
                        Rate = 7
                    }
                },
                new[]
                {
                    new QBItem { Number = "1-1000", Description = "WI-9999X-00000, Inserted Wiper Die EDP#38190", Active = true }
                });

            Assert.AreNotEqual("1-1000", result.ResolvedLines[0].Number);
            Assert.IsTrue(result.ResolvedLines[0].CreatedItem);
            Assert.AreEqual(1, result.ItemsToCreate.Count);
        }
```

**(d)** Replace `Resolve_MatchesWiLineToInsertWhoseDescriptionMentionsDieButIsNotAWiperDie` (same arrange; the "for Die Set" phrase must classify as insert, which now means EDP-named creation instead of reusing 1-5159):

```csharp
        [Test]
        public void Resolve_WiperInsertMentioningDieSetIsClassifiedAsInsertAndGetsEdpName()
        {
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine
                    {
                        Description = "WI-2500A-03000, Wiper Insert for Die Set EDP#3819",
                        Quantity = 1,
                        Rate = 7
                    }
                },
                new[]
                {
                    new QBItem { Number = "1-5159", Description = "WI-2500A-03000, Wiper Insert for Die Set EDP#3819", Active = true }
                });

            Assert.AreEqual("3819", result.ResolvedLines[0].Number);
            Assert.IsTrue(result.ResolvedLines[0].CreatedItem);
            Assert.AreEqual(1, result.ItemsToCreate.Count);
            Assert.AreEqual("3819", result.ItemsToCreate[0].Number);
        }
```

- [ ] **Step 2: Add the new behavior tests**

Append inside the `QuoteUploadItemResolverTests` class:

```csharp
        [Test]
        public void Resolve_WiperInsertWithEdp_MatchesItemNamedByEdpEvenWhenItsDescriptionOmitsTheEdp()
        {
            // Regression for the 1-1014 duplicate: item 3700 exists but its description
            // never mentions the EDP, and an old auto-generated item matches by description.
            // The EDP-named item must win; nothing is created.
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine
                    {
                        Description = "WI-2000A-04000, 2 x 4 wiper insert, alum-bronze, standard cut, EDP#3700",
                        Quantity = 1,
                        Rate = 64
                    }
                },
                new[]
                {
                    new QBItem { Number = "1-1014", Description = "WI-2000A-04000, 2 x 4 wiper insert, alum-bronze, standard cut, EDP#3700", Active = true },
                    new QBItem { Number = "3700", Description = "WI-2000A-04000, 2 x 4 wiper insert, alum-bronze", Active = true }
                });

            Assert.AreEqual("3700", result.ResolvedLines[0].Number);
            Assert.IsFalse(result.ResolvedLines[0].CreatedItem);
            Assert.AreEqual(0, result.ItemsToCreate.Count);
        }

        [Test]
        public void Resolve_WiperInsertWithEdp_CreatesItemNamedByEdpWhenNoneExists()
        {
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine
                    {
                        Description = "WI-2000A-04000, 2 x 4 wiper insert, alum-bronze, standard cut, EDP#3700",
                        Quantity = 1,
                        Rate = 64
                    }
                },
                new[]
                {
                    new QBItem { Number = "1-1000", Description = "RB-2500A-03000, Radius Block", Active = true }
                });

            Assert.AreEqual("3700", result.ResolvedLines[0].Number);
            Assert.IsTrue(result.ResolvedLines[0].CreatedItem);
            Assert.AreEqual(1, result.ItemsToCreate.Count);
            Assert.AreEqual("3700", result.ItemsToCreate[0].Number);
            Assert.AreEqual("WI-2000A-04000, 2 x 4 wiper insert, alum-bronze, standard cut, EDP#3700", result.ItemsToCreate[0].Description);
            Assert.AreEqual("Sales Income", result.ItemsToCreate[0].AccountName);
        }

        [Test]
        public void Resolve_RepeatedEdpInSamePass_CreatesItemOnlyOnce()
        {
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine { Description = "WI-2000A-04000, 2 x 4 wiper insert, alum-bronze, EDP#3700", Quantity = 1, Rate = 64 },
                    new QBQuoteUploadLine { Description = "WI-2000A-04000, 2 x 4 wiper insert, alum-bronze, EDP#3700", Quantity = 3, Rate = 60 }
                },
                new List<QBItem>());

            Assert.AreEqual("3700", result.ResolvedLines[0].Number);
            Assert.AreEqual("3700", result.ResolvedLines[1].Number);
            Assert.IsTrue(result.ResolvedLines[0].CreatedItem);
            Assert.IsFalse(result.ResolvedLines[1].CreatedItem);
            Assert.AreEqual(1, result.ItemsToCreate.Count);
        }

        [TestCase("WI-2500A-03000, Inserted Wiper Die EDP#3819")]
        [TestCase("WD-2500A-03000, Inserted Wiper EDP#3819")]
        public void Resolve_WiperDieLinesKeepGeneratedNumbersAndNeverClaimTheEdpName(string description)
        {
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine { Description = description, Quantity = 1, Rate = 7 }
                },
                new List<QBItem>());

            Assert.AreEqual("1-0000", result.ResolvedLines[0].Number);
            Assert.IsTrue(result.ResolvedLines[0].CreatedItem);
            Assert.AreEqual(1, result.ItemsToCreate.Count);
            Assert.AreEqual("1-0000", result.ItemsToCreate[0].Number);
        }

        [Test]
        public void Resolve_EdpNameHeldByDieDescribingItem_FallsBackToLegacyPath()
        {
            // Anomaly guard: the bare EDP name is already a die item. Do not resolve the
            // insert line to it and do not queue a colliding item write - fall back to
            // description matching (which excludes the die) and 1-XXXX generation.
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine
                    {
                        Description = "WI-2500A-03000, Inserted Wiper EDP#3819",
                        Quantity = 1,
                        Rate = 7
                    }
                },
                new[]
                {
                    new QBItem { Number = "3819", Description = "WI-2500A-03000, Inserted Wiper Die EDP#3819", Active = true }
                });

            Assert.AreEqual("1-0000", result.ResolvedLines[0].Number);
            Assert.IsTrue(result.ResolvedLines[0].CreatedItem);
            Assert.AreEqual(1, result.ItemsToCreate.Count);
            Assert.AreEqual("1-0000", result.ItemsToCreate[0].Number);
        }

        [Test]
        public void Resolve_EdpNameHeldByInactiveItem_FallsBackToLegacyPath()
        {
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine
                    {
                        Description = "WI-2000A-04000, 2 x 4 wiper insert, alum-bronze, EDP#3700",
                        Quantity = 1,
                        Rate = 64
                    }
                },
                new[]
                {
                    new QBItem { Number = "3700", Description = "WI-2000A-04000, 2 x 4 wiper insert, alum-bronze", Active = false }
                });

            Assert.AreEqual("1-0000", result.ResolvedLines[0].Number);
            Assert.IsTrue(result.ResolvedLines[0].CreatedItem);
            Assert.AreEqual(1, result.ItemsToCreate.Count);
            Assert.AreEqual("1-0000", result.ItemsToCreate[0].Number);
        }

        [Test]
        public void Resolve_OverrideStillWinsForWiperInsertWithEdp()
        {
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine
                    {
                        Description = "WI-2000A-04000, 2 x 4 wiper insert, alum-bronze, EDP#3700",
                        Quantity = 1,
                        Rate = 64,
                        OverrideNumber = "1-9999"
                    }
                },
                new[]
                {
                    new QBItem { Number = "3700", Description = "WI-2000A-04000, 2 x 4 wiper insert, alum-bronze", Active = true }
                });

            Assert.AreEqual("1-9999", result.ResolvedLines[0].Number);
        }
```

- [ ] **Step 3: Run to verify the expected failures**

Run: BUILD then TEST
Expected: build succeeds; the seven new `Resolve_*` tests and the four re-pinned tests **FAIL** (lines currently resolve via description matching / 1-XXXX generation). All other tests PASS. If anything else fails, stop and investigate before touching the implementation.

- [ ] **Step 4: Implement the strict EDP-name path**

In `QuoteItemResolution\QuoteUploadItemResolver.cs`:

**(a)** Replace the `else` branch of the `foreach` loop in `Resolve` (currently lines 57–77) with:

```csharp
                else
                {
                    string quotePartNumber = FindPartNumber(line.Description);
                    number = ResolveEdpNamedInsertItem(
                        line, quotePartNumber, catalog, activeCatalog, numbersToCreate, result, out createdItem);

                    if (number == string.Empty)
                    {
                        string lookupPartNumber = ItemLookupKey.GetLookupPartNumber(line.Description, quotePartNumber);
                        number = FindMatchingItemNumber(activeCatalog, lookupPartNumber, quotePartNumber);

                        if (number == string.Empty)
                        {
                            number = GetDieSetItemNumber(quotePartNumber);
                        }

                        if (number == string.Empty)
                        {
                            number = GenerateNumber(reservedNumbers);
                            numbersToCreate.Add(number);
                            QBItem item = CreateNonInventoryItem(number, line.Description);
                            result.ItemsToCreate.Add(item);
                            activeCatalog.Add(item);
                            createdItem = true;
                        }
                    }
                }
```

**(b)** Add the new private method (place it right after `Resolve`, before `GetActiveCatalog`):

```csharp
        // Strict rule for wiper-insert lines carrying a labeled EDP number: the EDP is the
        // item's only QuickBooks identity. Match the item NAME (EDP-named items usually do
        // not repeat the EDP in their description), otherwise create the EDP-named item.
        // Description matching is deliberately skipped for these lines, so legacy 1-XXXX
        // wiper items whose descriptions mention the EDP are superseded, not reused.
        // Returns empty when the rule does not apply or the EDP name is unusable (held by
        // an inactive or die-describing item) - the caller then runs the legacy path.
        private static string ResolveEdpNamedInsertItem(
            QBQuoteUploadLine line,
            string quotePartNumber,
            List<QBItem> catalog,
            List<QBItem> activeCatalog,
            HashSet<string> numbersToCreate,
            QuoteUploadItemResolution result,
            out bool createdItem)
        {
            createdItem = false;
            string edpNumber = ItemLookupKey.GetInsertEdpNumber(line.Description, quotePartNumber);
            if (edpNumber == string.Empty)
            {
                return string.Empty;
            }

            foreach (QBItem item in activeCatalog)
            {
                if (System.StringComparer.OrdinalIgnoreCase.Equals(item.Number ?? string.Empty, edpNumber))
                {
                    if (ItemLookupKey.DescribesWiperDie(item.Description))
                    {
                        return string.Empty;
                    }

                    return item.Number;
                }
            }

            foreach (QBItem item in catalog)
            {
                if (item != null && !item.Active &&
                    System.StringComparer.OrdinalIgnoreCase.Equals(item.Number ?? string.Empty, edpNumber))
                {
                    return string.Empty;
                }
            }

            numbersToCreate.Add(edpNumber);
            QBItem newItem = CreateNonInventoryItem(edpNumber, line.Description);
            result.ItemsToCreate.Add(newItem);
            activeCatalog.Add(newItem);
            createdItem = true;
            return edpNumber;
        }
```

Notes for the implementer:
- The active-catalog name scan runs before the create, and items created earlier in the same pass were appended to `activeCatalog`, so a repeated EDP within one upload resolves to the already-created item with `createdItem == false` (matching how repeated descriptions behave today).
- `catalog` (the unfiltered list) is scanned only for the inactive-name anomaly; do not "reuse" an inactive item.

- [ ] **Step 5: Run tests to verify all pass**

Run: BUILD then TEST
Expected: build succeeds; **every** test PASSES (new, re-pinned, and all pre-existing including `SalesOrderItemResolutionTests` and `AuditRecordTests`).

- [ ] **Step 6: Commit**

```powershell
git add QuoteItemResolution\QuoteUploadItemResolver.cs QuickbooksIPCUnitTests\QuoteUploadItemResolverTests.cs
git commit -m @'
feat(resolve): wiper inserts with EDP numbers resolve to EDP-named items

Insert lines carrying a labeled EDP# now find-or-create the item NAMED the
EDP instead of description matching, which duplicated EDP-named items (the
1-1014/3700 bug). Dies keep 1-XXXX; inactive or die-held EDP names fall back
to the legacy path.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

---

### Task 3: Cross-project compile verification

The shared sources are also compiled into the Excel add-in; verify that project still builds. No code changes expected — this task is verification only and has no commit.

**Files:**
- Build only: `ExcelAddIn1\QuoteUtility.csproj` (compiles the same linked `QuoteItemResolution\*.cs` sources)

**Interfaces:**
- Consumes: Tasks 1–2 final sources.
- Produces: nothing — a green build is the deliverable.

- [ ] **Step 1: Build the add-in project**

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" ExcelAddIn1\QuoteUtility.csproj /p:Configuration=Debug /p:Platform=AnyCPU /v:m
```

Expected: `Build succeeded.` with 0 errors. (Warnings are pre-existing and acceptable.)

- [ ] **Step 2: Re-run the full unit filter one final time**

Run: TEST
Expected: all tests PASS. If Step 1 or Step 2 fails, fix within the three shared files and re-run Tasks 1–3 verification before declaring done.

---

## Post-plan (not part of task execution)

- Live-QB confirmation via the project `/verify` skill before any ClickOnce publish (user-initiated; the dummy company file pipeline).
- The user will manually deactivate old duplicates (e.g. `1-1014`) in QuickBooks.
