# DEFER Taxonomy Audit — 2026-07

Spec-prep for **plantry-x5kp**. This document is the empirical baseline the fable-arbiter
rulebook cites: what DEFER findings the autonomous pipeline's critic actually produces, how
often each boundary trigger fires, which finding classes recur, and what happened to the beads
they spawned. Category keys defined here (kebab-case) are the stable vocabulary the arbiter
uses for recurrence detection and default rulings.

## Method

Sources mined 2026-07-28:

1. **Pre-flight reports** — 126 files in `code/.preflight/`. DEFER sections extracted from
   every file (both `### DEFER FINDINGS` and `WHY DEFER:` inline formats).
2. **Review-filed beads** — `bd list --label code-review --all` → 189 beads (25 open/blocked,
   164 closed); descriptions and notes mined for DEFER language, trigger keywords, source case.

Format-drift caveats:

- **2026-06-06 → 06-13 (24 reports): no DEFER category existed.** Zero findings from this
  period; early counts are structurally zero, not evidence of clean passes.
- **2026-06-14 → 06-30 (29 reports):** `## DEFER Findings` header present; `WHY DEFER:` labels
  mostly present but trigger names sometimes prose ("Out-of-scope blast radius / low
  confidence"). Filed-bead ids usually absent from the report.
- **2026-07 (68+ reports):** standardized `WHY DEFER: <trigger> — RECOMMEND: … → Filed as
  plantry-xxxx` format; from ~07-21 the DEFER-to-bead disposition line is routine, and from
  ~07-25 bead descriptions quote the critic finding verbatim.
- Bead-side counts include DEFER-filed beads whose originating review is **not** in
  `.preflight/` (reviews run inside worker sessions or the standalone code-review skill), so
  bead counts exceed report-extractable findings.

Counts marked `~` are approximate due to the drift above.

## 1. Frequency by boundary trigger

**A. Pre-flight report findings** — 30 distinct DEFER findings (plus ~6 multi-pass
restatements of the same finding, e.g. the plantry-n3r3 trio restated in pass 2). Multi-trigger
findings counted once per trigger.

| Trigger | 06-14 → 06-30 | 07-01 → 07-28 | Total |
|---|---|---|---|
| out-of-scope (incl. "blast radius") | 3 | 10 | **13** |
| contested-decision | 3 | 4 | **7** |
| missing-test-infra | 1 | 6 | **7** |
| low-confidence | 1 | 4 | **5** |
| unlabelled / design-call prose | 0 | 2 | **2** |
| *Distinct findings* | *7* | *23* | ***30*** |

**B. Bead corpus** — 131 of 189 code-review beads carry explicit DEFER/deferred-from language.
Trigger keyword mentions (co-occurring triggers counted in each row):

| Trigger keyword | Beads mentioning |
|---|---|
| out-of-scope / blast-radius | 56 |
| low-confidence | 31 |
| contested-decision / design call-fork | 30 |
| missing-test-infra | 24 |
| none of the four (unlabelled) | 17 |

Ranking is stable across both views: **out-of-scope ≫ contested ≈ missing-test-infra ≈
low-confidence**, with a persistent unlabelled tail the rulebook should force into a named
trigger.

**Filing leakage:** 3 report findings have no discoverable bead — 8r6-p1 (unregistered CSS
patterns), 48l-p1 (unordered `Take(SuggestionCap)`), qszb-p1a (Grocy `StageConversionAsync`
Count-dimension drop, despite the report claiming "Filed as a tracked follow-up"). plantry-h3hb
already tracks propagating tree-verification of DEFER recommendations to the inline critic.

## 2. Class taxonomy within each trigger

### missing-test-infra

| Class key | What it is | Examples |
|---|---|---|
| `missing-seam:timeprovider` | worker/test needs TimeProvider/FakeTimeProvider seam | plantry-hdry (10kx) |
| `missing-seam:iclock` | prod code bypasses injected IClock / ambient clock in SUT | plantry-5nxt, plantry-tact |
| `fixture-clock:fixed` | test fixtures on wall clock → UTC-midnight flake | plantry-apvz, plantry-1w87, plantry-nhnw |
| `coverage:l5-e2e` | behavior only verifiable with Playwright/L5 scaffolding | plantry-fd0x, plantry-470f, plantry-hldx, plantry-o39n |
| `coverage:classic-ui-js` | non-island Alpine/htmx JS has no sanctioned harness | plantry-p5ww (gzro.1) |
| `missing-test-infra:migration-harness` | no seed-and-migrate harness for data migrations | plantry-2y1r, plantry-o4dp |
| `missing-seam:chatclient` | AI-call behavior untestable without client stub | plantry-uurp, plantry-nax |

### out-of-scope

| Class key | What it is | Examples |
|---|---|---|
| `out-of-scope:sibling-call-site` | identical bug at an adjacent call site the spec excluded | plantry-paay, plantry-wcmg, plantry-pe7n, plantry-9n7l |
| `perf:io-in-loop` | N+1 / per-row round-trips; fix needs new batch contracts | plantry-ubqb, plantry-4t0g, plantry-n7u, plantry-rsy1 |
| `observability:missing-logger` | silent catch / unlogged state change; ILogger retrofit | plantry-6ha4, plantry-hq7c, plantry-nfzj, plantry-u4r8 |
| `missing-guard:di-lifetime` | load-bearing registration/lifetime with no pinning test | plantry-b6sc |
| `dead-code:orphan` | pre-existing orphaned file/CSS/helper the diff exposed | plantry-dnr, plantry-l350, plantry-puta, plantry-p9ej |
| `dup-helper:*` | verbatim-duplicated helper (`reporoot`, `fixedclock`, `expiry-today`) | plantry-kbrt, plantry-adie, plantry-g9jq |
| `dup-css-rule` | byte-identical CSS rules duplicated in plenish.css | plantry-j0d7 |
| `out-of-scope:adjacent-flow` | correct fix lives in a flow outside the diff footprint | plantry-pqu (8r6) |
| `out-of-scope:other-subsystem` | same defect class in another subsystem (e.g. Grocy migration) | qszb-p1a (unfiled) |

### contested-decision

| Class key | What it is | Examples |
|---|---|---|
| `contested:unit-semantics` | unit modeling forks (doz/pk/servings, mixed-unit merge) | plantry-qszb, plantry-2hzp, plantry-5yde, plantry-xw6 |
| `contested:duplicate-pair-policy` | schema/domain invariant needing an ADR call | plantry-pcfe |
| `arch-decision:event-transactionality` | outbox / multi-aggregate-save / pre-commit dispatch | plantry-3n7, plantry-kw52, plantry-jvzk, plantry-9or |
| `component-library:drift` | unregistered pattern, naming collision, bespoke duplicate | plantry-7p32, plantry-zjh9, plantry-yv9, plantry-qmy1 |
| `contested:sort-order` | no AC or convention settles a user-visible ordering | 48l-p1 (unfiled) |
| `contested:ui-convention` | verb/copy/placement conventions needing owner call | plantry-h91d, plantry-df5b, plantry-8fy |

### low-confidence

| Class key | What it is | Examples |
|---|---|---|
| `test-gap:untested-deliverable` | in-diff deliverable shipped without its test | plantry-z2sx |
| `test-altitude:extract-pure` | logic pinned only at L4; L1 extraction uncertain | plantry-j4cx |
| `hardcoded-href:pathbase` | root-relative href breaks under non-root PathBase | plantry-72c6 |
| `read-scope:conversion-load` | read-model load narrower than consumer needs | plantry-xnt5 |
| `latent-edge-case` | correct today, breaks under a narrow untriggered condition | plantry-yukq, plantry-bhuy, plantry-fxxh |

### Cross-trigger recurring guards

| Class key | What it is | Examples |
|---|---|---|
| `guard:html-raw-xdata` | Html.Raw inside x-data truncation | gcpb fix → plantry-wcmg → plantry-qrg7 ("3rd recurrence") |
| `guard:ambient-clock` | ambient-clock architecture guard scope/patterns | plantry-8c4o, plantry-hcf8, plantry-mvev |
| `cache-busting:stale-import` | JS import missing ?v= token, one level deeper each time | plantry-4h3a → plantry-hxkf → plantry-zg08 |

## 3. Recurrence analysis (class keys with 2+ occurrences across different cases)

| Class key | n | Occurrences (case → bead, date) |
|---|---|---|
| `coverage:l5-e2e` | 11 | n40 06-17; opc→o39n 06-28; gzro.1→p5ww 07-01; hl6u→lawx 07-05; wq9s→ac23 07-06; obg3→tg79 07-18; dtr9→22ci 07-18; fxxh→hldx 07-21; hldx→nuvm 07-21; 4037→fd0x 07-22; m375→470f 07-28 |
| `component-library:drift` | ~11 | 8r6 06-15 (unfiled); 259→04j 06-16; juh→8jo 06-16; bvf 06-16; yv9 06-17; 7ay 06-18; 26g→d37g 06-30; j6e8→zjh9 07-21; jpwg 07-21; vw6r→7p32 07-27; m375→qmy1 07-28 |
| clock family (`missing-seam:iclock`/`timeprovider`, `fixture-clock:fixed`, `guard:ambient-clock`, `dup-helper:fixedclock`) | ~10 | 3tvb→nhnw 07-10; zcbx→tact 07-24; tact→1w87 07-25; 5nxt 07-27; lgbu→apvz, adie, 8c4o, hcf8, mvev 07-28; 10kx→hdry 07-28 |
| `observability:missing-logger` | 6 | cw4q 06-27; 2j5k 06-27; nz3u.3→6ha4 06-28; fqb0.9→nfzj 07-10; av8z→u4r8 07-11; 3fqm→hq7c 07-21 |
| `dead-code:orphan` | 6 | ah3→dnr 06-15; l350, puta, ff7h 06-23; 151r 07-20; 7im4→p9ej 07-18 |
| `contested:unit-semantics` | 6 | 9ma.2→xw6 06-15; ess9.5→v90g 06-27; 1mu→x7j0/y2sm 07-04; 5yde 07-11; iejb→2hzp 07-24; xddq→qszb 07-25 |
| `perf:io-in-loop` | 5 | 0tk→n7u 06-18; 6yoz.13→k7tc 07-02; 66xs→4t0g 07-21; tmzj→ubqb 07-21; hh1f→rsy1 07-28 |
| `out-of-scope:sibling-call-site` | 5 | 66xs→g223 07-21; n9iw→pe7n 07-24; 1oca→9n7l 07-24; gcpb→wcmg 07-27; tzjt→paay 07-27 |
| `arch-decision:event-transactionality` | 5 | so3.3→3n7 06-15; 292 06-15; 9or 06-17; jvzk 07-03; iejb→kw52 07-24 |
| `dup-helper:reporoot` | 4 sites / 3+ cases | copies minted by MoneyFormattingGuard (2x6e.2-era), AmbientClockGuard (lgbu), AlpineXDataRawGuard (qrg7), MigrationTargetsConventionTests (eimm) → one bead plantry-kbrt 07-28 |
| `guard:html-raw-xdata` | 3 | gcpb (fix) 07-27; gcpb→wcmg 07-27; →qrg7 guard bead 07-28 (title: "3rd recurrence") |
| `cache-busting:stale-import` | 3 | 4h3a 06-30; 4h3a→hxkf 06-30; zg08 06-30 (parked) |
| `dup-helper:fixedclock` | 3 sites / 2 cases | PlanCostingServiceTests (pre-existing); lgbu→MealPlanTests copy + FixedIClock → plantry-adie |
| `missing-test-infra:migration-harness` | 4 instances / 2 cases | qszb-p1 07-25 → plantry-2y1r; n3r3-p1/p2 07-27 (restated) → plantry-o4dp widening |
| `migration:dedupe-scope` | 2 | qszb-p3→n3r3 07-25; n3r3→v51h 07-27 |

## 4. Disposition of DEFER-filed beads

Rough bucketing of the ~131 DEFER-language code-review beads (keyword bucketing, hand-corrected;
boundaries are judgment calls, hence `~`):

| Disposition | ~count | ~share | Example beads |
|---|---|---|---|
| (a) product-visible defects / behavior fixes | ~58 | ~44% | plantry-paay, plantry-v51h, plantry-7dja, plantry-fxxh, plantry-c04, plantry-yukq |
| (b) test / guard / observability / process meta-work | ~48 | ~37% | plantry-hdry, plantry-2y1r, plantry-b6sc, plantry-fd0x, plantry-hq7c, plantry-x8ez |
| (c) micro-cleanups (≲10-line fixes) | ~14 | ~11% | plantry-j0d7, plantry-kbrt, plantry-adie, plantry-p9ej, plantry-rrlt, plantry-pbdn |
| (d) genuine design decisions needing the human | ~11 | ~8% | plantry-kw52, plantry-2hzp, plantry-pcfe, plantry-h91d, plantry-8fy, plantry-3n7 |

Supporting signals:

- At least **8 beads required an explicit owner "DECIDED" triage line** before work could start
  (plantry-j4cx, fd0x, g9jq, epzj, hq7c, tr6l, 7vb7, rpg8 — all stamped 2026-07-23), i.e. a
  meaningful slice of nominally (a)/(b) beads were actually blocked on (d)-style human input.
- Of the 189 code-review beads: 164 closed, 20 open, 2 in-progress, 1 parked (zg08),
  1 deferred (n40), 1 blocked (qrg7). The open set is dominated by the 07-27/07-28 clock and
  dedup families.
- Micro-cleanups (c) routinely waited weeks as P2/P3 beads for <10-line diffs (e.g. plantry-dnr
  filed 06-16, the `.shopping-*` deletion).

## 5. Spec-lock companions

Cases where the **originating ticket itself ordered the deferral** — the critic filed rather than
fixed because the spec forbade the fix in-case:

| Bead | Originating case | Lock text (from bead description) |
|---|---|---|
| plantry-j0d7 | plantry-bc2c | "plantry-bc2c's scope decision #3 explicitly forbade deduplicating anything as part of that mechanical move" |
| plantry-adie | plantry-lgbu | "Consolidating them is a worthwhile tidy once both have landed — **do not do it here**, and do not create a shared test project" |
| plantry-kbrt | plantry-qrg7 | "plantry-qrg7 explicitly directed '**reused verbatim**', so the diff follows spec … the ticket sanctioned the copy" |
| plantry-hdry | plantry-10kx | "adding one is a production change plantry-10kx's own **AC4 forbids** ('No behavior change to production code beyond the interface extraction')" |
| plantry-8c4o | plantry-lgbu | "the ticket **explicitly enumerates** the eleven scanned projects and the excluded ones … the author implemented the decided design as specified" |
| plantry-paay | plantry-tzjt | "the ticket's **confirmed spec explicitly scopes** the fix to Cook.cshtml:185 and describes only the 'adding an item' picker" |

Borderline (spec-consequence rather than spec-order): plantry-qszb (the inert `doz` factor is
"the *intended* consequence of AC(b)" of plantry-xddq); plantry-2zbr (signature "deliberately
left open until a real save path exists").

Pattern: spec-locks cluster where a ticket pre-decided scope to keep a mechanical change safe
(bc2c, qrg7, 10kx, lgbu each locked ≥1 companion; lgbu locked two). A spec-lock finding is
**never** the implementer's error — the arbiter must not score it against the case.

## 6. Rulebook implications

Category keys the arbiter should use, ranked by observed frequency, with default rulings under
the plantry-x5kp Part 1 scheme:

| Rank | Class key | Obs. freq | Default ruling |
|---|---|---|---|
| 1 | `coverage:l5-e2e` | 11 | **FILE** — L5 scaffolding is never in-case; batch related filings onto one E2E epic rather than one bead per journey |
| 2 | `component-library:drift` | ~11 | **FIX-IN-CASE** when it's swapping to existing canonical markup; **FILE** when registration/naming needs a design call — with `needs-human` noted for naming collisions like 7p32 (arbiter guardrail 2) |
| 3 | clock family (`missing-seam:iclock`, `missing-seam:timeprovider`, `fixture-clock:fixed`, `guard:ambient-clock`) | ~10 | **FILE**, but onto the *standing* clock-hardening thread — a 10th standalone clock bead is recurrence the guard should absorb; seam additions blocked by an AC are spec-locks (FILE, no fault) |
| 4 | `observability:missing-logger` | 6 | **ABSORB** into the standing structured-logging ticket (plantry-2j5k precedent); FILE separately only when the silent catch hides a product defect |
| 5 | `dead-code:orphan` | 6 | **FIX-IN-CASE** when the case's own diff orphaned it; **FILE** (batched cleanup bead) when pre-existing — never expand a targeted fix to pre-existing cruft (p9ej precedent) |
| 6 | `contested:unit-semantics` | 6 | **FILE with `needs-human` noted** (arbiter guardrail 2 — the arbiter never decides these) — every instance (2hzp, qszb, 5yde, pcfe) ultimately required an owner ratification/ADR |
| 7 | `perf:io-in-loop` | 5 | **FILE** — the fix needs new batch read contracts beyond any case's footprint; P1 if a hot page, else P2 |
| 8 | `out-of-scope:sibling-call-site` | 5 | **FIX-IN-CASE** when the identical, trivially small fix has test coverage and no spec-lock; **FILE** when the spec explicitly scoped it out (paay) — check for a lock before ruling |
| 9 | `arch-decision:event-transactionality` | 5 | **FILE with `needs-human` noted** (arbiter guardrail 2) — outbox/multi-aggregate-save questions are architecture-wide, never case-local |
| 10 | `dup-helper:*` (`reporoot`, `fixedclock`, `expiry-today`) | ~7 across keys | **ABSORB** — one consolidation bead per helper family; **DROP** additional per-case filings once that bead exists (kbrt/adie already cover the two big families) |
| 11 | `guard:html-raw-xdata` (pattern for any recurring bug class) | 3 | Once a bug class demonstrably recurs: **FILE** one guard-test bead that pins the class (qrg7 precedent); afterwards **DROP** per-instance filings — the guard is the fix |
| 12 | `missing-test-infra:migration-harness` | 2 cases | **FILE once**; later findings **ABSORB** into the existing harness bead (o4dp widened 2y1r rather than forking — correct) |
| 13 | `migration:dedupe-scope` / `migration:silent-data-loss` | 2 | **FILE at P1** — these were real data-loss bugs (v51h), not meta-work; do not down-rank because the trigger says missing-test-infra |
| 14 | `test-gap:untested-deliverable` | 1+ | **FIX-IN-CASE** — the deliverable is in-diff by definition; a low-confidence defer here (z2sx) is a protocol miss, not a boundary |
| 15 | `dup-css-rule`, `hardcoded-href:pathbase`, other singletons | 1 each | **FILE at P2/P3** if user-visible risk exists; **DROP** if purely cosmetic and no recurrence key matches |

Cross-cutting rules the data supports:

- **Spec-lock check first.** 6 confirmed spec-lock companions (§5). If the originating ticket
  ordered the deferral, the ruling is FILE with no implementer fault and no re-litigation of the
  lock.
- **Force a named trigger.** 17 beads and 2 report findings carry no trigger label; the arbiter
  should reject unlabelled DEFERs back to the critic.
- **Verify the filing.** 3 report findings claim or imply filing that never happened (§1).
  A DEFER ruling of FILE is complete only when a bead id exists (plantry-h3hb's
  tree-verification requirement).
- **Absorption beats accumulation.** The three largest recurrence families (L5 coverage, clock,
  component library) generated ~32 individual beads; each should have converged on one standing
  thread far earlier. Recurrence detection on these keys is the arbiter's highest-leverage job.
