using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plantry.Deals.Infrastructure.Migrations
{
    /// <summary>
    /// plantry-rb36: extends <c>deals.store_subscription</c>'s RLS policy with the same pre-auth carve-out
    /// <c>identity.households</c> already has — rows are visible when <c>app.household_id</c> is unset, in
    /// addition to the normal per-household match. Backs
    /// <see cref="Plantry.Deals.Domain.IStoreSubscriptionRepository.GetLastPulledAtAcrossHouseholdsAsync"/>,
    /// the boot due-check's cross-tenant <c>MAX(last_pulled_at)</c> read: like
    /// <c>IHouseholdRepository.ListAllIdsAsync</c>, it MUST run with no <see cref="Plantry.SharedKernel.Tenancy.TenantContext"/>
    /// armed — an armed request path still sees only its own household's rows.
    /// </summary>
    public partial class AllowCrossHouseholdStoreSubscriptionRead : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP POLICY IF EXISTS household_isolation ON deals.store_subscription;
                CREATE POLICY household_isolation ON deals.store_subscription
                  USING (
                    NULLIF(current_setting('app.household_id', true), '') IS NULL
                    OR household_id = NULLIF(current_setting('app.household_id', true), '')::uuid
                  );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP POLICY IF EXISTS household_isolation ON deals.store_subscription;
                CREATE POLICY household_isolation ON deals.store_subscription
                  USING (household_id = NULLIF(current_setting('app.household_id', true), '')::uuid);
            ");
        }
    }
}
