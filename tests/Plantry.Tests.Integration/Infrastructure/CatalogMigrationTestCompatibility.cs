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
}
