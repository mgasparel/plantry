using Microsoft.EntityFrameworkCore;
using Plantry.Pantry.Infrastructure;

namespace Plantry.Tests.Integration.Infrastructure;

/// <summary>
/// Keeps historical Catalog migration tests writable with the current Catalog EF model. Those
/// tests deliberately stop the database before the latest migration, while the current model now
/// maps the nullable Never-expiry columns introduced by that later migration.
/// </summary>
internal static class CatalogMigrationTestCompatibility
{
    public static Task AddProductNeverExpiryColumnsAsync(CatalogDbContext db) =>
        db.Database.ExecuteSqlRawAsync(
            """
            ALTER TABLE catalog.products
                ADD COLUMN IF NOT EXISTS never_expires_after_freezing boolean NULL,
                ADD COLUMN IF NOT EXISTS never_expires_after_thawing boolean NULL;
            """);

    /// <summary>
    /// Same bridge as <see cref="AddProductNeverExpiryColumnsAsync"/>, for the <c>is_produced</c>
    /// column introduced by <c>20260805210929_AddProductIsProduced</c> (plantry-sn6v).
    /// </summary>
    public static Task AddProductIsProducedColumnAsync(CatalogDbContext db) =>
        db.Database.ExecuteSqlRawAsync(
            """
            ALTER TABLE catalog.products
                ADD COLUMN IF NOT EXISTS is_produced boolean NOT NULL DEFAULT false;
            """);
}
