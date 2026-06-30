// @ts-check
//
// Meal Planner island (ADR-020, bead plantry-2zvm.4).
//
// Buildless Preact + htm + signals. Replaces the Alpine mealEditor component
// and the addDishFromResult DOM-walk bridge. plan-tune.js (planning-weights
// popover) remains Alpine — it is a self-contained grid-level control, not
// part of the editor.
//
// ADR-020 §2 boundary:
//   SERVER: domain rules (fulfillment, cost, assign/clear persistence, rollup projection).
//   ISLAND: UI/draft state (dishes, mode, note, att, canSave), derived display
//           state (dishMeta formatting). Rollup (fulfillment %/cost) is a SERVER
//           PROJECTION — ADR-013 §4/§5 and ADR-020 §2/§7. The island posts the
//           draft dish list to the rollup JSON endpoint and renders the returned
//           HTML fragment. There is NO client-side rollup formula.
//
// ADR-020 §7 tripwire: fulfillment/cost MUST stay server-side. The island holds
//   only { dishes, servings, mode, note, att, attOverridden } + derived canSave.
//
// Bug classes eliminated vs the previous Alpine mealEditor component:
//   - "Object.fromEntries collapses repeated keys" (dish list now in JSON array)
//   - "_x_dataStack[0] DOM-walk" bridge (dish search is in-component)
//   - "Alpine var-token silently fails" (no Alpine variable scoping)
//   - debounced htmx.swap fragility (rollup is a direct postJson call)

// ── Cache-busting convention (plantry-hxkf) ───────────────────────────────────
//
// The server (MealPlan/Index.cshtml) versions this entry module via IFileVersionProvider,
// which appends a content-hash query to this file's URL. Transitive imports of
// runtime.js and meal-planner-logic.js are NOT independently versioned by the Razor
// layer — if only a transitive file changes, its URL stays the same and browsers
// serve a stale cached version.
//
// FIX: the ?v= query strings on the import specifiers below ARE the versioning
// mechanism. Changing the query changes the URL the browser uses as a cache key,
// which forces a re-fetch of that module. The content-hash approach (used on this
// file and helpers.js by Razor) cannot be extended to relative specifiers resolved
// inside a JS module — the only option here is a manual version token in the URL.
//
// CONVENTION — when to bump each ?v= query:
//   ./runtime.js?v=N           bump when runtime.js changes (Preact/htm/signals re-exports)
//   ./meal-planner-logic.js?v=N  bump when meal-planner-logic.js changes
//   ./helpers.js is imported directly by MealPlan/Index.cshtml with FileVersionProvider,
//   so it gets a content-hash automatically — no manual token needed here.
//
// The convention ensures that a logic-only change (e.g. meal-planner-logic.js) is
// caught by bumping the ?v= query, which changes this file's bytes, which changes
// the entry-module content hash, which causes the full dependency graph to reload.

import { render, html, signal, computed, effect, useSignal, useComputed, useRef } from "./runtime.js?v=1";
import { readAntiforgeryToken, postJson } from "./helpers.js";
import { lvl, money, dishMeta } from "./meal-planner-logic.js?v=1";

// ── Type documentation ────────────────────────────────────────────────────────

/**
 * @typedef {Object} DishDraft
 * @property {"recipe"|"product"} kind
 * @property {string} itemId
 * @property {string} name
 * @property {number} servings
 * @property {number|null} fulfillment       per-dish fulfillment % from server (display-only)
 * @property {number|null} costPerServing    per-dish cost from server (display-only)
 * @property {boolean} hasPhoto
 */

/**
 * @typedef {Object} EditorState
 * @property {string} dateStr
 * @property {string} slotIdStr
 * @property {string} slotLabel
 * @property {string|null} mealId     GUID string when editing, null when adding
 * @property {boolean} isEditing
 * @property {"dishes"|"note"} mode
 * @property {string} note
 * @property {DishDraft[]} dishes
 * @property {string[]} att           user IDs of effective attendees
 * @property {string[]} defaultAtt    slot default attendee IDs
 * @property {boolean} attOverridden
 * @property {string|null} initialRollupHtml  server-rendered initial rollup for existing meal
 * @property {string} dateDowLabel    e.g. "Mon" — editor header day-of-week
 * @property {string} dateMonthDay    e.g. "Jun 1" — editor header date
 * @property {boolean} isToday
 */

/**
 * @typedef {Object} MemberInfo
 * @property {string} userId
 * @property {string} displayName
 * @property {string} initials
 * @property {number} colorIndex
 */

/**
 * @typedef {Object} IslandHydration
 * @property {string} assignUrl
 * @property {string} clearUrl
 * @property {string} rollupUrl
 * @property {string} editorJsonUrl
 * @property {string} searchJsonUrl
 * @property {MemberInfo[]} members
 */

/**
 * @typedef {Object} SearchResultDish
 * @property {"recipe"|"product"} kind
 * @property {string} itemId
 * @property {string} name
 * @property {number} defaultServings
 * @property {number|null} fulfillmentPercent
 * @property {number|null} costPerServing
 * @property {boolean} hasPhoto
 * @property {string|null} photoUrl
 */

/**
 * @typedef {Object} CellMutationResult
 * @property {string} cellHtml
 * @property {string} railHtml
 * @property {string} barNavHtml
 * @property {string|null} error
 */

// ── Helpers ───────────────────────────────────────────────────────────────────
// lvl, money, dishMeta are imported from ./meal-planner-logic.js (bead plantry-2zvm.12).

/**
 * Apply a cell mutation result to the live DOM.
 * The island POST endpoints (AssignJson/ClearJson) return JSON carrying
 * the updated cell HTML + rail HTML + barNav HTML as rendered strings.
 * The island swaps these fragments into the live DOM directly, preserving
 * the ADR-013 OOB contract (rail recomputes on every cell mutation).
 * @param {CellMutationResult} result
 */
function applyMutationResult(result) {
  // 1. Swap the updated cell (outerHTML swap by id)
  const cellIdMatch = result.cellHtml.match(/id="(cell-[^"]+)"/);
  if (cellIdMatch) {
    const cellEl = document.getElementById(cellIdMatch[1]);
    if (cellEl) {
      const tmp = document.createElement("div");
      tmp.innerHTML = result.cellHtml;
      const newCell = /** @type {Element} */ (tmp.firstElementChild);
      if (newCell) {
        cellEl.replaceWith(newCell);
        if (typeof htmx !== "undefined") htmx.process(newCell);
      }
    }
  }

  // 2. Swap #plan-rail (OOB — ADR-013 invariant: rail recomputes on every mutation)
  if (result.railHtml) {
    const railEl = document.getElementById("plan-rail");
    if (railEl) {
      const tmp = document.createElement("div");
      tmp.innerHTML = result.railHtml;
      const newRail = /** @type {Element} */ (tmp.firstElementChild);
      if (newRail) {
        railEl.replaceWith(newRail);
        if (typeof htmx !== "undefined") htmx.process(newRail);
      }
    }
    // The reopen tab lives in barNavHtml (plan-rail-reopen) and is swapped below.
  }

  // 3. Swap barNav elements (plan-bar-nav, plan-cost-chip, plan-bar-autofill, plan-rail-reopen)
  if (result.barNavHtml) {
    const tmp = document.createElement("div");
    tmp.innerHTML = result.barNavHtml;
    for (const el of Array.from(tmp.children)) {
      const id = el.id;
      if (id) {
        const existing = document.getElementById(id);
        if (existing) {
          existing.replaceWith(el);
          if (typeof htmx !== "undefined") htmx.process(el);
          // Re-initialise Alpine on replaced elements (plan-bar-autofill contains x-data)
          if (typeof Alpine !== "undefined") Alpine.initTree(el);
        }
      }
    }
  }
}

// ── DishSearch component ──────────────────────────────────────────────────────

/**
 * In-component dish search — replaces the out-of-tree addDishFromResult bridge.
 * Calls the SearchJson endpoint and renders results reactively via signals.
 * @param {{
 *   slotIdStr: string,
 *   searchJsonUrl: string,
 *   onAdd: (dish: DishDraft) => void,
 * }} props
 */
function DishSearch({ slotIdStr, searchJsonUrl, onAdd }) {
  // useSignal ensures signals persist across re-renders (stable across the component's lifetime).
  // signal() recreates on every render and loses state — useSignal() is the hook-based equivalent.
  const query = useSignal("");
  const results = useSignal(/** @type {SearchResultDish[]} */ ([]));
  const isOpen = useSignal(false);
  // useRef persists the debounce timer without triggering re-renders.
  const debounceTimerRef = useRef(/** @type {ReturnType<typeof setTimeout>|null} */ (null));

  async function search(/** @type {string} */ q) {
    if (!q.trim()) {
      results.value = [];
      return;
    }
    try {
      const url = `${searchJsonUrl}&q=${encodeURIComponent(q)}`;
      const resp = await fetch(url, { headers: { "X-Requested-With": "XMLHttpRequest" } });
      if (resp.ok) {
        const data = await resp.json();
        results.value = data.hits ?? [];
      }
    } catch {
      // Search failure is non-fatal; results stay stale
    }
  }

  function onInput(/** @type {string} */ v) {
    query.value = v;
    isOpen.value = v.length > 0;
    if (debounceTimerRef.current) clearTimeout(debounceTimerRef.current);
    debounceTimerRef.current = setTimeout(() => search(v), 200);
  }

  function addResult(/** @type {SearchResultDish} */ r) {
    onAdd({
      kind: r.kind,
      itemId: r.itemId,
      name: r.name,
      servings: r.defaultServings,
      fulfillment: r.fulfillmentPercent,
      costPerServing: r.costPerServing,
      hasPhoto: r.hasPhoto,
    });
    query.value = "";
    results.value = [];
    isOpen.value = false;
  }

  const recipeHits = useComputed(() => results.value.filter((r) => r.kind === "recipe"));
  const productHits = useComputed(() => results.value.filter((r) => r.kind === "product"));
  const hasResults = useComputed(() => results.value.length > 0);

  return html`
    <div class="dish-search">
      <div class="ds-input">
        <svg class="icon" aria-hidden="true"><use href="#i-search" /></svg>
        <input type="text"
               id=${"dish-search-input-" + slotIdStr}
               value=${query}
               placeholder="Search recipes or pantry items to add…"
               autocomplete="off"
               onFocus=${() => { if (query.value.length > 0) isOpen.value = true; }}
               onBlur=${() => { setTimeout(() => { isOpen.value = false; }, 200); }}
               onInput=${(/** @type {InputEvent} */ e) =>
                 onInput(/** @type {HTMLInputElement} */ (e.target).value)} />
      </div>
      ${isOpen.value && hasResults.value && html`
        <div class="dish-menu" id=${"dish-results-" + slotIdStr}>
          ${recipeHits.value.length > 0 && html`<div class="dm-group">Recipes</div>`}
          ${recipeHits.value.map((r) => {
            const initial = r.name.charAt(0) || "?";
            return html`
              <div key=${r.itemId} class="dish-opt"
                   onMouseDown=${(/** @type {MouseEvent} */ e) => { e.preventDefault(); addResult(r); }}>
                ${r.hasPhoto && r.photoUrl
                  ? html`<img class="do-thumb" src=${r.photoUrl} alt=${r.name} />`
                  : html`<div class="do-thumb do-thumb--chip">${initial}</div>`}
                <div class="do-main">
                  <div class="do-name">${r.name}</div>
                  <div class="do-sub">
                    ${r.fulfillmentPercent != null
                      ? html`
                          <span class=${"fdot lvl-" + lvl(r.fulfillmentPercent) + "-bg"}></span>
                          <span>${r.fulfillmentPercent}% in pantry · ${r.defaultServings} srv${
                            r.costPerServing != null
                              ? " · " + money(r.costPerServing * r.defaultServings)
                              : ""
                          }</span>`
                      : html`<span>${r.defaultServings} srv</span>`}
                  </div>
                </div>
              </div>`;
          })}
          ${productHits.value.length > 0 && html`<div class="dm-group">Pantry items</div>`}
          ${productHits.value.map((r) => {
            const initial = r.name.charAt(0) || "?";
            return html`
              <div key=${r.itemId} class="dish-opt"
                   onMouseDown=${(/** @type {MouseEvent} */ e) => { e.preventDefault(); addResult(r); }}>
                <div class="do-thumb do-thumb--chip">${initial}</div>
                <div class="do-main">
                  <div class="do-name">${r.name}</div>
                  <div class="do-sub">Pantry item</div>
                </div>
              </div>`;
          })}
          ${!hasResults.value && query.value.length > 0 && html`
            <div style="padding:12px 8px;color:var(--color-text-faint);font-size:13px;">
              No results for "${query.value}"
            </div>`}
        </div>
      `}
    </div>
  `;
}

// ── MealEditor component ──────────────────────────────────────────────────────

/**
 * The reactive meal editor — owns draft state (dishes, mode, note, att).
 * Rollup is a server projection (ADR-013 §4/§5, ADR-020 §7).
 * @param {{
 *   state: EditorState,
 *   members: MemberInfo[],
 *   token: string,
 *   assignUrl: string,
 *   clearUrl: string,
 *   rollupUrl: string,
 *   searchJsonUrl: string,
 *   onClose: () => void,
 *   onMutated: (result: CellMutationResult) => void,
 * }} props
 */
function MealEditor({ state, members, token, assignUrl, clearUrl, rollupUrl, searchJsonUrl, onClose, onMutated }) {
  // ── Draft signals ──────────────────────────────────────────────────────────
  // useSignal() ensures signals are created once per component lifetime and persist
  // across re-renders. signal() inside a function body is re-created every render,
  // losing all state changes (e.g. mode switch, dish additions).
  const mode = useSignal(state.mode);
  const note = useSignal(state.note);
  const dishes = useSignal(/** @type {DishDraft[]} */ ([...state.dishes]));
  const att = useSignal([...state.att]);
  const defaultAtt = state.defaultAtt;
  const attOverridden = useSignal(state.attOverridden);
  const saving = useSignal(false);
  const errorMsg = useSignal(/** @type {string|null} */ (null));

  // ── Rollup (server projection, ADR-013 §4/§5) ─────────────────────────────
  // Initial HTML is the server-computed rollup embedded in the EditorJson response.
  const rollupHtml = useSignal(
    state.initialRollupHtml ??
    "<div class=\"err\"><div class=\"err-l\">Add a dish to see fulfillment &amp; cost.</div></div>"
  );
  // useRef persists the rollup debounce timer without triggering re-renders.
  const rollupTimerRef = useRef(/** @type {ReturnType<typeof setTimeout>|null} */ (null));

  function requestRollup() {
    if (rollupTimerRef.current) clearTimeout(rollupTimerRef.current);
    rollupTimerRef.current = setTimeout(async () => {
      rollupTimerRef.current = null;
      const body = {
        mode: mode.value,
        dishes: mode.value === "note" ? [] : dishes.value.map((d) => ({
          kind: d.kind,
          itemId: d.itemId,
          servings: d.servings,
        })),
      };
      try {
        const resp = await postJson(rollupUrl, body, token);
        if (resp.ok) {
          const data = await resp.json();
          if (typeof data.html === "string") rollupHtml.value = data.html;
        }
      } catch {
        // Rollup failure is non-fatal — footer stays stale but editor is still usable
      }
    }, 300);
  }

  // ── Derived ────────────────────────────────────────────────────────────────
  const canSave = useComputed(() =>
    mode.value === "note" ? note.value.trim().length > 0 : dishes.value.length > 0
  );

  // ── Attendee toggle ────────────────────────────────────────────────────────
  function toggleAtt(/** @type {string} */ uid) {
    const cur = att.value;
    const next = cur.includes(uid) ? cur.filter((x) => x !== uid) : [...cur, uid];
    att.value = next;
    const attSorted = [...next].sort().join(",");
    const defSorted = [...defaultAtt].sort().join(",");
    attOverridden.value = attSorted !== defSorted;
  }

  // ── Dish mutations ─────────────────────────────────────────────────────────
  function addDish(/** @type {DishDraft} */ dish) {
    dishes.value = [...dishes.value, dish];
    requestRollup();
  }

  function removeDish(/** @type {number} */ idx) {
    dishes.value = dishes.value.filter((_, i) => i !== idx);
    requestRollup();
  }

  function incServings(/** @type {DishDraft} */ d) {
    dishes.value = dishes.value.map((x) =>
      x === d ? { ...x, servings: x.servings + 1 } : x
    );
    requestRollup();
  }

  function decServings(/** @type {DishDraft} */ d) {
    dishes.value = dishes.value.map((x) =>
      x === d ? { ...x, servings: Math.max(1, x.servings - 1) } : x
    );
    requestRollup();
  }

  function switchToNote() {
    mode.value = "note";
    dishes.value = [];
    requestRollup();
  }

  function switchToDishes() {
    mode.value = "dishes";
    note.value = "";
    requestRollup();
  }

  // ── Save (Assign) ──────────────────────────────────────────────────────────
  async function save() {
    if (saving.value || !canSave.value) return;
    saving.value = true;
    errorMsg.value = null;
    try {
      const body = {
        mode: mode.value,
        note: mode.value === "note" ? note.value : null,
        dishes: mode.value === "note" ? [] : dishes.value.map((d) => ({
          kind: d.kind,
          itemId: d.itemId,
          servings: d.servings,
        })),
        att: attOverridden.value ? att.value : null,
        attendeesOverridden: attOverridden.value,
        mealId: state.mealId ?? null,
        date: state.dateStr,
        slotId: state.slotIdStr,
      };
      const resp = await postJson(assignUrl, body, token);
      const data = /** @type {CellMutationResult} */ (await resp.json());
      if (!resp.ok || data.error) {
        errorMsg.value = data.error ?? `Save failed (${resp.status})`;
        return;
      }
      onClose();
      onMutated(data);
    } catch {
      errorMsg.value = "Network error — please try again.";
    } finally {
      saving.value = false;
    }
  }

  // ── Clear (Remove meal) ────────────────────────────────────────────────────
  async function clearMeal() {
    if (!state.mealId || saving.value) return;
    saving.value = true;
    errorMsg.value = null;
    try {
      const body = { date: state.dateStr, slotId: state.slotIdStr, mealId: state.mealId };
      const resp = await postJson(clearUrl, body, token);
      const data = /** @type {CellMutationResult} */ (await resp.json());
      if (!resp.ok || data.error) {
        errorMsg.value = data.error ?? `Clear failed (${resp.status})`;
        return;
      }
      onClose();
      onMutated(data);
    } catch {
      errorMsg.value = "Network error — please try again.";
    } finally {
      saving.value = false;
    }
  }

  const notePresets = ["Takeout", "Out of town", "Leftovers", "Dining out", "Skip this meal"];

  return html`
    <div id="meal-editor-inner">

      <div class="ed-head">
        <div class="eh-slot">
          <svg class="icon" aria-hidden="true"><use href="#i-clock" /></svg>
        </div>
        <div>
          <h2>${state.slotLabel}</h2>
          <div class="eh-sub">${state.dateDowLabel}, ${state.dateMonthDay}${state.isToday ? " · Today" : ""}</div>
        </div>
        <span class="spacer"></span>
        <button class="ed-close" type="button" onClick=${onClose} aria-label="Close">
          <svg class="icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
            <path d="M18 6 6 18M6 6l12 12" />
          </svg>
        </button>
      </div>

      <div class="ed-body">

        <div class="ed-sect">
          <div class="ed-sect-head">
            <span class="es-label">Who's eating</span>
            <span class="spacer"></span>
            ${attOverridden.value && html`<span class="ed-att-note">overrides slot default</span>`}
          </div>
          <div class="ed-att">
            ${members.map((m) => html`
              <button key=${m.userId} type="button"
                      class=${"ed-att-chip" + (att.value.includes(m.userId) ? "" : " off")}
                      onClick=${() => toggleAtt(m.userId)}
                      aria-label=${"Toggle " + m.displayName}>
                <span class="pl-av" data-av-color=${m.colorIndex}
                      style="width:22px;height:22px;font-size:9px;">${m.initials}</span>
                ${m.displayName}
              </button>
            `)}
          </div>
        </div>

        ${mode.value === "dishes" && html`
          <div class="ed-sect">
            <div class="ed-sect-head">
              <span class="es-label">Dishes</span>
              <span class="spacer"></span>
              <div class="ed-note-toggle">or${" "}<button type="button" onClick=${switchToNote}>add a note instead</button></div>
            </div>

            ${dishes.value.length > 0 && html`
              <div class="ed-dishes">
                ${dishes.value.map((d, idx) => html`
                  <div key=${idx} class="ed-dish">
                    ${d.hasPhoto && d.kind === "recipe"
                      ? html`<img class="edd-thumb edd-thumb--photo"
                                  src=${"/Recipes/Details?id=" + d.itemId + "&handler=Photo"}
                                  alt=${d.name} />`
                      : html`<div class="edd-thumb edd-thumb--chip">${d.name.charAt(0)}</div>`}
                    <div class="edd-main">
                      <div class="edd-name">${d.name}</div>
                      <div class="edd-meta">
                        ${d.fulfillment != null && html`
                          <span class=${"fdot lvl-" + lvl(d.fulfillment) + "-bg"}></span>
                        `}
                        <span>${dishMeta(d)}</span>
                      </div>
                    </div>
                    <div class="serv-step">
                      <button type="button" onClick=${() => decServings(d)} aria-label="Fewer">−</button>
                      <span class="sv"><span>${d.servings}</span><small>serv</small></span>
                      <button type="button" onClick=${() => incServings(d)} aria-label="More">+</button>
                    </div>
                    <button type="button" class="edd-del" onClick=${() => removeDish(idx)} aria-label="Remove dish">
                      <svg class="icon" aria-hidden="true"><use href="#i-trash" /></svg>
                    </button>
                  </div>
                `)}
              </div>
            `}

            <${DishSearch}
              slotIdStr=${state.slotIdStr}
              searchJsonUrl=${searchJsonUrl}
              onAdd=${addDish} />
          </div>
        `}

        ${mode.value === "note" && html`
          <div class="ed-sect">
            <div class="ed-sect-head">
              <span class="es-label">Note</span>
              <span class="spacer"></span>
              <div class="ed-note-toggle">or${" "}<button type="button" onClick=${switchToDishes}>plan dishes instead</button></div>
            </div>
            <div class="ed-note-field">
              <input type="text" value=${note}
                     placeholder="e.g. Takeout, Out of town…"
                     onInput=${(/** @type {InputEvent} */ e) => {
                       note.value = /** @type {HTMLInputElement} */ (e.target).value;
                     }} />
            </div>
            <div class="ed-note-chips">
              ${notePresets.map((p) => html`
                <button key=${p} type="button" onClick=${() => { note.value = p; }}>${p}</button>
              `)}
            </div>
          </div>
        `}

      </div>

      <div class="ed-foot">
        ${/* Rollup container — server projection (ADR-013 §4/§5, ADR-020 §7).
             dangerouslySetInnerHTML because the rollup is an HTML fragment rendered
             server-side by _EditorRollup.cshtml (the same Razor partial the old Alpine
             component swapped via htmx). The island fetches it via postJson → RollupJson. */ null}
        <div class="ed-rollup" id=${"ed-rollup-" + state.slotIdStr}
             dangerouslySetInnerHTML=${{ __html: rollupHtml.value }}></div>
        <span class="spacer"></span>

        ${state.isEditing && state.mealId && html`
          <button type="button" class="txt-btn danger"
                  disabled=${saving.value}
                  onClick=${clearMeal}>
            Remove meal
          </button>
        `}

        <button type="button" class="btn btn--secondary" onClick=${onClose}>Cancel</button>

        <button type="button" class="btn btn--primary"
                disabled=${!canSave.value || saving.value}
                onClick=${save}>
          ${saving.value ? "Saving…" : "Save meal"}
        </button>

        ${errorMsg.value && html`
          <div role="alert"
               style="color:var(--color-danger);font-size:13px;width:100%;text-align:right;margin-top:4px;">
            ${errorMsg.value}
          </div>
        `}
      </div>
    </div>
  `;
}

// ── App (modal veil manager) ──────────────────────────────────────────────────

/**
 * Root component — renders the modal veil + editor when open, nothing when closed.
 * Signal access inside the render function makes Preact subscribe automatically.
 * @param {{
 *   modalOpen: import("@preact/signals").Signal<boolean>,
 *   editorState: import("@preact/signals").Signal<EditorState|null>,
 *   members: MemberInfo[],
 *   tokenSig: import("@preact/signals").Signal<string>,
 *   hydration: IslandHydration,
 *   onClose: () => void,
 *   onMutated: (result: CellMutationResult) => void,
 * }} props
 */
function App({ modalOpen, editorState, members, tokenSig, hydration, onClose, onMutated }) {
  if (!modalOpen.value || !editorState.value) return null;

  return html`
    <div class="modal-veil" id="meal-editor-modal" style="display:grid"
         onClick=${(/** @type {MouseEvent} */ e) => {
           if (e.target === e.currentTarget) onClose();
         }}>
      <div class="editor" role="dialog" aria-modal="true" id="meal-editor-dialog">
        <${MealEditor}
          state=${editorState.value}
          members=${members}
          token=${tokenSig.value}
          assignUrl=${hydration.assignUrl}
          clearUrl=${hydration.clearUrl}
          rollupUrl=${hydration.rollupUrl}
          searchJsonUrl=${hydration.searchJsonUrl}
          onClose=${onClose}
          onMutated=${onMutated} />
      </div>
    </div>
  `;
}

// ── Mount ─────────────────────────────────────────────────────────────────────

/**
 * Mount the meal planner island. The island manages the editor modal; the grid
 * cells remain server-rendered (htmx). Cell onclick handlers call the global
 * bridge `window.__mealPlannerIsland.openEditor(date, slotId, mealId)`.
 *
 * @param {Element} root           — the island mount point (<div id="meal-planner-island-root">)
 * @param {IslandHydration} hydration — from readHydration('meal-planner-island-data')
 */
export function mountMealPlanner(root, hydration) {
  const tokenSig = signal(readAntiforgeryToken());
  const editorState = signal(/** @type {EditorState|null} */ (null));
  const modalOpen = signal(false);

  function closeEditor() {
    modalOpen.value = false;
    editorState.value = null;
    // Refresh the token for the next open (tokens can rotate between requests)
    tokenSig.value = readAntiforgeryToken();
  }

  function handleMutated(/** @type {CellMutationResult} */ result) {
    applyMutationResult(result);
  }

  /**
   * Open the editor for a given date + slot. Cell onclick handlers call this.
   * Fetches hydration from EditorJson endpoint, then opens the modal.
   * @param {string} dateStr    "yyyy-MM-dd"
   * @param {string} slotId     GUID string (D format)
   * @param {string|null} mealId  GUID string when editing, null when adding
   */
  async function openEditor(dateStr, slotId, mealId) {
    tokenSig.value = readAntiforgeryToken();
    const url = `${hydration.editorJsonUrl}&date=${encodeURIComponent(dateStr)}&slotId=${encodeURIComponent(slotId)}${mealId ? `&mealId=${encodeURIComponent(mealId)}` : ""}`;
    try {
      const resp = await fetch(url, { headers: { "X-Requested-With": "XMLHttpRequest" } });
      if (!resp.ok) return;
      const state = /** @type {EditorState} */ (await resp.json());
      editorState.value = state;
      modalOpen.value = true;
    } catch {
      // Silently degrade — editor stays closed
    }
  }

  // Global bridge — called by server-rendered cell onclick handlers.
  // Exposes openEditor, closeEditor, and applyMutation for Playwright E2E tests.
  window.__mealPlannerIsland = { openEditor, closeEditor, applyMutation: applyMutationResult };

  // Keyboard close
  document.addEventListener("keydown", (/** @type {KeyboardEvent} */ e) => {
    if (e.key === "Escape" && modalOpen.value) closeEditor();
  });

  // Mount once — Preact + signals handles all subsequent re-renders automatically.
  // The @preact/signals integration (signals.module.js) hooks into Preact's diffing
  // so any signal accessed in render() triggers a re-render when it changes.
  render(
    html`<${App}
      modalOpen=${modalOpen}
      editorState=${editorState}
      members=${hydration.members}
      tokenSig=${tokenSig}
      hydration=${hydration}
      onClose=${closeEditor}
      onMutated=${handleMutated} />`,
    root,
  );
}
