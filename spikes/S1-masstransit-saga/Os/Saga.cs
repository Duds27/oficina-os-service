using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OficinaMecanica.Domain.Enums;
using OficinaMecanica.Infrastructure.Data;
using Spike.Os.Contracts;

namespace Spike.Os;

public class OrdemServicoSagaState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = string.Empty;
    public int OsId { get; set; }
    public string NumeroOS { get; set; } = string.Empty;
    public DateTime? PrazoExpiraEm { get; set; }
    public DateTime CriadoEm { get; set; }
    public DateTime? AtualizadoEm { get; set; }

    /// <summary>
    /// Concorrência otimista no MySQL: o Pomelo mapeia DateTime + IsRowVersion() para
    /// timestamp(6) com ON UPDATE CURRENT_TIMESTAMP(6). O banco troca o valor a cada UPDATE.
    /// </summary>
    public DateTime RowVersion { get; set; }
}

public class OrdemServicoSagaStateMap : SagaClassMap<OrdemServicoSagaState>
{
    protected override void Configure(EntityTypeBuilder<OrdemServicoSagaState> entity, ModelBuilder model)
    {
        entity.ToTable("SagaOrdemServico");
        entity.Property(x => x.CurrentState).HasMaxLength(64);
        entity.Property(x => x.NumeroOS).HasMaxLength(50);
        entity.Property(x => x.RowVersion).IsRowVersion();
        entity.HasIndex(x => x.OsId).IsUnique();
    }
}

/// <summary>
/// Acrescenta ao modelo do AppDbContext atual as tabelas do MassTransit (inbox/outbox) e da saga,
/// sem alterar a classe. Na Etapa 1 estas linhas vão direto para o OnModelCreating.
/// </summary>
public class SagaModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
        new OrdemServicoSagaStateMap().Configure(modelBuilder);
    }
}

public class OrdemServicoStateMachine : MassTransitStateMachine<OrdemServicoSagaState>
{
    /// <summary>Fila de comando do execution-service (docs/contratos-mensagens.md, seção 3).</summary>
    public static readonly Uri FilaSolicitarDiagnostico = new("queue:execution-solicitar-diagnostico");

    public State AguardandoDiagnostico { get; private set; } = null!;
    public State EmDiagnostico { get; private set; } = null!;

    public Event<OrdemServicoAberta> OrdemServicoAberta { get; private set; } = null!;
    public Event<DiagnosticoIniciado> DiagnosticoIniciado { get; private set; } = null!;

    public OrdemServicoStateMachine()
    {
        InstanceState(x => x.CurrentState);

        Event(() => OrdemServicoAberta, e => e.CorrelateById(c => c.Message.CorrelationId));
        Event(() => DiagnosticoIniciado, e => e.CorrelateById(c => c.Message.CorrelationId));

        Initially(
            When(OrdemServicoAberta)
                .ThenAsync(async c =>
                {
                    // Mesmo DbContext do escopo da mensagem: o mesmo usado pelo repositório da saga.
                    var db = c.GetServiceOrCreateInstance<AppDbContext>();
                    var os = await db.OrdensServico
                        .Include(o => o.Veiculo)
                        .SingleAsync(o => o.Id == c.Message.OsId);

                    c.Saga.OsId = os.Id;
                    c.Saga.NumeroOS = os.NumeroOS;
                    c.Saga.CriadoEm = DateTime.UtcNow;

                    var destino = await c.GetSendEndpoint(FilaSolicitarDiagnostico);
                    await destino.Send(new SolicitarDiagnostico
                    {
                        CorrelationId = c.Saga.CorrelationId,
                        OsId = os.Id,
                        NumeroOS = os.NumeroOS,
                        OcorridoEm = DateTime.UtcNow,
                        Descricao = os.Descricao,
                        Veiculo = new VeiculoInfo
                        {
                            Placa = os.Veiculo.Placa,
                            Marca = os.Veiculo.Marca,
                            Modelo = os.Veiculo.Modelo,
                            Ano = os.Veiculo.Ano
                        }
                    });
                })
                .TransitionTo(AguardandoDiagnostico));

        During(AguardandoDiagnostico,
            When(DiagnosticoIniciado)
                .ThenAsync(async c =>
                {
                    // Muda o status da OS na mesma transação em que a saga muda de estado.
                    var db = c.GetServiceOrCreateInstance<AppDbContext>();
                    var os = await db.OrdensServico.SingleAsync(o => o.Id == c.Saga.OsId);
                    os.Status = StatusOrdemServico.EmDiagnostico;
                    os.DataAtualizacao = DateTime.UtcNow;
                    c.Saga.AtualizadoEm = DateTime.UtcNow;
                })
                .TransitionTo(EmDiagnostico));

        During(EmDiagnostico, Ignore(DiagnosticoIniciado));
    }
}
