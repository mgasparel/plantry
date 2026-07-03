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
  };
}

// ── buildSaveItems ────────────────────────────────────────────────────────────

/**
 * Build the items array for the save POST body from an array of dirty rows.
 *
 * Pure transform of its arguments — no network I/O, no signal subscriptions
 * beyond reading `.value`. Called inside `save()` after the dirty filter;
 * extracted here so the shape of the POST payload has an explicit contract.
 *
 * @param {Row[]} dirtyRows   — only rows where row.dirty.value === true
 * @returns {{ productId: string, countedValue: number, countedUnitId: string, reason: string }[]}
 */
export function buildSaveItems(dirtyRows) {
  return dirtyRows.map((r) => ({
    productId: r.productId,
    countedValue: r.counted.value,
    countedUnitId: r.unitId.value,
    reason: r.reason.value,
  }));
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
