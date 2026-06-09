// Alpine component backing the `_SortableList` partial. Alpine owns the drag-and-drop
// interaction (tracking the dragged item and reordering the DOM as the pointer moves over
// siblings); on drop, htmx posts the full ordered list of ids to the url the partial was given
// (the server reassigns sort orders as multiples of ten). The antiforgery token is read from
// the page's hidden input — emitted globally by _Layout — and sent as a form value, which is
// what ASP.NET Core's antiforgery validation checks by default.
//
// The dragged item's id is read from the element's own `data-id` ($el / $event.target) rather
// than interpolated into the Alpine expression — see SearchableSelectTagHelper for why
// interpolating arbitrary values into inline handlers is avoided here.
document.addEventListener('alpine:init', function () {
    'use strict';

    Alpine.data('sortableList', function (reorderUrl) {
        return {
            dragging: null,

            onDragStart(el) {
                this.dragging = el.dataset.id;
            },

            onDragOver(e) {
                var target = e.target.closest('[data-id]');
                if (!this.dragging || !target || target.dataset.id === this.dragging) return;

                var draggedEl = this.$el.querySelector('[data-id="' + this.dragging + '"]');
                if (!draggedEl) return;

                var rect = target.getBoundingClientRect();
                var before = (e.clientY - rect.top) < rect.height / 2;
                this.$el.insertBefore(draggedEl, before ? target : target.nextSibling);
            },

            onDragEnd() {
                this.dragging = null;
                this.persist();
            },

            persist() {
                var items = Array.from(this.$el.querySelectorAll('[data-id]'));

                // Nothing to do if every item is already at its index-derived position.
                var unchanged = items.every(function (item, index) {
                    return Number(item.getAttribute('data-sort-order')) === index * 10;
                });
                if (unchanged) return;

                var token = document.querySelector('input[name="__RequestVerificationToken"]');
                var values = { ids: items.map(function (item) { return item.getAttribute('data-id'); }) };
                if (token) values.__RequestVerificationToken = token.value;

                htmx.ajax('POST', reorderUrl, { values: values, swap: 'none' }).then(function () {
                    // Commit the new positions locally so a subsequent drag diffs against them.
                    items.forEach(function (item, index) {
                        item.setAttribute('data-sort-order', String(index * 10));
                    });
                }).catch(function () {
                    window.location.reload();
                });
            }
        };
    });
});
