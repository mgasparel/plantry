// Alpine component backing <searchable-select> (see TagHelpers/SearchableSelectTagHelper.cs).
// Alpine owns the popover/keyboard interaction; htmx swaps the option list as the user types
// (see hx-get on the search input). Selecting an option writes the value into the hidden input
// that actually gets posted with the form, and a `change` event so any other listeners observe it.
document.addEventListener('alpine:init', function () {
    'use strict';

    Alpine.data('searchableSelect', function () {
        return {
            open: false,
            highlighted: -1,
            query: '',

            init() {
                this.query = this.$el.dataset.initialLabel ?? '';
            },

            select(value, label) {
                this.$refs.hidden.value = value;
                this.$refs.hidden.dispatchEvent(new Event('change', { bubbles: true }));
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
                var opts = this.options();
                var opt = opts[this.highlighted] || opts[0];
                if (opt) this.select(opt.getAttribute('data-value'), opt.textContent.trim());
            }
        };
    });
});
