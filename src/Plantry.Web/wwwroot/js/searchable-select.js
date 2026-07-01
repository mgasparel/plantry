// Alpine component backing <searchable-select> (see TagHelpers/SearchableSelectTagHelper.cs).
// Alpine owns the popover/keyboard interaction; htmx swaps the option list as the user types
// (see hx-get on the search input). Selecting an option writes the value into the hidden input
// that actually gets posted with the form, and a `change` event so any other listeners observe it.
//
// hasMatches / createLabel back the AllowCreate mode (plantry-gzro.1, the shared fuzzy-ranked
// search + demoted create-button component): hasMatches tracks whether the last htmx swap of the
// listbox produced any options (drives the create button's btn--demoted class), and createLabel
// is seeded from data-create-label for the button's x-text.
document.addEventListener('alpine:init', function () {
    'use strict';

    Alpine.data('searchableSelect', function () {
        return {
            open: false,
            highlighted: -1,
            query: '',
            hasMatches: false,
            createLabel: '',

            init() {
                this.query = this.$el.dataset.initialLabel ?? '';
                this.createLabel = this.$el.dataset.createLabel ?? '';
            },

            select(value, label) {
                // $refs.hidden only exists in the bound (asp-for) mode. Unbound hosts (e.g.
                // Recipes/TakeStock's per-item enrichment) render their own @click handlers that
                // read data-* attributes and dispatch their own event instead of calling select()
                // at all — guarded here so select() stays safe to call from either mode.
                if (this.$refs.hidden) {
                    this.$refs.hidden.value = value;
                    this.$refs.hidden.dispatchEvent(new Event('change', { bubbles: true }));
                }
                this.query = label;
                this.open = false;
                this.highlighted = -1;
            },

            options() {
                return Array.from(this.$refs.listbox.querySelectorAll('[role="option"]'));
            },

            highlightNext() {
                this.open = true;
                var opts = this.options();
                if (opts.length === 0) return;
                this.highlighted = (this.highlighted + 1) % opts.length;
                opts[this.highlighted].scrollIntoView({ block: 'nearest' });
            },

            highlightPrev() {
                this.open = true;
                var opts = this.options();
                if (opts.length === 0) return;
                this.highlighted = (this.highlighted - 1 + opts.length) % opts.length;
                opts[this.highlighted].scrollIntoView({ block: 'nearest' });
            },

            chooseHighlighted() {
                // Delegates to the option's own @click handler (via a real DOM click) rather than
                // calling select() directly — every host, bound or unbound, already puts the
                // correct pick logic on each <li>'s @click (select() for bound hosts like Shopping,
                // a custom pick-product dispatch for unbound hosts like Recipes/TakeStock), so
                // Enter-to-choose stays correct under both without special-casing here.
                var opts = this.options();
                var opt = opts[this.highlighted] || opts[0];
                if (opt) {
                    opt.click();
                }
            }
        };
    });
});
