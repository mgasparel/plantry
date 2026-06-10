using Plantry.SharedKernel;

namespace Plantry.Intake.Domain;

public interface IImportSessionRepository
{
    Task AddAsync(ImportSession session, CancellationToken ct = default);
    Task AddReceiptAsync(ImportReceipt receipt, CancellationToken ct = default);
    Task<ImportSession?> FindAsync(ImportSessionId sessionId, CancellationToken ct = default);
    Task<ImportReceipt?> FindReceiptAsync(ImportSessionId sessionId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
    Task<List<ImportSession>> ListPendingAsync(HouseholdId householdId, CancellationToken ct = default);
}
