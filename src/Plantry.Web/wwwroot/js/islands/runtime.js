// @ts-check
//
// Shared island runtime (ADR-020 §6, bead plantry-2zvm.1).
//
// Re-exports the Preact + htm + signals surface that every island uses,
// pre-bound so islands import one module instead of three. All relative-path
// vendor imports remain relative — no import map (import maps fight Razor @@
// escaping and have brittle ordering rules — see ADR-020 §6).
//
// Usage in an island:
//   import { h, render, html, signal, computed } from './runtime.js';
//
// Type references below are checker-only (JSDoc + jsconfig `paths` → vendor.d.ts).

import { h, render, Fragment } from "./vendor/preact.module.js";
import { signal, computed, effect, batch } from "./vendor/signals.module.js";
import htm from "./vendor/htm.module.js";

/** Tagged-template html function pre-bound to Preact's `h`. */
const html = htm.bind(h);

export { h, render, Fragment, html, signal, computed, effect, batch };
