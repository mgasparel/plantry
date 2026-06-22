# Plantry — Future / Nice-to-haves

> Parking lot for features deliberately pulled out of the in-scope specs (SPEC.md / VISION.md).
> Nothing here blocks the core build. Items graduate out of this doc when they're committed to a phase.

---

## Recipe import from URL

**What.** Paste a recipe URL; AI extracts name, ingredients (mapped to catalog products where possible), directions, and tags into a review/edit form before saving.

**Why deferred.** A genuine convenience, but not on the critical path: recipes can already be created and edited manually (SPEC §4d–4e), and the cook/fulfillment/meal-plan loop doesn't depend on import existing. It's also the feature most exposed to messy real-world input, so it's better tackled once the recipe model is stable.

**Open questions when we pick it up.**
- HTML extraction reliability: paywalled pages, JS-rendered food blogs, inconsistent ingredient formats.
- Fetch strategy for client-rendered pages (raw HTML vs. a rendering step).
- How aggressively to auto-map extracted ingredients to catalog products vs. leaving them for the review form.

**Touch points if/when built.** Reuses the same review-then-commit pattern as receipt intake (SPEC §2e) and the catalog-search composite widget. AI orchestration stays server-side per ARCHITECTURE.md ADR-007.

---

## Tag-driven meal planning & member preferences

**What.** Tags are designed primarily as **meal-planner inputs**, not just browse filters (see recipes C2). Each household member gets a per-user **preference profile**: a **stance** toward any tag, on a single scale that captures both hard dietary restrictions and soft likes/dislikes.

**Stance scale (per User × Tag edge):**

| Stance | Force | Planner behaviour | Example |
|--------|-------|-------------------|---------|
| **Required** | hard positive | only plan recipes carrying this tag | a vegan: `Vegan = Required` |
| **Preferred** | soft positive | weight toward | "I love spicy": `Spicy = Preferred` |
| *Neutral* | — | default; absence of a stance | — |
| **Disliked** | soft negative | weight away | mild: `Poultry = Disliked` |
| **Restricted** | hard negative | never plan recipes carrying this tag | refusal/allergy: `Poultry = Restricted` |

**Why this shape.** The hard/soft distinction is **not** a property of the tag — the same tag ("Poultry") is a mild dislike for one member and an absolute no for another. So strength + polarity live on the **User↔Tag relationship**, never on the Tag itself. The Tag stays a kind-less vocabulary token (with an optional cosmetic `category` for UI grouping). This is the first-class evolution of the per-member dietary-profile idea.

**Planner aggregation (across a household sharing a meal).** Union every member's hard stances — the meal must satisfy all `Required` / `Restricted`; balance the soft stances on average. Irreconcilable hard conflicts (one member keto-meat-`Required`, another `Vegan`-`Required`) are flagged or resolved by planning separate dishes — a planner concern, not a tag-model one.

**Why deferred.** The meal planner is Phase 2+ and not yet modeled; preferences have no consumer until it exists. But the **Tag entity is built now** (Recipes, kind-less + categories) specifically so adding this later needs no tag migration. Builds on the flat-household "per-user preferences" note below.

**Related:** *Auto-Suggest Tags for Recipes* (below) would populate Recipe↔Tag membership automatically from ingredients, feeding this.

---

## Candidate future items (unsorted)

These have been mentioned but are not committed. Listed so they aren't lost.

- **PWA install + web-push notifications.** The core app is a responsive, mobile-first web app; camera capture for receipts works through the standard browser file input. Installability, offline shell, and web-push (for stock-up alerts) are an additive enhancement layer, not a foundational requirement.
- **Per-member roles / permissions.** v1 households are flat (every member equal). Owner/member roles and per-user preferences layer on top of the existing `household_id` scoping (ARCHITECTURE.md ADR-008) if needed.
- **Deeper intelligence layer:** deal-optimized shopping-list routing, pattern-based stock-up recommendations, consumption-trend analysis.
- **Consumption Rates** We drink a lot of Bubly. How many cans per week do we go through? When will we need to restock? This could be expanded into a complete analytics section.
- **Auto-Convert Units** When displaying units, use the highest human-friendly unit. For example, a recipe calls for 1/2 Cup, and we double the recipe. We should never list 2 x 1/2 Cup, we should list 1 Cup.
- **Auto-Suggest Tags for Recipes** Based on the ingredient list, suggest or auto-tag recipes, ie: Spicy, Vegetarian, Vegan, Meat, Fish