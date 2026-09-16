// @ts-check

/**
 * Shared unit-mismatch hint behavior for the Deals review surfaces. The buttons are rendered by Razor on
 * steps 1/3 and by deal-deck.js on step 2, so the listeners are delegated from document and survive htmx
 * swaps and deck-card replacement.
 */

let mounted = false;

/** @param {EventTarget|null} target */
function closestHint(target) {
  return target instanceof Element ? /** @type {HTMLButtonElement|null} */ (target.closest(".unit-hint")) : null;
}

/** @param {EventTarget|null} target */
function closestPopover(target) {
  return target instanceof Element ? target.closest(".unit-popover") : null;
}

/** Mount the delegated unit-hint interaction once for the page. */
export function mountDealUnitHints() {
  if (mounted) return;

  const popover = document.getElementById("deal-unit-popover");
  if (!(popover instanceof HTMLElement)) return;

  const copy = popover.querySelector("[data-unit-popover-copy]");
  if (!(copy instanceof HTMLElement)) return;

  mounted = true;
  let activeHint = /** @type {HTMLButtonElement|null} */ (null);
  let pinned = false;
  let closeTimer = /** @type {number|null} */ (null);

  function cancelClose() {
    if (closeTimer !== null) {
      window.clearTimeout(closeTimer);
      closeTimer = null;
    }
  }

  function closeHint() {
    cancelClose();
    if (activeHint) activeHint.setAttribute("aria-expanded", "false");
    activeHint = null;
    pinned = false;
    popover.hidden = true;
    popover.style.removeProperty("left");
    popover.style.removeProperty("top");
  }

  /** @param {HTMLButtonElement} button */
  function openHint(button) {
    const hint = button.dataset.unitHint;
    if (!hint || button.hidden || !button.isConnected) return;

    cancelClose();
    if (activeHint !== button) {
      if (activeHint) activeHint.setAttribute("aria-expanded", "false");
      activeHint = button;
      pinned = false;
    }

    copy.textContent = hint;
    button.setAttribute("aria-expanded", "true");
    popover.hidden = false;

    // Measure after un-hiding, then keep the fixed portal inside the viewport on narrow screens and at the
    // bottom edge. The portal can accept pointer movement from the button, so hover remains uninterrupted.
    const rect = button.getBoundingClientRect();
    const width = popover.offsetWidth;
    const height = popover.offsetHeight;
    const left = Math.max(12, Math.min(rect.right - width, window.innerWidth - width - 12));
    const below = rect.bottom + 8;
    const top = below + height <= window.innerHeight - 12
      ? below
      : Math.max(12, rect.top - height - 8);
    popover.style.left = `${left}px`;
    popover.style.top = `${top}px`;
  }

  function delayClose() {
    cancelClose();
    if (pinned) return;
    closeTimer = window.setTimeout(closeHint, 150);
  }

  document.addEventListener("pointerover", (event) => {
    const button = closestHint(event.target);
    if (button && event instanceof PointerEvent && event.pointerType === "mouse") {
      openHint(button);
      return;
    }
    if (closestPopover(event.target)) cancelClose();
  }, true);

  document.addEventListener("pointerout", (event) => {
    const button = closestHint(event.target);
    if (button && !(event.relatedTarget instanceof Node && button.contains(event.relatedTarget))) {
      delayClose();
      return;
    }
    const portal = closestPopover(event.target);
    if (portal && !(event.relatedTarget instanceof Node && portal.contains(event.relatedTarget)))
      delayClose();
  }, true);

  document.addEventListener("focusin", (event) => {
    const button = closestHint(event.target);
    if (button) openHint(button);
  }, true);

  document.addEventListener("focusout", (event) => {
    if (closestHint(event.target)) closeHint();
  }, true);

  document.addEventListener("click", (event) => {
    const button = closestHint(event.target);
    if (!button) return;
    if (activeHint === button && pinned) {
      closeHint();
      return;
    }
    openHint(button);
    pinned = true;
  }, true);

  document.addEventListener("pointerdown", (event) => {
    if (!closestHint(event.target) && !closestPopover(event.target)) closeHint();
  }, true);

  document.addEventListener("keydown", (event) => {
    if (event instanceof KeyboardEvent && event.key === "Escape") closeHint();
  }, true);

  window.addEventListener("resize", closeHint);
  window.addEventListener("scroll", closeHint, true);

  const region = document.getElementById("review-region");
  if (region) {
    const observer = new MutationObserver(() => {
      if (activeHint && (!activeHint.isConnected || activeHint.hidden)) closeHint();
    });
    observer.observe(region, { childList: true, subtree: true });
  }
}
