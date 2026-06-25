// @ts-check
//
// Shared island transport + hydration helpers (ADR-020 §2/§7, bead plantry-2zvm.1).
//
// UI / transport ONLY — no domain logic. Every island that sends JSON to the
// server and reads hydration data from the page should import from here rather
// than duplicating these inline. See ADR-020 §7 tripwire: business/domain logic
// must never migrate into this file or any island.
//
// Usage:
//   import { readHydration, readAntiforgeryToken, postJson } from './helpers.js';

/**
 * Read the hydration payload emitted by the server as a typed JSON blob.
 *
 * The server emits hydration data in a `<script type="application/json" id="...">` tag;
 * this function parses it back to a typed value. Returns null (and logs a warning) if
 * the element is missing, so islands can handle a missing mount gracefully.
 *
 * @template T
 * @param {string} elementId  The `id` attribute on the `<script type="application/json">` element.
 * @returns {T | null}
 */
export function readHydration(elementId) {
  const el = document.getElementById(elementId);
  if (!el) {
    console.warn(`[islands] Hydration element #${elementId} not found — island skipped.`);
    return null;
  }
  return /** @type {T} */ (JSON.parse(el.textContent ?? "null"));
}

/**
 * Read the ASP.NET Core antiforgery token from the hidden `__RequestVerificationToken`
 * input that `@Html.AntiForgeryToken()` injects. Returns an empty string (and logs a
 * warning) when the input is absent so callers can still call postJson — the server will
 * reject with 400 rather than the island throwing, which is the safer degradation.
 *
 * @returns {string}
 */
export function readAntiforgeryToken() {
  const input = /** @type {HTMLInputElement | null} */ (
    document.querySelector('input[name="__RequestVerificationToken"]')
  );
  if (!input) {
    console.warn("[islands] Antiforgery token input not found — POST will be rejected by the server.");
    return "";
  }
  return input.value;
}

/**
 * Post a JSON body to `url` with the standard island request headers.
 *
 * Sets `Content-Type: application/json`, `RequestVerificationToken` (ASP.NET Core CSRF),
 * and `X-Requested-With: XMLHttpRequest`. Returns the raw `Response` — callers check
 * `resp.ok` and call `await resp.json()` themselves. Throws on network error.
 *
 * @param {string} url
 * @param {unknown} body  — serialised to JSON via `JSON.stringify`.
 * @param {string} token  — antiforgery token from {@link readAntiforgeryToken}.
 * @returns {Promise<Response>}
 */
export async function postJson(url, body, token) {
  return fetch(url, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "RequestVerificationToken": token,
      "X-Requested-With": "XMLHttpRequest",
    },
    body: JSON.stringify(body),
  });
}
