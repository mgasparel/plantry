using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductConversionUnorderedPairUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADR-022 amendment (plantry-pcfe): a product may hold at most one conversion per
            // UNORDERED unit pair {from_unit_id, to_unit_id} — UnitConverter.BuildConversionGraph
            // already adds both directions from a single stored row (forward multiplies by Factor,
            // reverse divides by it), so a second row in the reverse direction is not a duplicate,
            // it is a contradiction that leaves UnitConverter.Convert's BFS to nondeterministically
            // pick whichever edge it discovers first. Product.AddConversion/PromoteConversion now
            // enforce this pair-unordered invariant going forward (in-memory, on the aggregate);
            // this migration (a) collapses any pre-existing violation and (b) adds a unique
            // expression index so the database enforces it too.
            //
            // (a) Canonicalising dedupe — NEWEST wins. For each (product_id, unordered pair) group
            // with more than one row, keep the highest id and delete the rest. ProductConversionId
            // is UUIDv7 (time-ordered), so highest id = most recently written = the user's latest
            // correction for that pair. This is the deliberate INVERSE of
            // 20260727061526_RemovePackAndDozenUnits's backstop dedupe, which keeps the LOWEST id
            // (the longest-standing row) when collapsing pk/doz-relabel collisions — that backstop
            // has no domain-authorship signal to prefer (both colliding rows are equally
            // relabel-derived), so it falls back to "oldest wins" as a stable tie-break. Here the
            // group can include genuine, deliberate user re-entries of the same pair (exactly what
            // Product.AddConversion's new replace-on-confirm rule targets), so the newest row is the
            // one carrying the user's actual current intent. The two migrations are not
            // inconsistent — each picks the survivor that best represents "the truth" for its own
            // scenario.
            migrationBuilder.Sql(
                "DELETE FROM catalog.product_conversions c " +
                "USING catalog.product_conversions o " +
                "WHERE o.product_id = c.product_id " +
                "  AND LEAST(o.from_unit_id, o.to_unit_id) = LEAST(c.from_unit_id, c.to_unit_id) " +
                "  AND GREATEST(o.from_unit_id, o.to_unit_id) = GREATEST(c.from_unit_id, c.to_unit_id) " +
                "  AND o.id > c.id;");

            // (b) Unique expression index over the canonicalised (LEAST, GREATEST) pair. LEAST/GREATEST
            // work directly on uuid in Postgres (uuid has a total btree ordering), so no schema change
            // or data rewrite is needed to express "unordered pair" uniqueness.
            //
            // EF CAVEAT: an expression index cannot be declared through HasIndex/fluent API, so it is
            // raw SQL here and CatalogDbContext/CatalogDbContextModelSnapshot deliberately do NOT
            // represent it (a plain HasIndex on the ordered triple would be redundant with the
            // existing ProductId index and would wrongly imply ordered-triple semantics — see
            // Product.AddConversion's unordered-pair merge rule). A future model diff must not try to
            // "restore" this as a plain composite index.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_product_conversions_product_unordered_pair " +
                "ON catalog.product_conversions (product_id, LEAST(from_unit_id, to_unit_id), GREATEST(from_unit_id, to_unit_id));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS catalog.ix_product_conversions_product_unordered_pair;");

            // The dedupe in Up() is data-lossy (mirrors AddUnitSystem/AddServingUnit/
            // RemovePackAndDozenUnits' Down convention) — collapsed duplicate rows are not
            // recoverable, only the index is reversed.
        }
    }
}
