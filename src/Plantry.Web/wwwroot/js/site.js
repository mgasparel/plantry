// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

// Drag-and-drop reordering for [data-sortable-list] catalog lists (e.g. Categories). Each
// draggable item carries data-id/data-sort-order; on drop we POST the full ordered list of ids
// once to the URL named by data-sortable-list (the server reassigns sort orders as multiples of
// 10), reusing the page's existing antiforgery token. If the request fails we reload so the UI
// re-syncs with the persisted order rather than drifting.
(function () {
    'use strict';

    document.querySelectorAll('[data-sortable-list]').forEach(initSortableList);

    function initSortableList(list) {
        var dragged = null;

        list.querySelectorAll('[data-id]').forEach(function (item) {
            item.addEventListener('dragstart', function () {
                dragged = item;
                item.classList.add('catalog-list__item--dragging');
            });

            item.addEventListener('dragend', function () {
                item.classList.remove('catalog-list__item--dragging');
                dragged = null;
                persistOrder(list);
            });
        });

        list.addEventListener('dragover', function (e) {
            var target = e.target.closest('[data-id]');
            if (!dragged || !target || target === dragged) return;
            e.preventDefault();

            var rect = target.getBoundingClientRect();
            var before = (e.clientY - rect.top) < rect.height / 2;
            list.insertBefore(dragged, before ? target : target.nextSibling);
        });
    }

    function persistOrder(list) {
        var url = list.getAttribute('data-sortable-list');
        var token = document.querySelector('input[name="__RequestVerificationToken"]');
        if (!url || !token) return;

        var items = Array.prototype.slice.call(list.querySelectorAll('[data-id]'));

        // Nothing to do if every item is already at its index-derived position.
        var unchanged = items.every(function (item, index) {
            return Number(item.getAttribute('data-sort-order')) === index * 10;
        });
        if (unchanged) return;

        var body = new URLSearchParams();
        items.forEach(function (item) { body.append('ids', item.getAttribute('data-id')); });

        fetch(url, {
            method: 'POST',
            headers: { 'RequestVerificationToken': token.value },
            body: body
        }).then(function (response) {
            if (!response.ok) {
                window.location.reload();
                return;
            }
            // Commit the new positions locally so a subsequent drag diffs against them.
            items.forEach(function (item, index) {
                item.setAttribute('data-sort-order', String(index * 10));
            });
        }).catch(function () {
            window.location.reload();
        });
    }
})();
