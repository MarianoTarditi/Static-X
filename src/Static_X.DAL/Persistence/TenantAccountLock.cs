using Microsoft.EntityFrameworkCore;

namespace DAL;

// A transaction-owned gym-row write serializes invitation, activation and linked ficha-state writers.
// It changes no gym data and avoids provider-specific advisory locks or nullable alternate keys.
public static class TenantAccountLock
{
    public static Task<int> AcquireAsync(StaticXDbContext context, int gymId, CancellationToken cancellationToken) =>
        context.Gimnasios.Where(g => g.Id == gymId)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Name, g => g.Name), cancellationToken);
}
