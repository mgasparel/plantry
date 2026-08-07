using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Pantry.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BackfillCookReasonOnHistoricalYields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // plantry-a45c (arbiter FIX-IN-CASE, KEY: migration:journal-label-backfill): relabels
            // historical cook-produced journal rows that were stamped Reason='Purchase' before
            // AddCookReason introduced the dedicated 'Cook' addition reason — including the Chicken
            // Gyros row named in the ticket's Context. Label-only relabel during schema evolution,
            // same shape as 20260727062530_RelabelPackAndDozenUnitReferences (unit_id relabel on this
            // same table); no delta/quantity/entry-identity/occurred_at/household_id column is
            // touched, so this is not the kind of ledger mutation Gate 7 / ADR-011 immutability
            // forbids. See ADR-011's 2026-08-07 amendment for the recorded rationale.
            migrationBuilder.Sql(
                "UPDATE inventory.stock_journal_entry " +
                "SET reason = 'Cook' " +
                "WHERE reason = 'Purchase' AND source_type = 'Cook';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversing relabel so AddCookReason.Down's narrower CHECK constraint (which no longer
            // allows 'Cook') can re-apply over historical rows on rollback. Same predicate as Up.
            // Residual: this also relabels any cook-yield rows added *after* this migration ran
            // (post-fix Cook rows), rewriting them back to Purchase on rollback — an acceptable
            // residual of the same shape as the AddAmendmentReason precedent's Down.
            migrationBuilder.Sql(
                "UPDATE inventory.stock_journal_entry " +
                "SET reason = 'Purchase' " +
                "WHERE reason = 'Cook' AND source_type = 'Cook';");
        }
    }
}
