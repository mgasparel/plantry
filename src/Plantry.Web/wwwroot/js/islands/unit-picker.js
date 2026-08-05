// @ts-check
//
// Island counterpart to Pages/Shared/_UnitOptionGroups.cshtml.  Server
// hydration remains authoritative for reachability and ordering; this module
// only groups the server sequence into native optgroups.

import { html } from "./runtime.js?v=1";

/** @typedef {{unitId:string, code:string, dimension:string}} UnitOption */

const CANONICAL_DIMENSIONS = new Set(["Mass", "Volume", "Count"]);

/** @param {string} dimension @returns {string} */
function dimensionLabel(dimension) {
  switch (String(dimension).toLowerCase()) {
    case "mass": return "Mass";
    case "volume": return "Volume";
    case "count": return "Count";
    default: return dimension;
  }
}

/**
 * Group the server-canonical reachable sequence into contiguous dimensions.
 * UnitQueries.OrderForDropdown / the adapter already own ordering; the island
 * must not apply browser-locale sorting that could diverge from
 * StringComparer.OrdinalIgnoreCase. Unknown dimensions are kept as supplied
 * and render outside an optgroup so a stale/partial read model cannot be
 * mislabeled as Mass, Volume or Count.
 *
 * @param {Iterable<UnitOption>} options
 * @returns {{dimension:string, options:UnitOption[]}[]}
 */
export function groupUnitOptions(options) {
  /** @type {{dimension:string, options:UnitOption[]}[]} */
  const groups = [];
  for (const option of options) {
    const groupDimension = dimensionLabel(option.dimension);
    const last = groups[groups.length - 1];
    if (!last || last.dimension !== groupDimension) {
      groups.push({ dimension: groupDimension, options: [option] });
    } else {
      last.options.push(option);
    }
  }
  return groups;
}

/**
 * Render a product unit picker as a borderless inline select — the unit slot
 * of the compact quantity+unit composite shared with recipe rows (see
 * .mp-dish-qty in plenish.css). A saved unit that is no longer reachable is
 * deliberately absent from the select options; there is no dedicated stale
 * visual — the disabled "Choose unit…" placeholder remains selected until
 * the user chooses a server-provided replacement. Reachability/optgroup
 * semantics are unchanged.
 *
 * @param {{options:UnitOption[], selectedUnitId:string,
 *          onChange:(unitId:string)=>void, ariaLabel?:string}} props
 */
export function UnitPicker({ options, selectedUnitId, onChange, ariaLabel = "Unit" }) {
  const groups = groupUnitOptions(options);
  return html`
    <select class="meal-unit-picker__select"
            value=${selectedUnitId}
            aria-label=${ariaLabel}
            onChange=${(/** @type {Event} */ e) =>
              onChange(/** @type {HTMLSelectElement} */ (e.target).value)}>
      ${selectedUnitId === "" && html`
        <option value="" disabled>Choose unit…</option>
      `}
      ${groups.map((group) => CANONICAL_DIMENSIONS.has(group.dimension)
        ? html`<optgroup key=${group.dimension} label=${group.dimension}>
            ${group.options.map((option) => html`
              <option key=${option.unitId} value=${option.unitId}>${option.code}</option>
            `)}
          </optgroup>`
        : group.options.map((option) => html`
            <option key=${option.unitId} value=${option.unitId}>${option.code}</option>
          `))}
    </select>
  `;
}
