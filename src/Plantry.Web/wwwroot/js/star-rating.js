// @ts-check
//
// star-rating.js — Alpine component backing the star-rating input (recipe ratings,
// plantry-zlwp.2). Alpine owns the interaction — tap a star to rate 1-5, tap the current rating
// again to clear it — and htmx carries the POST, mirroring the split in sortable-list.js's
// persist(): Alpine drives the DOM, htmx.ajax() drives the network call so the antiforgery token
// and response swap follow the same convention as every other mutation in the app.
//
// WHY A MODULE (not a classic script like plan-tune.js / sortable-list.js): the state object is
// extracted into an exported factory purely so rate()'s tap-current-again-to-clear toggle (the
// ticket's own acceptance clause, and the epic's "no opinion = absence of a row" storage
// decision), isFilled()'s hover-wins preview rule, and hint()'s pluralisation/cleared-state text
// can be unit-tested with the sanctioned zero-dependency `node --test` rig (see
// __tests__/star-rating.test.js) — mirroring recipe-sections.js / ingredient-amount.js. persist()
// (the only htmx/DOM-touching member) is the single untested seam, same as those precedents.
// `export` is a SyntaxError outside module context, so _Layout.cshtml loads this file via
// <script type="module" src="...">, not <script defer>; module scripts are deferred by default
// and execute in document order alongside classic defer scripts, so the "Alpine.data
// registrations before alpine.min.js" ordering documented in _Layout.cshtml still holds.
//
// NOTE (bd memory): declare every local with let/const, never var — this file is exempt from the
// var-heavy style in the older Alpine components (plan-tune.js, sortable-list.js).

/**
 * @typedef {Object} StarRatingConfig
 * @property {number} [value]        Initial rating, 0-5 (0 = unrated).
 * @property {string|null} [postUrl] Handler URL to POST the new rating to. Omit (e.g. the Dev
 *   component library demo, which has no backing recipe) and the widget stays fully interactive
 *   locally but skips the network call — lets the library page show live behaviour with no server.
 * @property {string} [name]         POST field name for the rating value (default 'stars').
 */

/**
 * Builds the Alpine.data state object for the star-rating input. A plain factory — nothing here
 * touches Alpine/document/htmx except persist() — so it can be constructed and asserted against
 * directly in tests without a DOM or Alpine runtime.
 * @param {StarRatingConfig} cfg
 */
export function createStarRating(cfg) {
    return {
        value: cfg.value || 0,
        hoverValue: 0,
        postUrl: cfg.postUrl || null,
        fieldName: cfg.name || 'stars',

        // A star is "filled" while hovering up to it, or — with no active hover — up to the
        // committed value. Hover always wins so the preview reflects the pointer, not the
        // last-saved rating.
        isFilled(star) {
            const active = this.hoverValue || this.value;
            return star <= active;
        },

        // Tap the current rating again to clear it (posts 0 — "no opinion = absence of a
        // row", per the epic's storage decision); tap any other star to set it.
        rate(star) {
            const next = (this.value === star) ? 0 : star;
            this.value = next;
            this.persist(next);
        },

        persist(stars) {
            if (!this.postUrl) return;

            const token = document.querySelector('input[name="__RequestVerificationToken"]');
            const values = {};
            values[this.fieldName] = stars;
            if (token) values.__RequestVerificationToken = token.value;

            htmx.ajax('POST', this.postUrl, { values: values, swap: 'none' }).catch(function () {
                window.location.reload();
            });
        },

        hint() {
            if (!this.value) return 'Tap to rate';
            const plural = this.value > 1 ? 's' : '';
            return 'You rated this ' + this.value + ' star' + plural + ' — tap again to clear';
        }
    };
}

// Guarded so importing this module under Node (star-rating.test.js) never touches `document` —
// ingredient-amount.js / recipe-sections.js have no module-scope side effects at all; this file
// needs one (the global Alpine.data registration), so it is the one that needs the guard.
if (typeof document !== 'undefined') {
    document.addEventListener('alpine:init', function () {
        'use strict';

        Alpine.data('starRatingInput', createStarRating);
    });
}
