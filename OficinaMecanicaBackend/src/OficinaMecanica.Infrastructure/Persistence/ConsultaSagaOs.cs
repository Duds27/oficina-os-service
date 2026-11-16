using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Application.Saga;
using OficinaMecanica.Infrastructure.Data;

namespace OficinaMecanica.Infrastructure.Persistence;

public class ConsultaSagaOs : IConsultaSagaOs
{
    private readonly AppDbContext _db;

    public ConsultaSagaOs(AppDbContext db) => _db = db;

    public Task<OrdemServicoSagaState?> ObterPorOsIdAsync(int osId, CancellationToken ct = default) =>
        _db.Set<OrdemServicoSagaState>().AsNoTracking().FirstOrDefaultAsync(s => s.OsId == osId, ct);
}
