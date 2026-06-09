// Attaches the page's antiforgery token to every unsafe (non-GET/HEAD) htmx request, so hypermedia
// mutations — the _DataGrid delete action is the first — pass ASP.NET Core's antiforgery validation.
// The token is read from the hidden __RequestVerificationToken input that a `<form method="post">`
// or @Html.AntiforgeryToken() emits (the _DataGrid partial emits one when it renders a POST action);
// this is the same token source sortable-list.js reads. GET/HEAD are left untouched — antiforgery
// only guards unsafe methods, and the searchable-select hx-get must not carry it.
(function () {
    'use strict';

    document.body.addEventListener('htmx:configRequest', function (evt) {
        var verb = (evt.detail.verb || '').toLowerCase();
        if (verb === 'get' || verb === 'head') return;

        var token = document.querySelector('input[name="__RequestVerificationToken"]');
        if (token) {
            evt.detail.headers['RequestVerificationToken'] = token.value;
        }
    });
})();
