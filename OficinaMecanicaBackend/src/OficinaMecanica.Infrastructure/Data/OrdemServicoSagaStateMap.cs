using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OficinaMecanica.Application.Saga;

namespace OficinaMecanica.Infrastructure.Data;

/// <summary>Tabela da saga da OS no MySQL (mesmo AppDbContext da OS e do outbox).</summary>
public class OrdemServicoSagaStateMap : SagaClassMap<OrdemServicoSagaState>
{
    protected override void Configure(EntityTypeBuilder<OrdemServicoSagaState> entity, ModelBuilder model)
    {
        entity.ToTable("SagaOrdemServico");
        entity.Property(x => x.CurrentState).HasMaxLength(64);
        entity.Property(x => x.NumeroOS).HasMaxLength(50);
        entity.Property(x => x.LinkPagamento).HasMaxLength(500);
        entity.Property(x => x.MotivoCancelamento).HasMaxLength(500);

        // Validado no spike S1: timestamp(6) ... ON UPDATE CURRENT_TIMESTAMP(6).
        entity.Property(x => x.RowVersion).IsRowVersion();

        entity.HasIndex(x => x.OsId).IsUnique().HasDatabaseName("IX_SagaOrdemServico_OsId");
        // Consulta do VerificadorPrazosSaga.
        entity.HasIndex(x => x.PrazoExpiraEm).HasDatabaseName("IX_SagaOrdemServico_PrazoExpiraEm");
    }
}
