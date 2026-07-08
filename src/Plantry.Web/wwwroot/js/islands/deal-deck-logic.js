// @ts-check
//
// deal-deck-logic.js — pure transforms for the Deals judgement-call deck island
// (ADR-020 fourth-island amendment, bead plantry-q9zr.8).
//
// CONVENTION (island testing, mirrors take-stock-logic.js):
//   Pure transforms are extracted into this sibling `*-logic.js` module. The island
//   (`deal-deck.js`) imports and calls them. Tests
//   (`__tests__/deal-deck-logic.test.js`) import from here and run under `node --test`
//   (built-in, zero deps). The running file is the file you read — no build, no transpile.
//
// What belongs here (ADR-020 §2 / §7 boundary):
//   PRESENTATION-ONLY deck transforms — deck membership/order (rebuilt from tier truth
//   while preserving skip-rotation order), skip/back reordering, swipe-gesture geometry
//   (threshold, stamp opacity, transform), and the per-flyer high-water progress. These
//   are pure functions of their arguments and hold NO domain logic: they never confirm,
//   reject, or reclassify a deal. Every verb still posts through the existing htmx
//   endpoints and the server re-renders #review-region from truth (§7 tripwire); the deck
//   owns order + drag + focus, nothing more.
//
// What does NOT belong here:
//   Anything that crosses the ADR-020 §7 tripwire — deal classification (which step a
//   deal belongs to), confirm/reject/correct effects, or price/confidence rules. Those
//   are server-side (ReviewStepClassifier + the three verbs).

/** The pointer-drag distance (px) a card must pass to commit a swipe verb. */
export const DECK_SWIPE_THRESHOLD = 120;

// ── Deck membership + order ────────────────────────────────────────────────────

/**
 * Rebuild the deck's ordered id list from the current tier truth, preserving the
 * skip-rotation order of cards already in the deck (the buildDeck rule from the adopted
 * focus.html prototype). Cards still eligible keep their prior relative order; newly
 * eligible cards (e.g. a High just demoted into step 2) are appended at the end.
 *
 * Pure: no DOM, no persistence — the island reads/writes the prior order from
 * sessionStorage around this call.
 *
 * @param {string[]} eligibleIds  — the deal ids the server currently classifies into step 2, in server order.
 * @param {string[]} [priorOrder] — the deck's previous id order (from sessionStorage), possibly stale.
 * @returns {string[]} the new deck order.
 */
export function buildDeckOrder(eligibleIds, priorOrder = []) {
  const eligible = new Set(eligibleIds);
  const kept = priorOrder.filter((id) => eligible.has(id));
  const keptSet = new Set(kept);
  const appended = eligibleIds.filter((id) => !keptSet.has(id));
  return [...kept, ...appended];
}

/**
 * Rotate the top card to the end of the order (the raw skip motion). A deck of 0 or 1
 * card is returned unchanged (nothing to rotate).
 *
 * @param {string[]} order
 * @returns {string[]}
 */
export function rotateToEnd(order) {
  if (order.length <= 1) return [...order];
  return [...order.slice(1), order[0]];
}

/**
 * Skip (S): rotate the top card to the end and record it on the skip stack so a later
 * Back (Z) can restore it. Skipping the only remaining card is a no-op (it never leaves
 * the top), so nothing is pushed.
 *
 * @param {string[]} order
 * @param {string[]} skipStack
 * @returns {{ order: string[], skipStack: string[] }}
 */
export function applySkip(order, skipStack) {
  if (order.length <= 1) return { order: [...order], skipStack: [...skipStack] };
  const skipped = order[0];
  return { order: rotateToEnd(order), skipStack: [...skipStack, skipped] };
}

/**
 * Back (Z) — verb-aware, scoped to UN-DOING A SKIP ONLY (design ruling q9zr.9: Confirm and
 * Reject commit immediately server-side and are final; there is no client inverse for them,
 * so Back cannot resurrect a resolved card). Pops the most-recently-skipped id and moves it
 * back to the front of the deck. It NEVER removes a card, NEVER confirms/rejects, and is a
 * no-op when the skip stack is empty. If the popped id is no longer in the deck (it left via
 * a server verb), it is simply discarded from the stack and the order is unchanged.
 *
 * @param {string[]} order
 * @param {string[]} skipStack
 * @returns {{ order: string[], skipStack: string[] }}
 */
export function applyBack(order, skipStack) {
  if (skipStack.length === 0) return { order: [...order], skipStack: [...skipStack] };
  const nextStack = skipStack.slice(0, -1);
  const restored = skipStack[skipStack.length - 1];
  if (!order.includes(restored)) {
    // The skipped card already left the deck (confirmed/rejected on a later card's re-render).
    return { order: [...order], skipStack: nextStack };
  }
  const without = order.filter((id) => id !== restored);
  return { order: [restored, ...without], skipStack: nextStack };
}

/**
 * Drop any skip-stack ids that are no longer in the deck (a skipped card was later confirmed
 * or rejected, so the server round-trip removed it). Keeps Back honest after a re-mount.
 *
 * @param {string[]} skipStack
 * @param {string[]} order
 * @returns {string[]}
 */
export function reconcileSkipStack(skipStack, order) {
  const present = new Set(order);
  return skipStack.filter((id) => present.has(id));
}

// ── Swipe geometry ─────────────────────────────────────────────────────────────

/**
 * The verb a pointer-drag of horizontal distance `dx` commits, or null while under
 * threshold (spring-back). Right past +threshold = confirm; left past −threshold = reject.
 * Exactly ±threshold is under the bar (springs back) — the card must move PAST it.
 *
 * @param {number} dx
 * @param {number} [threshold]
 * @returns {'confirm' | 'reject' | null}
 */
export function swipeVerb(dx, threshold = DECK_SWIPE_THRESHOLD) {
  if (dx > threshold) return "confirm";
  if (dx < -threshold) return "reject";
  return null;
}

/** @param {number} n @returns {number} */
function clamp01(n) {
  return Math.min(1, Math.max(0, n));
}

/**
 * Opacity (0..1) of the CONFIRM / REJECT swipe stamps, ramping in with drag distance so a
 * committing drag reaches full opacity exactly at the threshold.
 *
 * @param {number} dx
 * @param {number} [threshold]
 * @returns {{ confirm: number, reject: number }}
 */
export function stampOpacity(dx, threshold = DECK_SWIPE_THRESHOLD) {
  return { confirm: clamp01(dx / threshold), reject: clamp01(-dx / threshold) };
}

/**
 * The CSS transform for a card dragged `dx` px: translate + a slight rotation proportional
 * to the drag, for the physical "tossing a card" feel.
 *
 * @param {number} dx
 * @param {number} [rotateDivisor]
 * @returns {string}
 */
export function cardTransform(dx, rotateDivisor = 40) {
  return `translateX(${dx}px) rotate(${dx / rotateDivisor}deg)`;
}

// ── Progress (per-flyer high-water baseline) ────────────────────────────────────

/**
 * The deck-progress baseline: a per-flyer high-water mark of deck length. It only ever
 * grows within a session (new demotions can enlarge the deck), so the bar never regresses.
 *
 * @param {number} priorBase
 * @param {number} deckLen
 * @returns {number}
 */
export function nextBaseline(priorBase, deckLen) {
  return Math.max(priorBase || 0, deckLen);
}

/**
 * "N left" and the fill percentage for the deck-progress bar, measured against the
 * high-water baseline. A zero baseline yields 0% (nothing seeded yet).
 *
 * @param {number} deckLen
 * @param {number} baseline
 * @returns {{ left: number, percent: number }}
 */
export function deckProgress(deckLen, baseline) {
  const percent = baseline > 0 ? Math.round(((baseline - deckLen) / baseline) * 100) : 0;
  return { left: deckLen, percent };
}

// ── Escaping (untrusted flyer strings) ──────────────────────────────────────────

/**
 * HTML-escape a value for safe interpolation into the card's innerHTML. Raw flyer names
 * are untrusted (ACL quarantine, DD6) and reach the deck verbatim in the hydration payload,
 * so every interpolated string passes through here. Pure — tested alongside the geometry.
 *
 * @param {unknown} value
 * @returns {string}
 */
export function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>"']/g, (c) => {
    switch (c) {
      case "&": return "&amp;";
      case "<": return "&lt;";
      case ">": return "&gt;";
      case '"': return "&quot;";
      default: return "&#39;";
    }
  });
}
