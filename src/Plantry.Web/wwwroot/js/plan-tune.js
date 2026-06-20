// Alpine.data component for constant-sum / proportional-allocation sliders.
//
// Generalised from the meal-planner tune popover (plantry-izgn.4).
// Following the same pattern as meal-editor.js (ADR-013 §5).
//
// Config object (passed from Razor via x-data="planTune(cfg)" where cfg = window.__planTuneCfg):
//   buckets  — array of { key, label, color, defaultWeight } describing each slider bucket.
//              The three values always sum to 100; rebalance() maintains that invariant.
//
// Backward-compat shim: if cfg.buckets is absent, the component builds it from the legacy
// waste/cost/variety flat-key shape (wasteDefault, costDefault, varietyDefault) so existing
// callers keep working without change.
//
// Produces (in the meal-plan context) hidden inputs read by OnPostGenerateAsync:
//   wasteWeight, costWeight, varietyWeight  — integers summing to 100
//   budget                                  — optional weekly budget (0 = no target)
//   scope                                   — "week" | "today"
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
            budget: 0,
            scope: 'week',
            buckets: buckets,
            weights: Object.assign({}, initialWeights),

            reset: function () {
                var w = {};
                this.buckets.forEach(function (b) { w[b.key] = b.defaultWeight; });
                this.weights = w;
                this.budget = 0;
                this.scope = 'week';
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
