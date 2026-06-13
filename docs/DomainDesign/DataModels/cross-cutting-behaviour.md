# Cross-cutting behaviour ✅

Behaviours that span multiple contexts and inform how tables are written and queried.

---

## Expiry materialization ✅

Expiry is a concrete `date` stored on `stock_entry`, written **once at event time**, never computed at read time. The `product` holds the *rules* (the four `default_due_days_*`); the lot holds the *resulting date*.

| Event | `stock_entry.expiry_date` becomes |
|---|---|
| Intake | user-entered value, else the default chain below |
| Transfer ambient → frozen | `freeze_date + default_due_days_after_freezing` |
| Transfer frozen → ambient | `thaw_date + default_due_days_after_thawing` |
| Open | `open_date + default_due_days_after_opening` |
| Transfer ambient → ambient | unchanged |

**Default resolution chain** (applied at the event, producing the stored date): product-level default → category `default_due_days` → blank (user fills in). At a frozen location, intake uses `default_due_days_after_freezing` in place of `default_due_days` (SPEC §387).

---

## Unit conversion resolution ✅

The engine behind `Consume` and fulfillment scoring:

1. **Same unit** → use as-is.
2. **Same dimension** → `factor_to_base` arithmetic (pure math, no rows).
3. **Cross-dimension** → look up a `product_conversion` for that product; if none exists, the conversion **fails loudly** and surfaces to the user (the "unit mismatch" fulfillment edge case) rather than silently skipping.

---

## Consumption primitive ✅ (ADR-011)

All stock removal flows through one operation — `ProductStock.Consume(quantity, unit, reason, sourceRef?)` — which converts units, orders lots FEFO, deducts across them, writes signed journal rows, and reports shortfall. It can target a specific lot instead of FEFO ("this carton is empty"). The `reason` taxonomy (**Consumed** / **Discarded** / **Correction**) is what makes waste analysis possible. Inventory knows nothing about recipes or substitutions; Cook (Phase 2) reaches it only as `Consume` calls carrying a `source_ref`.
