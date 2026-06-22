# Plantry — Vision

> Smart household inventory, built for the way people actually cook and shop.

---

## The Problem

Most households waste food, overspend on groceries, and cook less than they'd like — not because they lack the desire, but because the tools to manage it are too manual, too rigid, or simply nonexistent.

The apps that exist fall into two camps: simple lists (no intelligence, no memory) or powerful but painful trackers that demand more discipline than they return. Getting a grocery receipt into a system should take seconds. Finding out what you can cook tonight should be instant. Planning a week of meals around what's in your fridge should be the app's job, not yours.

Plantry is built on the premise that the friction is the problem. Remove the friction and the rest follows.

---

## What Plantry Is

A household inventory and kitchen intelligence app. At its core: a live picture of what you have at home, what it's worth, and what you can make with it.

The three things it does better than anything else:

**1. Intake is nearly automatic.** Take a photo of your receipt — or forward it by email — and Plantry reads it, maps every item to your catalog, and queues it for a fast review. Confirm and you're done. A full grocery run logged in under a minute.

**2. Recipes are connected to reality.** Every recipe shows what percentage of its ingredients you have on hand, what it'll cost to make, and whether anything is about to expire. Cooking isn't a separate activity from managing your pantry — it's the point of it.

**3. The app thinks ahead.** An AI planner can generate a week of meals around your inventory, your preferences, and what's on sale this week at local stores — prioritizing expiring ingredients and minimizing waste without you having to think about it.

---

## The Four Pillars

### Pantry as ground truth

Everything starts from an accurate, live inventory. Plantry tracks stock at the product level with expiry dates and full consumption history. It uses FEFO ordering — oldest stock consumed first — so nothing quietly expires in the back of a shelf. The pantry isn't a chore to maintain; intake automation makes keeping it current the path of least resistance.

### Receipt intake as the primary flow

The most common way stock enters a home is a grocery run. That's where Plantry earns its keep. AI vision parses receipt photos and extracts every line item with quantity and price. Items are matched to your catalog automatically, with a fast review step for anything ambiguous. Unrecognized items can be created inline without breaking the flow. The same pipeline works asynchronously — forward the email receipt and it'll be ready when you open the app.

### Recipes that know what you have

Recipe fulfillment is a first-class feature. Every recipe in Plantry carries a live score: how many of its ingredients are on hand, what's missing, and what it would cost to complete it. Cost per serving is computed from your own purchase history and updated with live deal data when available. Meal planning and shopping list generation flow directly from recipe fulfillment — no manual cross-referencing.

### Deal awareness baked in

Plantry integrates with local store flyers to surface this week's deals, mapped to products in your catalog. Deals feed directly into recipe cost estimates, shopping list prioritization, and stock-up alerts for items you buy frequently. The AI planner is deal-aware — it'll bias toward ingredients that are cheap this week, not just ingredients that are available.

---

## What Plantry Is Not

- Not a meal kit service or subscription.
- Not a social network or public-sharing platform — it's a private app shared only within your household (which can have more than one member).
- Not dependent on barcode scanning or external product databases.
- Not trying to be a Grocy skin. This is a ground-up product with its own data model, built for this specific set of problems.

---

## Open Questions

- **Flipp access:** No official public API. Options are an unofficial API, a scraper, or a browser extension exporter. Stability trade-offs TBD.
- **Fulfillment scoring edge cases:** Unit mismatch handling (recipe calls for cups, stock tracked in grams), partial stock, multi-SKU aggregation.
- **Meal plan slot model:** _Resolved_ — user-configurable, free-text meal slots per household (e.g., Breakfast / Lunch / Afternoon snack / Dinner). See SPEC §5 and §7h.
- **Push notifications:** Stock-up alerts ship in-app first; web-push needs PWA install — an enhancement, not foundational.
- **Email intake setup:** Dedicated forwarding address, or subject-line trigger on a shared inbox?
