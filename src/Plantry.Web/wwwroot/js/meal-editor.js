// Alpine.data component for the meal editor (_MealEditor.cshtml).
//
// Replaces all inline @click / x-data attribute-string logic with a registered component.
// This eliminates the 'var-token silently fails' and 'Object.fromEntries collapses keys'
// bug classes (see bd memories) and is the legibility fix mandated by ADR-013 §5.
//
// State owned here (ADR-005 blesses Alpine for draft collections):
//   mode, note, dishes, att, attOverridden, attDefaults, config
//
// Rollup (fulfillment %/cost) is a SERVER PROJECTION only — ADR-013 §4/§5.
// There is NO client-side rollup formula in this file. The 'roll' getter that was here
// before (comment: "mirrors PL.rollup") has been deleted. On dish changes the component
// posts the draft list to OnPostRollupAsync, which computes via PlanFulfillmentService /
// PlanCostingService and returns the _EditorRollup partial for an htmx swap.
//
// config keys (passed from Razor via x-data="mealEditor(cfg)" where cfg is JSON):
//   dateStr       — ISO date "yyyy-MM-dd"
//   slotIdStr     — slot GUID (D format)
//   mealId        — GUID string or null (present when editing an existing meal)
//   isEditing     — bool
//   clearMealId   — GUID string or null (same as mealId when editing)

document.addEventListener('alpine:init', function () {
    'use strict';

    Alpine.data('mealEditor', function (cfg) {
        return {
            // ── Persistent server-side state (initialised from Razor JSON attributes) ──
            mode: cfg.mode,
            note: cfg.note,
            dishes: cfg.dishes,
            att: cfg.att,
            defaultAtt: cfg.defaultAtt,
            attOverridden: cfg.attOverridden,

            // ── canSave derived getter ──────────────────────────────────────────────
            get canSave() {
                return this.mode === 'note' ? this.note.trim().length > 0 : this.dishes.length > 0;
            },

            // ── Presentational helpers (formatting only — no domain logic) ──────────

            lvl: function (p) {
                return p >= 80 ? 'hi' : p >= 50 ? 'mid' : 'lo';
            },

            money: function (n) {
                return '$' + Number(n).toFixed(2);
            },

            // Per-dish subtitle: "N% in pantry [· $cost]" or "pantry item" for products.
            // Reads ONLY from the dish's own fields — no aggregation across dishes.
            dishMeta: function (d) {
                if (d.fulfillment == null) return 'pantry item';
                var s = d.fulfillment + '% in pantry';
                if (d.costPerServing != null) s += ' · ' + this.money(d.costPerServing * (d.servings || 1));
                return s;
            },

            photoUrl: function (d) {
                return '/Recipes/Details?id=' + d.itemId + '&handler=Photo';
            },

            // ── Attendee toggle ─────────────────────────────────────────────────────

            toggleAtt: function (uid) {
                if (this.att.indexOf(uid) >= 0) {
                    this.att = this.att.filter(function (x) { return x !== uid; });
                } else {
                    this.att = this.att.concat([uid]);
                }
                var attSorted = this.att.slice().sort().join(',');
                var defSorted = this.defaultAtt.slice().sort().join(',');
                this.attOverridden = attSorted !== defSorted;
            },

            // ── Dish list mutations ─────────────────────────────────────────────────

            // Called by the external addDishFromResult bridge (see bottom of this file).
            addDish: function (dish) {
                this.dishes.push(dish);
                this._requestRollup();
            },

            removeDish: function (idx) {
                this.dishes.splice(idx, 1);
                this._requestRollup();
            },

            incServings: function (d) {
                d.servings++;
                this._requestRollup();
            },

            decServings: function (d) {
                d.servings = Math.max(1, d.servings - 1);
                this._requestRollup();
            },

            // ── Mode toggle ─────────────────────────────────────────────────────────

            switchToNote: function () {
                this.mode = 'note';
                this.dishes = [];
                this._requestRollup();
            },

            switchToDishes: function () {
                this.mode = 'dishes';
                this.note = '';
                this._requestRollup();
            },

            // ── Server rollup (ADR-013 §4 — no client formula) ─────────────────────
            //
            // Posts the draft dish list to OnPostRollupAsync, which computes fulfillment
            // and cost via the existing server services and returns _EditorRollup as a
            // fragment. htmx swaps the result into #ed-rollup-{slotIdStr}.
            //
            // Debounced 300ms to avoid flooding on fast stepper clicks.
            _rollupTimer: null,

            _requestRollup: function () {
                var self = this;
                if (self._rollupTimer) clearTimeout(self._rollupTimer);
                self._rollupTimer = setTimeout(function () {
                    self._rollupTimer = null;
                    self._postRollup();
                }, 300);
            },

            _postRollup: function () {
                var self = this;
                var params = new URLSearchParams();
                params.append('mode', self.mode);
                self.dishes.forEach(function (d) {
                    params.append('dishKinds', d.kind);
                    params.append('dishItemIds', d.itemId);
                    params.append('dishServings', String(d.servings));
                });
                params.append('__RequestVerificationToken',
                    document.querySelector('[name=__RequestVerificationToken]')?.value ?? '');

                fetch('/MealPlan?handler=Rollup&date=' + cfg.dateStr + '&slotId=' + cfg.slotIdStr, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                    body: params.toString()
                }).then(function (r) {
                    return r.text();
                }).then(function (html) {
                    var target = document.getElementById('ed-rollup-' + cfg.slotIdStr);
                    if (target) htmx.swap(target, html, { swapStyle: 'innerHTML' });
                });
            },

            // ── Save (Assign) ───────────────────────────────────────────────────────

            save: function () {
                var self = this;
                var params = new URLSearchParams();
                params.append('mode', self.mode);
                if (self.mode === 'note') {
                    params.append('note', self.note);
                } else {
                    // Emit three index-aligned arrays so BuildDishSpecs can preserve
                    // per-dish servings regardless of kind order. Use URLSearchParams
                    // and POST as body string — Object.fromEntries would collapse
                    // repeated keys (e.g. dishKinds for a 2-dish meal), losing all
                    // but the last dish.
                    self.dishes.forEach(function (d) {
                        params.append('dishKinds', d.kind);
                        params.append('dishItemIds', d.itemId);
                        params.append('dishServings', String(d.servings));
                    });
                }
                self.att.forEach(function (uid) { params.append('attendeesOverride', uid); });
                if (self.attOverridden) params.append('attendeesOverridden', 'true');
                if (cfg.mealId) params.append('mealId', cfg.mealId);
                params.append('__RequestVerificationToken',
                    document.querySelector('[name=__RequestVerificationToken]')?.value ?? '');

                // POST via fetch with params.toString() to preserve repeated keys.
                // htmx.ajax with Object.fromEntries(params) collapses duplicate keys
                // and would lose all but the last dish / attendee.
                fetch('/MealPlan?handler=Assign&date=' + cfg.dateStr + '&slotId=' + cfg.slotIdStr, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                    body: params.toString()
                }).then(function (r) {
                    return r.text();
                }).then(function (html) {
                    // htmx.swap applies the cell outerHTML swap AND processes the out-of-band
                    // rail refresh emitted by _CellWithRail, keeping the insights in sync.
                    var cell = document.getElementById('cell-' + cfg.cellKey);
                    if (cell) htmx.swap(cell, html, { swapStyle: 'outerHTML' });
                });
                closeMealEditor();
            },

            // ── Clear (Remove meal) ─────────────────────────────────────────────────

            clearMeal: function () {
                if (!cfg.clearMealId) return;
                fetch('/MealPlan?handler=Clear&date=' + cfg.dateStr +
                      '&slotId=' + cfg.slotIdStr + '&mealId=' + cfg.clearMealId, {
                    method: 'POST',
                    headers: {
                        'RequestVerificationToken':
                            document.querySelector('[name=__RequestVerificationToken]')?.value ?? '',
                        'HX-Request': 'true'
                    }
                }).then(function (r) {
                    return r.text();
                }).then(function (html) {
                    // htmx.swap applies the cell swap AND processes the out-of-band rail
                    // refresh in the response, so the insights rail recomputes on clear.
                    var el = document.getElementById('cell-' + cfg.cellKey);
                    if (el) htmx.swap(el, html, { swapStyle: 'outerHTML' });
                });
                closeMealEditor();
            }
        };
    });

    // ── Global bridge: called by dish-search result buttons (outside the editor root) ──
    //
    // Dish-search results are rendered by htmx into #dish-results-{slotId} inside the editor.
    // The result buttons use onmousedown="addDishFromResult(this)" (via _DishSearch.cshtml).
    // This function locates the editor root by walking up the DOM to the nearest element with
    // x-data, then calls addDish() on its Alpine data object.
    //
    // Alpine v3: the reactive data object lives on el._x_dataStack[0].
    // (Alpine v2 used el.__x.$data — do not regress to v2 accessor.)
    window.addDishFromResult = function (el) {
        // Walk up to the nearest element that has the mealEditor Alpine data stack.
        var current = el.parentElement;
        var editorEl = null;
        while (current) {
            if (current._x_dataStack && current._x_dataStack[0] && typeof current._x_dataStack[0].addDish === 'function') {
                editorEl = current;
                break;
            }
            current = current.parentElement;
        }
        var d = editorEl && editorEl._x_dataStack && editorEl._x_dataStack[0];
        if (!d) return;
        d.addDish({
            kind: el.dataset.kind,
            itemId: el.dataset.itemId,
            name: el.dataset.name,
            servings: parseInt(el.dataset.servings, 10) || 1,
            fulfillment: el.dataset.fulfillment === '' ? null : parseInt(el.dataset.fulfillment, 10),
            costPerServing: el.dataset.costPerServing === '' ? null : parseFloat(el.dataset.costPerServing),
            hasPhoto: el.dataset.hasPhoto === '1'
        });
        // Clear the search input: find the input inside the nearest .dish-search ancestor
        var dishSearch = el.closest('.dish-search');
        if (dishSearch) {
            var input = dishSearch.querySelector('input[type="text"]');
            if (input) input.value = '';
        }
    };
});
