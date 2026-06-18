// Alpine.data component for the plan tuning popover (Index.cshtml / MealPlan page).
//
// Replaces the inline planTune() function that lived in the page <script> block.
// Following the same pattern as meal-editor.js (ADR-013 §5, plantry-cyj / plantry-jix).
//
// Config keys (passed from Razor via x-data="planTune(cfg)" where cfg is from window.__planTuneCfg):
//   wasteDefault, costDefault, varietyDefault  — server-side defaults (from PlanningWeights.Default)
//
// Produces hidden inputs read by OnPostGenerateAsync:
//   wasteWeight, costWeight, varietyWeight  — integers summing to 100
//   budget                                  — optional weekly budget (0 = no target)
//   scope                                   — "week" | "today"
//
// REBALANCE rule (mirrors prototype TunePopover): when one slider is dragged the other two
// are proportionally rescaled so the three values always sum to 100. Edge case: when both
// others are at 0 the remainder is split evenly.
//
// PlanningWeights only bias SOFT choices — they never relax a hard dietary stance (M5/M11).
// The constraint resolver from P3-6a stays authoritative.

document.addEventListener('alpine:init', function () {
    'use strict';

    Alpine.data('planTune', function (cfg) {
        return {
            tuneOpen: false,
            budget: 0,
            scope: 'week',
            weights: {
                waste: cfg.wasteDefault,
                cost: cfg.costDefault,
                variety: cfg.varietyDefault
            },
            meta: [
                { key: 'waste', label: 'Waste', color: 'oklch(0.62 0.13 150)' },
                { key: 'cost', label: 'Cost', color: 'oklch(0.62 0.13 70)' },
                { key: 'variety', label: 'Variety', color: 'oklch(0.62 0.13 255)' }
            ],

            reset: function () {
                this.weights = {
                    waste: cfg.wasteDefault,
                    cost: cfg.costDefault,
                    variety: cfg.varietyDefault
                };
                this.budget = 0;
                this.scope = 'week';
            },

            // Keep the three weights summing to 100 as one is dragged (mirrors the prototype).
            // Edge case: when both others are at 0 the remainder is split evenly.
            rebalance: function (changed, v) {
                v = Math.max(0, Math.min(100, Math.round(+v)));
                var others = ['waste', 'cost', 'variety'].filter(function (k) { return k !== changed; });
                var remain = 100 - v;
                var sumOther = others.reduce(function (s, k) { return s + this.weights[k]; }.bind(this), 0);
                var next = { waste: this.weights.waste, cost: this.weights.cost, variety: this.weights.variety };
                next[changed] = v;
                if (sumOther === 0) {
                    next[others[0]] = Math.round(remain / 2);
                    next[others[1]] = remain - next[others[0]];
                } else {
                    next[others[0]] = Math.round(remain * this.weights[others[0]] / sumOther);
                    next[others[1]] = remain - next[others[0]];
                }
                this.weights = next;
            }
        };
    });
});
