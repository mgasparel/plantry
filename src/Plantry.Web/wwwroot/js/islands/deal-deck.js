// @ts-check
//
// Deals judgement-call deck — the FOURTH sanctioned island (ADR-020 amendment, bead
// plantry-q9zr.8). Mounts on the step-2 "Judgement calls" region of Deals Review and
// presents the pending Lows + demoted Highs one card at a time, with keyboard verbs and a
// pointer-drag swipe.
//
// ── Why an island here (ADR-020 §1 fourth-surface bar) ──────────────────────────
// This surface is not a draft-collection like the other three; it is a GESTURE surface.
// Continuous pointer-tracking (translate + rotate + stamp opacity written every
// pointermove), a client-owned rotating deck order, and a per-flyer high-water progress
// bar are behaviour-heavy and out-of-tree — beyond the page-Alpine budget (see the ADR-020
// amendment). Deliberately IMPERATIVE DOM, not Preact: driving a 60fps drag transform
// through a VDOM is the wrong tool; the card is a single node re-rendered only on
// skip/back. The tested pure transforms live in deal-deck-logic.js.
//
// ── ADR-020 §2 / §7 boundary ────────────────────────────────────────────────────
// The island owns PRESENTATION STATE ONLY: deck order, skip stack, drag, focus, and the
// progress high-water baseline (persisted per-flyer in sessionStorage so a server
// re-render preserves the skip order). It NEVER classifies, confirms, rejects, or corrects
// a deal itself. Every verb posts through the EXISTING htmx endpoints (Confirm / Reject /
// Correct) and the server re-renders #review-region from truth; this island then re-mounts
// and rebuilds the deck from the fresh classification. Server state never forks.
//
// ── Cache-busting convention (plantry-hxkf, mirrors take-stock.js) ──────────────
// The entry module is content-hash-versioned by the Razor IFileVersionProvider. Transitive
// imports are NOT independently versioned, so the ?v= query on ./deal-deck-logic.js is the
// manual cache key — bump it when that logic module changes.

import {
  buildDeckOrder,
  applySkip,
  applyBack,
  reconcileSkipStack,
  swipeVerb,
  stampOpacity,
  cardTransform,
  nextBaseline,
  deckProgress,
  escapeHtml,
  DECK_SWIPE_THRESHOLD,
} from "./deal-deck-logic.js?v=1";

// ── Types ───────────────────────────────────────────────────────────────────────

/**
 * One deck card, emitted by the server (DealDeckCardVm → camelCase JSON).
 * @typedef {Object} DealDeckCard
 * @property {string} dealId
 * @property {string} rawName                  verbatim ALL-CAPS flyer string (ACL quarantine, DD6)
 * @property {string} displayName              server title-cased name (DealReviewDisplay.TitleCase)
 * @property {string|null} brand
 * @property {string} price                    server-formatted currency string
 * @property {boolean} hasSuggestion
 * @property {string|null} suggestedProductName
 * @property {string|null} reasoning
 * @property {boolean} isNoise                 a $0.00 flyer-noise row (still individually rejectable)
 * @property {string} confirmUrl              htmx POST url (?handler=Confirm&dealId=&flyer=&step=2)
 * @property {string} rejectUrl               htmx POST url (?handler=Reject&dealId=&flyer=&step=2)
 */

/**
 * The deck hydration payload (DealDeckHydration → camelCase JSON).
 * @typedef {Object} DealDeckConfig
 * @property {string} flyerKey                 active flyer routing key (deck state is scoped per flyer)
 * @property {number} step                     the step number (2) — threaded for symmetry with the shell
 * @property {DealDeckCard[]} deals            the step-2 deals in server order
 */

// ── sessionStorage (presentation state survives the htmx re-render) ─────────────

const ORDER_KEY = "plantry.dealDeck.order.";
const BASE_KEY = "plantry.dealDeck.base.";
const SKIP_KEY = "plantry.dealDeck.skips.";

/** @param {string} key @returns {string[]} */
function readIds(key) {
  try {
    const raw = sessionStorage.getItem(key);
    const parsed = raw ? JSON.parse(raw) : [];
    return Array.isArray(parsed) ? parsed.map(String) : [];
  } catch {
    return [];
  }
}

/** @param {string} key @param {string[]} ids */
function writeIds(key, ids) {
  try {
    sessionStorage.setItem(key, JSON.stringify(ids));
  } catch {
    /* sessionStorage unavailable (private mode quota) — degrade to in-memory order only */
  }
}

/** @param {string} key @returns {number} */
function readNum(key) {
  try {
    const raw = sessionStorage.getItem(key);
    const n = raw ? Number(raw) : 0;
    return Number.isFinite(n) ? n : 0;
  } catch {
    return 0;
  }
}

/** @param {string} key @param {number} n */
function writeNum(key, n) {
  try {
    sessionStorage.setItem(key, String(n));
  } catch {
    /* ignore */
  }
}

// ── Global keyboard (one persistent listener, points at the active deck) ────────

/** @type {DeckController | null} */
let activeDeck = null;
let keydownInstalled = false;

function installKeydownOnce() {
  if (keydownInstalled) return;
  keydownInstalled = true;
  document.addEventListener("keydown", onKeydown);
}

/** @param {KeyboardEvent} e */
function onKeydown(e) {
  const deck = activeDeck;
  if (!deck) return;
  // Deck was swapped away (navigated to another step/flyer) — drop the stale reference.
  if (!document.body.contains(deck.mount)) {
    activeDeck = null;
    return;
  }
  // Yield while the shared Correct sheet is open, or while typing in any field.
  if (isSheetOpen()) return;
  const target = /** @type {HTMLElement} */ (e.target);
  if (target && target.closest && target.closest("input, textarea, select, [contenteditable]")) return;
  // Never hijack a key aimed at a focused button/link — let the native activation run.
  if (target && target.closest && target.closest("button, a")) return;

  const key = e.key;
  // Arrows are aliased to the primary verbs (→ confirm, ← reject).
  const verb =
    key === "Enter" || key === "ArrowRight" ? "confirm"
      : key === "ArrowLeft" ? "reject"
        : key === "x" || key === "X" ? "reject"
          : key === "m" || key === "M" ? "match"
            : key === "s" || key === "S" ? "skip"
              : key === "z" || key === "Z" ? "back"
                : null;
  if (!verb) return;
  e.preventDefault();
  deck.verb(verb);
}

/** The shared _ProductSearchCreateSheet is visible (x-show toggles display) when open. */
function isSheetOpen() {
  const sheet = /** @type {HTMLElement|null} */ (document.querySelector('.sheet[role="dialog"]'));
  return !!(sheet && sheet.offsetParent !== null);
}

// ── Controller ──────────────────────────────────────────────────────────────────

/**
 * @typedef {Object} DeckController
 * @property {Element} mount
 * @property {(verb: string) => void} verb
 */

/**
 * Mount the deck onto the step-2 region. Idempotent per node (guards a double-mount from the
 * initial htmx:load + the boot's manual sweep). Reads/persists deck order, skip stack, and the
 * progress high-water baseline in sessionStorage keyed by flyer, so a server re-render preserves
 * the client-side skip order.
 *
 * @param {Element} mount   the #judgement-deck element (its fallback card list is replaced)
 * @param {DealDeckConfig} config
 */
export function mountDealDeck(mount, config) {
  if (!(mount instanceof HTMLElement)) return;
  if (mount.dataset.deckMounted === "1") return;
  mount.dataset.deckMounted = "1";

  const cards = Array.isArray(config?.deals) ? config.deals : [];
  if (cards.length === 0) return; // server renders the empty-state; nothing to mount

  const flyerKey = config.flyerKey || "_";
  const orderKey = ORDER_KEY + flyerKey;
  const baseKey = BASE_KEY + flyerKey;
  const skipKey = SKIP_KEY + flyerKey;

  /** @type {Map<string, DealDeckCard>} */
  const byId = new Map(cards.map((c) => [c.dealId, c]));
  const eligibleIds = cards.map((c) => c.dealId);

  // Rebuild the deck order from tier truth, preserving any prior skip-rotation order.
  let order = buildDeckOrder(eligibleIds, readIds(orderKey));
  let skipStack = reconcileSkipStack(readIds(skipKey), order);
  let baseline = nextBaseline(readNum(baseKey), order.length);

  persist();

  function persist() {
    writeIds(orderKey, order);
    writeIds(skipKey, skipStack);
    writeNum(baseKey, baseline);
  }

  /**
   * Post a verb through the EXISTING htmx endpoint; the server re-renders #review-region from
   * truth and the island re-mounts. htmx-antiforgery.js attaches the token on configRequest.
   * @param {string} url
   */
  function postVerb(url) {
    const h = /** @type {any} */ (window).htmx;
    if (h && typeof h.ajax === "function") {
      h.ajax("POST", url, { target: "#review-region", swap: "innerHTML" });
    }
  }

  /** @param {DealDeckCard} card */
  function openMatchSheet(card) {
    // Reuse the persistent host's openCorrect via the bubbling deal-correct event (the same
    // affordance the server-rendered card's Correct button dispatches). The host reads the deal
    // context from THIS card's DOM (data-deal-id + the deal-review-row__* selectors below).
    const el = mount.querySelector(`[data-deal-id="${cssEscape(card.dealId)}"]`);
    (el || mount).dispatchEvent(
      new CustomEvent("deal-correct", { bubbles: true, detail: { dealId: card.dealId } }),
    );
  }

  /** @param {string} verb */
  function runVerb(verb) {
    const card = byId.get(order[0]);
    if (!card) return;
    switch (verb) {
      case "confirm":
        // A None/noise deal has no server suggestion to accept — Confirm is not offered for it.
        if (card.hasSuggestion) postVerb(card.confirmUrl);
        return;
      case "reject":
        postVerb(card.rejectUrl);
        return;
      case "match":
        openMatchSheet(card);
        return;
      case "skip": {
        const next = applySkip(order, skipStack);
        order = next.order;
        skipStack = next.skipStack;
        baseline = nextBaseline(baseline, order.length);
        persist();
        renderCard();
        return;
      }
      case "back": {
        const next = applyBack(order, skipStack);
        order = next.order;
        skipStack = next.skipStack;
        persist();
        renderCard();
        return;
      }
      default:
        return;
    }
  }

  function renderCard() {
    const card = byId.get(order[0]);
    if (!card) {
      // Deck emptied by client rotation only (shouldn't happen — server owns removal), guard anyway.
      mount.innerHTML = "";
      return;
    }
    const { left, percent } = deckProgress(order.length, baseline);
    const confirmable = card.hasSuggestion;

    mount.innerHTML = `
      <div class="deck-progress">
        <div class="bar"><div class="fill" style="width:${percent}%"></div></div>
        <span class="label">${left} left</span>
      </div>
      <div class="deck-zone">
        <section class="card focus-card${card.isNoise ? " focus-card--noise" : ""}" data-deal-id="${escapeHtml(card.dealId)}">
          <span class="swipe-hint swipe-hint--confirm" data-hint="confirm">&#10003; Confirm</span>
          <span class="swipe-hint swipe-hint--reject" data-hint="reject">&#10005; Reject</span>

          <div class="focus-card__flyer">
            <div class="focus-card__src">From the flyer</div>
            <div class="focus-card__raw-name">
              <span class="deal-review-row__name" title="${escapeHtml(card.rawName)}">${escapeHtml(card.displayName)}</span>
            </div>
            <div class="focus-card__meta">
              ${card.brand ? `<span class="deal-review-row__brand">${escapeHtml(card.brand)}</span>` : ""}
              <span class="deal-row__amount">${escapeHtml(card.price)}</span>
            </div>
            ${card.isNoise ? `<div class="focus-card__noise"><svg class="icon" aria-hidden="true"><use href="#i-alert" /></svg> Flyer noise — no usable price</div>` : ""}
          </div>

          ${confirmable ? `
          <div class="focus-card__link">Plantry thinks this is</div>
          <div class="focus-card__match">
            <div class="focus-card__src">Your catalog</div>
            <div class="focus-card__product">${escapeHtml(card.suggestedProductName ?? "")}</div>
            ${card.reasoning ? `<div class="focus-card__reasoning">${escapeHtml(card.reasoning)}</div>` : ""}
          </div>` : `
          <div class="focus-card__link">No catalog match</div>
          <div class="focus-card__match focus-card__match--none">
            <div class="focus-card__reasoning">Plantry didn’t find a product for this line. Match it yourself, or reject it.</div>
          </div>`}

          <div class="focus-verbs">
            <button type="button" class="btn btn--ghost btn--sm focus-verbs__reject" data-verb="reject">Reject</button>
            <button type="button" class="btn btn--secondary btn--sm" data-verb="match">
              <svg class="icon" aria-hidden="true"><use href="#i-search" /></svg> Change match
            </button>
            ${confirmable ? `
            <button type="button" class="btn btn--primary btn--sm focus-verbs__confirm" data-verb="confirm">
              <svg class="icon" aria-hidden="true"><use href="#i-check" /></svg> Confirm
            </button>` : ""}
          </div>

          <div class="focus-under">
            <button type="button" data-verb="back"${skipStack.length ? "" : " disabled"}>← Undo skip</button>
            <button type="button" data-verb="skip">Skip for now →</button>
          </div>
        </section>
        <div class="swipe-tip">Swipe right to confirm · left to reject</div>
      </div>`;

    mount.querySelectorAll("[data-verb]").forEach((b) => {
      b.addEventListener("click", () => runVerb(/** @type {HTMLElement} */ (b).dataset.verb || ""));
    });

    wireSwipe(/** @type {HTMLElement} */ (mount.querySelector(".focus-card")));
  }

  /** Pointer-drag swipe. Threshold DECK_SWIPE_THRESHOLD; buttons/links excluded from the drag start. */
  function wireSwipe(cardEl) {
    if (!cardEl) return;
    const hintConfirm = /** @type {HTMLElement|null} */ (cardEl.querySelector('[data-hint="confirm"]'));
    const hintReject = /** @type {HTMLElement|null} */ (cardEl.querySelector('[data-hint="reject"]'));
    let startX = /** @type {number|null} */ (null);
    let dx = 0;

    cardEl.addEventListener("pointerdown", (e) => {
      const t = /** @type {HTMLElement} */ (e.target);
      if (t.closest && t.closest("button, a")) return; // never start a drag from a control
      startX = e.clientX;
      dx = 0;
      cardEl.classList.add("dragging");
      try {
        cardEl.setPointerCapture(e.pointerId);
      } catch {
        /* pointer capture may fail on synthetic events — drag still works via move/up */
      }
    });

    cardEl.addEventListener("pointermove", (e) => {
      if (startX === null) return;
      dx = e.clientX - startX;
      cardEl.style.transform = cardTransform(dx);
      const o = stampOpacity(dx);
      if (hintConfirm) hintConfirm.style.opacity = String(o.confirm);
      if (hintReject) hintReject.style.opacity = String(o.reject);
    });

    const end = () => {
      if (startX === null) return;
      startX = null;
      cardEl.classList.remove("dragging");
      const verb = swipeVerb(dx, DECK_SWIPE_THRESHOLD);
      // Only commit (and leave the card off-screen for the re-render) when the verb will actually act:
      // reject always acts; confirm acts only on a card that HAS a suggestion (no-suggestion cards offer
      // no Confirm). A right-swipe on a no-suggestion card must spring back, not strand off-screen.
      const top = byId.get(order[0]);
      const commits = verb === "reject" || (verb === "confirm" && !!top && top.hasSuggestion);
      if (commits) {
        runVerb(/** @type {string} */ (verb)); // confirm/reject commit through htmx; the region re-renders
        return;
      }
      // Under threshold (or a non-committing swipe) — spring back to rest.
      cardEl.classList.add("springing");
      cardEl.style.transform = "";
      if (hintConfirm) hintConfirm.style.opacity = "0";
      if (hintReject) hintReject.style.opacity = "0";
      setTimeout(() => cardEl.classList.remove("springing"), 200);
    };
    cardEl.addEventListener("pointerup", end);
    cardEl.addEventListener("pointercancel", end);
  }

  renderCard();

  // Point the persistent keyboard handler at this deck and render the desktop kbd hint bar.
  activeDeck = { mount, verb: runVerb };
  installKeydownOnce();
  ensureKbdBar();
}

// ── Desktop keyboard-hint bar (composed from the shared .kbd-bar primitive) ──────

function ensureKbdBar() {
  if (document.getElementById("deal-deck-kbd-bar")) return;
  const bar = document.createElement("div");
  bar.id = "deal-deck-kbd-bar";
  bar.className = "kbd-bar";
  bar.innerHTML =
    "<span><kbd>Enter</kbd>/<kbd>→</kbd> confirm</span>" +
    "<span><kbd>M</kbd> change match</span>" +
    "<span><kbd>X</kbd>/<kbd>←</kbd> reject</span>" +
    "<span><kbd>S</kbd> skip</span>" +
    "<span><kbd>Z</kbd> undo skip</span>";
  document.body.appendChild(bar);
}

/**
 * Tear down the deck's page-level chrome when a swap leaves step 2 (auto-advance, flyer done,
 * or navigation to step 1/3). The boot calls this on any #review-region swap that no longer
 * carries a deck mount. The deck card itself is removed by htmx's innerHTML swap; this drops
 * the fixed kbd-bar and clears the keyboard target so no stale card can be acted on.
 */
export function teardownDealDeck() {
  activeDeck = null;
  const bar = document.getElementById("deal-deck-kbd-bar");
  if (bar) bar.remove();
}

/** Minimal CSS.escape fallback for the attribute selector (deal ids are GUIDs, but be safe). */
function cssEscape(value) {
  const g = /** @type {any} */ (window);
  if (g.CSS && typeof g.CSS.escape === "function") return g.CSS.escape(value);
  return String(value).replace(/["\\\]]/g, "\\$&");
}
