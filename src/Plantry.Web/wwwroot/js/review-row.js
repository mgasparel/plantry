// Alpine component backing the receipt-review edit drawer (_ReviewRow.cshtml).
//
// Each row is its own Alpine.data instance, so its helper methods and state stay private to
// that row. This is deliberate: defining the helpers in x-init instead fails two ways —
//   1. bare `applyProductDefaults = ...` assignments leak to `window`, so every row clobbers one
//      global and the $watch ends up calling the last-rendered row's closure (cross-row corruption); and
//   2. `this` inside an x-init expression is NOT the component data, so `this.productDefaults`
//      etc. are undefined when a stored helper runs later from the watch.
// Inside an Alpine.data() component, `this` IS the reactive data — the same contract
// searchable-select.js relies on — so the helpers below read/write row state safely.
//
// The server passes the row's initial state as a JSON object (see `alpineState` in
// _ReviewRow.cshtml); we spread it onto the component and add the live-recompute methods.
document.addEventListener('alpine:init', function () {
    'use strict';

    Alpine.data('reviewRow', function (state) {
        return Object.assign({}, state, {
            // .expiry-field "Date" toggle: switch to has-date mode, seed today when empty,
            // and open the native date picker. "Never" is handled by the radio (expiryMode = 'never').
            pick() {
                this.expiryMode = 'has';
                if (!this.d) this.d = this.today;
                this.$nextTick(function () {
                    if (this.$refs.dp && this.$refs.dp.showPicker) this.$refs.dp.showPicker();
                }.bind(this));
            },

            // Apply the selected product's defaults on every product selection (AI match, "Did you mean"
            // chip, or dropdown). This is a PURE APPLICATOR: productDefaults entries are already resolved
            // server-side (receipt-unit-wins; expiry = today + DefaultDueDays, or "" for never) — see the
            // productDefaults map in _ReviewRow.cshtml — so no prefill rule lives here to drift from the server.
            applyProductDefaults(pid) {
                var def = this.productDefaults[pid];
                if (!def) return;
                this.loc = def.locationId || '';
                this.unit = def.unitId || '';
                this.d = def.expiry || '';
                this.expiryMode = this.d ? 'has' : 'never';
            },

            init() {
                var self = this;
                // Initial render is already prefilled server-side (ComputePrefill); the watch
                // only fires on subsequent in-drawer selection changes, never on init.
                this.$watch('selectedProductId', function (pid) {
                    if (pid) self.applyProductDefaults(pid);
                });
            }
        });
    });
});
