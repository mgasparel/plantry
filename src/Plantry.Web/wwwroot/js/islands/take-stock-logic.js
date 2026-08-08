// @ts-check
//
// take-stock-logic.js — pure transforms for the Take Stock island (ADR-020, bead plantry-2zvm.13).
//
// CONVENTION (island testing):
//   Pure transforms are extracted into a sibling `*-logic.js` module.
//   The island (`take-stock.js`) imports and calls them.
//   Tests (`__tests__/take-stock-logic.test.js`) import from here using
//   `node --test` (built-in, zero deps).
//   This keeps the island file focused on wiring/rendering; the running file
//   is still the file you read (no build, no transpile).
//
// What belongs here (ADR-020 §2 / §7 boundary):
//   UI/draft state transforms — hydration→signal factories, count-clamping,
//   and save-body assembly from dirty rows. These are pure functions of their
//   arguments and hold NO domain logic. They do not compute actual stock
//   quantities, validate business rules, or implement any domain-stock rule.
//   setCount is UI input handling (clamp + NaN guard); the server owns the
//   actual stock mutation (§7 tripwire).
//
// What does NOT belong here:
//   Anything that crosses the ADR-020 §7 tripwire. If you need domain rules,
//   call a server endpoint instead.
//
// save() is intentionally excluded: it calls postJson (network I/O), mutates
//   toast/saving signals as side effects, and is inseparable from the fetch
//   lifecycle. Its coverage is left to the existing Playwright E2E suite.
//   buildSaveItems and reconcileResults — the two pure sub-steps inside save()
//   — are extracted here and tested.

// ── Types ─────────────────────────────────────────────────────────────────────

/** @typedef {{ unitId: string, code: string }} UnitOption */

/**
 * Hydration shape emitted by the server (Walk.cshtml → IslandRow DTO).
 * @typedef {Object} RowSeed
 * @property {string} productId
 * @property {string} productName
 * @property {number} recorded
 * @property {string} unitCode
 * @property {string} unitId
 * @property {boolean} [hasActiveStock]
 * @property {string} [lotsUrl]
 * @property {UnitOption[]} [supportedUnits]
 * @property {boolean} [isNewRow]
 * @property {string} [expiryDate]   optional yyyy-MM-dd seed for a row injected already-dirty (plantry-4onl)
 * @property {string} [saveLotsUrl]  POST ?handler=SaveLots URL for this product (plantry-vvqt walk redesign)
 * @property {string|null} [categoryName]      category grouping label (plantry-vvqt); null groups under "Other"
 * @property {number} [categorySortOrder]      store-layout sort order for the category group (plantry-vvqt)
 */

/**
 * Minimal signal/computed shape used by tests (mirrors the @preact/signals API
 * surface that the logic functions depend on). The island passes real signals;
 * the test rig passes these plain objects.
 *
 * For read/write signals:
 * @template T
 * @typedef {{ value: T }} SignalLike
 */

/**
 * One row's reactive state shape expected by setCount, buildSaveItems,
 * and reconcileResults.
 *
 * @typedef {Object} Row
 * @property {string} productId
 * @property {string} productName
 * @property {string} unitCode
 * @property {boolean} hasActiveStock
 * @property {string} lotsUrl
 * @property {UnitOption[]} supportedUnits
 * @property {SignalLike<number>} recorded
 * @property {SignalLike<number>} counted
 * @property {SignalLike<string>} unitId
 * @property {SignalLike<string>} reason
 * @property {SignalLike<boolean>} failed
 * @property {SignalLike<string|null>} failMsg
 * @property {SignalLike<boolean>} dirty        ReadonlySignal in the real island; SignalLike for tests
 * @property {SignalLike<boolean>} down         ReadonlySignal in the real island; SignalLike for tests
 * @property {boolean} isNewRow
 * @property {SignalLike<boolean>} needsConversion   true when the last save returned a needsConversion row (plantry-3mwx)
 * @property {SignalLike<string>} convFromUnitId     the counted unit id awaiting a conversion factor
 * @property {SignalLike<string>} convFromCode       the counted unit's display code
 * @property {SignalLike<string>} convToUnitId       the product default unit id to convert into
 * @property {SignalLike<string>} convToCode         the product default unit's display code
 * @property {SignalLike<string>} convFactor         the user-entered conversion factor (raw input)
 * @property {SignalLike<string>} expiryDate         optional yyyy-MM-dd for a found/increased lot (plantry-4onl);
 *                                                    only sent to the server when the row is an increase (see buildSaveItems)
 * @property {SignalLike<boolean>} confirmed         true once the user has explicitly reviewed this row via the
 *                                                    check-off tap or the adjuster sheet's Confirm/Done button
 *                                                    (plantry-vvqt walk redesign) — walk-progress state only, never
 *                                                    posted to the server (see rowStatus)
 * @property {string} saveLotsUrl                    POST ?handler=SaveLots URL for this row's product (plantry-vvqt);
 *                                                    empty string for rows with no lot escape-hatch (new rows)
 * @property {string|null} categoryName              category grouping label (plantry-vvqt); null → "Other" group
 * @property {number} categorySortOrder              store-layout sort order for the category group (plantry-vvqt)
 */

/**
 * One result entry from the server's save response.
 * @typedef {Object} SaveResult
 * @property {string} productId
 * @property {boolean} isSuccess
 * @property {string|null} [error]
 * @property {boolean} [needsConversion]   true when the row needs a conversion factor (plantry-3mwx)
 * @property {string} [fromUnitId]         the counted unit id (needsConversion rows)
 * @property {string} [fromUnitCode]       the counted unit's display code
 * @property {string} [toUnitId]           the product default unit id to convert into
 * @property {string} [toUnitCode]         the product default unit's display code
 */

// ── setCount ─────────────────────────────────────────────────────────────────

/**
 * Update the counted value on a row from a raw input (number or string).
 *
 * Rules:
 * - If `raw` is a number, use it directly.
 * - If `raw` is a string, parse with parseFloat.
 * - If the result is NaN (empty string, "abc", etc.) fall back to row.recorded.value.
 * - Clamp the final value to Math.max(0, parsed) — negative inputs become 0.
 * - Always clear row.failed and row.failMsg (resets any prior save-error state).
 *
 * This is a UI input-handling transform. The server owns the actual stock mutation
 * (ADR-020 §7 tripwire).
 *
 * @param {Row} row
 * @param {string | number} raw
 * @returns {void}
 */
export function setCount(row, raw) {
  const parsed = typeof raw === "number" ? raw : parseFloat(raw);
  row.counted.value = Number.isNaN(parsed) ? row.recorded.value : Math.max(0, parsed);
  row.failed.value = false;
  row.failMsg.value = null;
}

// ── makeRow ──────────────────────────────────────────────────────────────────

/**
 * Build initial Row reactive state from a server hydration seed.
 *
 * Pure function of its arguments — `signal` and `computed` are injected so
 * tests can pass plain-object stubs rather than real Preact signals.
 *
 * Computeds:
 * - `dirty`: counted !== recorded  (any deviation from the recorded value)
 * - `down`:  dirty && counted < recorded  (a decrease — triggers reason selector)
 *
 * Defaults:
 * - `reason`: "Correction"
 * - `failed`: false
 * - `failMsg`: null
 * - `hasActiveStock`: seed.hasActiveStock ?? false
 * - `lotsUrl`: seed.lotsUrl ?? ""
 * - `supportedUnits`: seed.supportedUnits ?? []
 * - `isNewRow`: seed.isNewRow ?? false
 *
 * @template {SignalLike<any>} S
 * @param {RowSeed} seed
 * @param {(v: any) => S} signalFn       — real `signal` from Preact or a plain-object stub
 * @param {(fn: () => any) => S} computedFn  — real `computed` from Preact or a stub that calls fn() immediately
 * @returns {Row}
 */
export function makeRow(seed, signalFn, computedFn) {
  const recorded = signalFn(seed.recorded);
  const counted = signalFn(seed.recorded);
  const dirty = computedFn(() => counted.value !== recorded.value);
  const down = computedFn(() => dirty.value && counted.value < recorded.value);
  return {
    productId: seed.productId,
    productName: seed.productName,
    unitCode: seed.unitCode,
    hasActiveStock: seed.hasActiveStock ?? false,
    lotsUrl: seed.lotsUrl ?? "",
    supportedUnits: seed.supportedUnits ?? [],
    recorded,
    counted,
    unitId: signalFn(seed.unitId),
    reason: signalFn("Correction"),
    failed: signalFn(false),
    failMsg: signalFn(/** @type {string | null} */ (null)),
    dirty,
    down,
    isNewRow: seed.isNewRow ?? false,
    // NeedsConversion prompt state (plantry-3mwx) — set by reconcileResults when a save returns a
    // needsConversion row, cleared once the factor is saved and the row re-saves cleanly.
    needsConversion: signalFn(false),
    convFromUnitId: signalFn(""),
    convFromCode: signalFn(""),
    convToUnitId: signalFn(""),
    convToCode: signalFn(""),
    convFactor: signalFn(""),
    // Optional expiry for a found/increased lot (plantry-4onl). Blank means blank — no default
    // inheritance from the product's DefaultDueDays (decision 1; Take Stock deliberately differs
    // from the Add Stock sheet here).
    expiryDate: signalFn(seed.expiryDate ?? ""),
    // Walk-progress state only (plantry-vvqt walk redesign) — never posted to the server. Set by
    // toggleRowCheck / confirmRow and read by rowStatus. Starts false: an untouched row is "todo".
    confirmed: signalFn(false),
    // Plain (non-signal) fields — set once at hydration, never mutated after.
    saveLotsUrl: seed.saveLotsUrl ?? "",
    categoryName: seed.categoryName ?? null,
    categorySortOrder: seed.categorySortOrder ?? Number.MAX_SAFE_INTEGER,
  };
}

// ── rowStatus / toggleRowCheck / confirmRow (plantry-vvqt walk redesign) ───────

/**
 * Derives a row's check-off status for the walk redesign's progress strip and row styling.
 *
 * - "chg"  — dirty (counted differs from recorded); always wins over confirmed, since a
 *            pending edit always needs review regardless of prior confirm state.
 * - "ok"   — confirmed and not dirty (the common case: shelf matched the record).
 * - "todo" — untouched — neither confirmed nor dirty.
 *
 * @param {Row} row
 * @returns {"todo" | "ok" | "chg"}
 */
export function rowStatus(row) {
  if (row.dirty.value) return "chg";
  if (row.confirmed.value) return "ok";
  return "todo";
}

/**
 * Handle a tap on the row's check-off button. Mirrors the approved prototype
 * (.preview/take-stock-walk-redesign.html):
 * - "todo" → confirmed (counted already equals recorded, so no count change needed).
 * - "ok"   → un-confirm, back to "todo" (a mis-tap escape hatch).
 * - "chg"  → re-confirm AT the recorded value, discarding the pending edit — the check-off is a
 *            "matches record" affirmation, not a way to save a change (that's the adjuster sheet).
 *
 * @param {Row} row
 * @returns {void}
 */
export function toggleRowCheck(row) {
  const status = rowStatus(row);
  // A "chg" reset-to-recorded only makes sense for a row seeded from the server's recorded
  // baseline. A row injected by inline-add is seeded recorded: 0 with counted set to the quantity
  // the user just entered (take-stock.js handleSheetAdd) — resetting counted to 0 here would
  // silently zero out the addition and drop it from Save with no warning. For a new row the
  // check-off simply confirms it in place; rowStatus already keeps it "chg" while dirty, so this
  // is a review acknowledgement, not a reset.
  if (status === "chg" && !row.isNewRow) {
    row.counted.value = row.recorded.value;
    row.failed.value = false;
    row.failMsg.value = null;
  }
  row.confirmed.value = status !== "ok";
}

/**
 * Mark a row reviewed without touching its counted value — called when the adjuster sheet's
 * Confirm/Done button closes the sheet. A dirty row stays dirty (status "chg"); confirming only
 * matters for the "todo"/"ok" distinction once the row is clean again.
 *
 * @param {Row} row
 * @returns {void}
 */
export function confirmRow(row) {
  row.confirmed.value = true;
}

// ── groupRowsByCategory ─────────────────────────────────────────────────────

/**
 * Groups rows by `categoryName` for the walk's category-grouped list (plantry-vvqt design item 6).
 * Rows with no category (`categoryName` null/empty) fall into an "Other" bucket. Groups are ordered
 * by `categorySortOrder` (the household's store-layout order), then alphabetically by name as a
 * tiebreaker; "Other" naturally sorts last because makeRow defaults an absent categorySortOrder to
 * Number.MAX_SAFE_INTEGER. Rows within a group keep their incoming relative order (stable sort).
 *
 * Pure transform: reads only plain fields (no signal `.value` reads), so it does not itself need to
 * be reactive — callers re-derive the grouping from `rows.value` inside the render, same as any
 * other derived-from-signals view.
 *
 * @param {Row[]} rows
 * @returns {{ name: string, sortOrder: number, items: Row[] }[]}
 */
export function groupRowsByCategory(rows) {
  /** @type {Map<string, { name: string, sortOrder: number, items: Row[] }>} */
  const groups = new Map();
  for (const row of rows) {
    const name = row.categoryName || "Other";
    const sortOrder = row.categoryName ? row.categorySortOrder : Number.MAX_SAFE_INTEGER;
    let group = groups.get(name);
    if (!group) {
      group = { name, sortOrder, items: [] };
      groups.set(name, group);
    }
    group.items.push(row);
  }
  return [...groups.values()].sort((a, b) =>
    a.sortOrder - b.sortOrder || a.name.localeCompare(b.name));
}

// ── readyToSaveCount ─────────────────────────────────────────────────────────

/**
 * The sticky Save bar's "N changes ready" count and the walk's overall completeness signal
 * (plantry-vvqt design point 4/7) — dirty rows PLUS products whose lot panel holds a pending
 * adjustment. A lot-only edit (no row-level count changed) still counts as a pending change: the
 * adjuster sheet's Done button no longer flushes the lot panel itself (that would just relocate
 * the separate save trigger the redesign removes), so dirtyLotIds is the only place that edit is
 * tracked once the sheet closes, and it must still surface here.
 *
 * @param {number} dirtyRowCount           rows.filter(r => r.dirty.value).length
 * @param {Record<string, boolean>} dirtyLotIds   productId -> true while that product's lot panel is dirty
 * @returns {number}
 */
export function readyToSaveCount(dirtyRowCount, dirtyLotIds) {
  return dirtyRowCount + Object.keys(dirtyLotIds).length;
}

// ── buildSaveItems ────────────────────────────────────────────────────────────

/**
 * Build the items array for the save POST body from an array of dirty rows.
 *
 * Pure transform of its arguments — no network I/O, no signal subscriptions
 * beyond reading `.value`. Called inside `save()` after the dirty filter;
 * extracted here so the shape of the POST payload has an explicit contract.
 *
 * An increase (counted > recorded, i.e. `!row.down.value` on a dirty row) additionally carries
 * `expiryDate` when the user entered one — the optional expiry for the lot that increase mints
 * (plantry-4onl). Omitted entirely (not sent as null) on a decrease or no-op row: the server
 * ignores a supplied expiry when the delta doesn't go up, but the key is left off the wire shape
 * so that intent is explicit at the boundary, not just tolerated.
 *
 * @param {Row[]} dirtyRows   — only rows where row.dirty.value === true
 * @returns {{ productId: string, countedValue: number, countedUnitId: string, reason: string, expiryDate?: string }[]}
 */
export function buildSaveItems(dirtyRows) {
  return dirtyRows.map((r) => {
    /** @type {{ productId: string, countedValue: number, countedUnitId: string, reason: string, expiryDate?: string }} */
    const item = {
      productId: r.productId,
      countedValue: r.counted.value,
      countedUnitId: r.unitId.value,
      reason: r.reason.value,
    };
    if (!r.down.value && r.expiryDate.value) {
      item.expiryDate = r.expiryDate.value;
    }
    return item;
  });
}

// ── reconcileResults ─────────────────────────────────────────────────────────

/**
 * Reconcile a server save response onto the row signal state.
 *
 * For each result in `results`:
 * - If `isSuccess`: advance recorded to match counted (row is now clean),
 *   clear failed/failMsg.
 * - If not `isSuccess`: set failed=true, failMsg to result.error (or fallback).
 *
 * Returns `{ saved, failed }` counts for toast message assembly.
 *
 * Pure transform: only reads/writes `.value` on signal-like objects;
 * no network I/O, no DOM access.
 *
 * A result carrying `needsConversion: true` is neither saved nor a plain failure: the row is put
 * into the conversion-prompt state (plantry-3mwx) so the UI can collect a factor. Such rows are
 * counted in the returned `needsConversion` tally and are NOT included in `failed`.
 *
 * @param {Row[]} rows                  — the full row list (haystack for productId lookup)
 * @param {SaveResult[]} results        — data.results from the server response
 * @returns {{ saved: number, failed: number, needsConversion: number }}
 */
export function reconcileResults(rows, results) {
  const byId = new Map(rows.map((r) => [r.productId, r]));
  let saved = 0, failed = 0, needsConversion = 0;
  for (const result of results) {
    const row = byId.get(result.productId);
    if (!row) continue;
    if (result.isSuccess) {
      row.recorded.value = row.counted.value;
      row.failed.value = false;
      row.failMsg.value = null;
      row.needsConversion.value = false;
      // A saved row has definitely been reviewed (plantry-vvqt walk redesign) — without this, a row
      // edited via the adjuster sheet and saved directly (never tapping the row check-off) would
      // read back as "todo" once clean, understating the walk's progress count.
      row.confirmed.value = true;
      saved++;
    } else if (result.needsConversion) {
      // Hold the row for a conversion factor instead of showing a raw error (C10 parity).
      row.needsConversion.value = true;
      row.convFromUnitId.value = result.fromUnitId ?? row.unitId.value;
      row.convFromCode.value = result.fromUnitCode ?? "";
      row.convToUnitId.value = result.toUnitId ?? "";
      row.convToCode.value = result.toUnitCode ?? "";
      row.failed.value = false;
      row.failMsg.value = null;
      needsConversion++;
    } else {
      row.failed.value = true;
      row.failMsg.value = result.error ?? "Failed to save";
      row.needsConversion.value = false;
      failed++;
    }
  }
  return { saved, failed, needsConversion };
}

// ── saveStatusMessage ─────────────────────────────────────────────────────────

/**
 * Pure status/toast text for a save outcome — the four branches the save() flow reaches:
 * transport failure (!ok), all-saved, all-failed, and partial success. Extracted so the
 * partial-success and all-failed wording (previously only reachable via the live fetch path)
 * is unit-tested.
 * @param {{ ok: boolean, status?: number, saved?: number, failed?: number }} outcome
 * @returns {string}
 */
export function saveStatusMessage({ ok, status, saved = 0, failed = 0 }) {
  if (!ok) return `Save failed (${status}) — please try again`;
  if (failed === 0) return saved === 1 ? "1 item updated" : `${saved} items updated`;
  if (saved === 0) return "Save failed — please try again";
  return `${saved} saved, ${failed} failed — retry the highlighted rows`;
}

// ── mergeSheetUnitIntoRow ──────────────────────────────────────────────────────

/**
 * The inline-add sheet payload (subset used by the existing-row merge).
 * @typedef {Object} SheetAddDetail
 * @property {string} [productId]
 * @property {string} [productName]
 * @property {number|string} [addCount]
 * @property {string} [addUnitId]
 * @property {string} [addUnitCode]
 * @property {UnitOption[]} [supportedUnits]
 * @property {string} [expiryDate]   optional yyyy-MM-dd for the opening-balance lot (plantry-4onl)
 */

/**
 * Merge an inline-add sheet payload onto a row that is ALREADY in the working set
 * (plantry-3mwx root-cause #1; regression-covered per plantry-1me7).
 *
 * Carries the sheet-selected count AND unit onto the existing row. The unit carry is the fix for
 * plantry-3mwx: previously the chosen unit was dropped here, so a count entered in a non-default unit
 * was silently recorded in the product default unit. When the chosen unit is not yet in the row's
 * reachable `supportedUnits` set (the per-row selector is limited to units reachable from the default),
 * it is appended so the selector can display it.
 *
 * Pure transform: mutates only the passed row — signal `.value` writes plus reassignment of the plain
 * `supportedUnits`/`unitCode` fields. No DOM, no network. The island re-publishes the rows array
 * afterwards to trigger the re-render.
 *
 * @param {Row} row
 * @param {SheetAddDetail} detail
 * @returns {void}
 */
export function mergeSheetUnitIntoRow(row, detail) {
  const newCounted = parseFloat(String(detail.addCount ?? 0)) || 0;
  row.counted.value = newCounted;
  row.failed.value = false;
  row.failMsg.value = null;
  row.needsConversion.value = false;
  // Carry the sheet-entered expiry onto the row (plantry-4onl). Blank is a valid, meaningful value
  // (decision 1 — no default inheritance), so it is always written, not only when truthy.
  row.expiryDate.value = detail.expiryDate ?? "";
  if (detail.addUnitId) {
    row.unitId.value = detail.addUnitId;
    // Ensure the selected unit is displayable even if it is not in the reachable set yet.
    if (detail.addUnitCode
        && !row.supportedUnits.some((u) => u.unitId === detail.addUnitId)) {
      row.supportedUnits = [...row.supportedUnits, { unitId: detail.addUnitId, code: detail.addUnitCode }];
    }
    if (detail.addUnitCode) row.unitCode = detail.addUnitCode;
  }
}

// ── shouldShowMarkCounted ─────────────────────────────────────────────────────

/**
 * Visibility predicate for the walk header's explicit "Mark counted" button (plantry-hp67).
 *
 * The button is the zero-change completion path: the Save bar only renders when at least one
 * row is dirty, so a fully-confirmed walk (nothing to change) needs an explicit, user-authored
 * gesture to advance the location's freshness. It shows only when:
 *   - nothing is dirty (a dirty walk completes via Save, which stamps server-side), and
 *   - there is at least one product row (an empty location has nothing to count), and
 *   - this session hasn't already stamped the location (via the button itself, or a Save whose
 *     response reported a successful stamp) — so the button can never contradict a header
 *     already updated to the server-reported freshness.
 *
 * @param {number} dirtyCount
 * @param {number} rowCount
 * @param {boolean} markedThisSession
 * @returns {boolean}
 */
export function shouldShowMarkCounted(dirtyCount, rowCount, markedThisSession) {
  return dirtyCount === 0 && rowCount > 0 && !markedThisSession;
}
