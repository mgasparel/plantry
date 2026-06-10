namespace Plantry.Intake.Application;

/// <summary>
/// Port: cross-context call to Catalog to create a new product on-the-fly during commit.
/// Implemented in Plantry.Web (adapter over Catalog's CreateProductCommand).
/// </summary>
public interface ICreateProductPort
{
    Task<Guid> CreateAsync(string name, Guid categoryId, Guid defaultUnitId, CancellationToken ct = default);
}
