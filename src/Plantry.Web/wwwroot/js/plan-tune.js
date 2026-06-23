// Alpine.data component for constant-sum / proportional-allocation sliders.
//
// Generalised from the meal-planner tune popover (plantry-izgn.4).
// Alpine.data component pattern (ADR-013 §5).
//
// Config object (passed from Razor via x-data="planTune(cfg)" where cfg = window.__planTuneCfg):
//   buckets  — array of { key, label, color, defaultWeight } describing each slider bucket.
//              The three values always sum to 100; rebalance() maintains that invariant.
//
// Backward-compat shim: if cfg.buckets is absent, the component builds it from the legacy
// waste/cost/variety flat-key shape (wasteDefault, costDefault, varietyDefault) so existing
// callers keep working without change.
//
// Produces (in the meal-plan context):
//   persistSettings(el) — on slider/budget change, POSTs wasteWeight/costWeight/varietyWeight/budget
//                         to ?handler=SetPlanningSettings (persists as week override independently
//                         of Generate). htmx processes the response to update the grid + bar.
//   scope               — "week" | "today" — included in Generate POST only.
//
// REBALANCE rule: when one slider is dragged the others are proportionally rescaled so all
// values always sum to 100. Edge case: when all others are at 0 the remainder splits evenly.
//
// PlanningWeights only bias SOFT choices — they never relax a hard dietary stance (M5/M11).
// The constraint resolver stays authoritative.

document.addEventListener('alpine:init', function () {
    'use strict';

    Alpine.data('planTune', function (cfg) {
        // Normalise config: accept either buckets array or legacy flat-key shape.
        var buckets = cfg.buckets || [
            { key: 'waste',   label: 'Waste',   color: 'oklch(0.62 0.13 150)', defaultWeight: cfg.wasteDefault   },
            { key: 'cost',    label: 'Cost',     color: 'oklch(0.62 0.13 70)',  defaultWeight: cfg.costDefault    },
            { key: 'variety', label: 'Variety',  color: 'oklch(0.62 0.13 255)', defaultWeight: cfg.varietyDefault }
        ];

        // Build initial weights map: { key: defaultWeight, ... }
        var initialWeights = {};
        buckets.forEach(function (b) { initialWeights[b.key] = b.defaultWeight; });

        return {
            tuneOpen: false,
            // Seed from persisted resolved value (0 when not set). cfg.budget is a numeric decimal
            // injected by the Razor page (window.__planTuneCfg.budget) so the popover reflects the
            // persisted budget on every render rather than always opening at 0.
            budget: (typeof cfg.budget === 'number' ? cfg.budget : 0),
            scope: 'week',
            buckets: buckets,
            weights: Object.assign({}, initialWeights),

            reset: function () {
                // Reset to app defaults (PlanningWeights.Default), not to persisted values.
                // Uses appDefault (injected per-bucket) so reset always restores the hard-coded
                // app baseline regardless of what the user has persisted.
                var w = {};
                this.buckets.forEach(function (b) { w[b.key] = (b.appDefault !== undefined ? b.appDefault : b.defaultWeight); });
                this.weights = w;
                this.budget = 0;
                this.scope = 'week';
            },

            // Persist the current budget + weights as a per-week override via SetPlanningSettings.
            // Fired on slider change (@@change) and budget input change (@@change). Uses htmx to
            // POST the hidden inputs inside the component root and swaps the grid + bar from the response.
            persistSettings: function (rootEl) {
                // Collect the antiforgery token from the page (all Razor Pages forms include it).
                var token = document.querySelector('input[name="__RequestVerificationToken"]');
                if (!token) return; // no token = test or non-form page; skip gracefully.

                // Build the form data from the hidden inputs inside the component.
                var form = new FormData();
                form.append('__RequestVerificationToken', token.value);
                var wasteEl = rootEl.querySelector('input[name="wasteWeight"]');
                var costEl  = rootEl.querySelector('input[name="costWeight"]');
                var varEl   = rootEl.querySelector('input[name="varietyWeight"]');
                var budgEl  = rootEl.querySelector('input[name="budget"]');
                if (wasteEl) form.append('wasteWeight', wasteEl.value);
                if (costEl)  form.append('costWeight',  costEl.value);
                if (varEl)   form.append('varietyWeight', varEl.value);
                if (budgEl)  form.append('budget',      budgEl.value);

                // Derive the week from the Generate button's URL on the page.
                var genBtn = rootEl.querySelector('[hx-post*="handler=Generate"]');
                var weekParam = '';
                if (genBtn) {
                    var url = genBtn.getAttribute('hx-post') || '';
                    var m = url.match(/week=([^&]+)/);
                    if (m) weekParam = '&week=' + m[1];
                }

                // POST to SetPlanningSettings; htmx processes the response automatically.
                htmx.ajax('POST',
                    '/MealPlan?handler=SetPlanningSettings' + weekParam,
                    { source: rootEl, target: '#plan-main-content', swap: 'innerHTML', values: form });
            },

            // Keep all bucket weights summing to 100 as one is dragged.
            // Edge case: when all others are at 0 the remainder splits evenly across them.
            rebalance: function (changed, v) {
                v = Math.max(0, Math.min(100, Math.round(+v)));
                var self = this;
                var others = this.buckets.map(function (b) { return b.key; }).filter(function (k) { return k !== changed; });
                var remain = 100 - v;
                var sumOther = others.reduce(function (s, k) { return s + self.weights[k]; }, 0);
                var next = Object.assign({}, this.weights);
                next[changed] = v;
                if (sumOther === 0) {
                    // Even split across others when they're all at 0.
                    var share = Math.floor(remain / others.length);
                    var leftover = remain;
                    others.forEach(function (k, i) {
                        if (i < others.length - 1) { next[k] = share; leftover -= share; }
                        else { next[k] = leftover; }
                    });
                } else {
                    // Proportional rescale, last bucket absorbs rounding remainder.
                    var allocated = 0;
                    others.forEach(function (k, i) {
                        if (i < others.length - 1) {
                            let val = Math.round(remain * self.weights[k] / sumOther);
                            next[k] = val;
                            allocated += val;
                        } else {
                            next[k] = remain - allocated;
                        }
                    });
                }
                this.weights = next;
            }
        };
    });
});
