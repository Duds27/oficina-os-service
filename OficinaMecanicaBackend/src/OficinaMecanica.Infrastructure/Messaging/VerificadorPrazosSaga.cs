using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oficina.Contracts;
using OficinaMecanica.Application.Saga;
using OficinaMecanica.Infrastructure.Data;

namespace OficinaMecanica.Infrastructure.Messaging;

/// <summary>Seção <c>Saga:VerificadorPrazos</c>.</summary>
public class VerificadorPrazosSagaOptions
{
    public const string Secao = "Saga:VerificadorPrazos";

    /// <summary>De quanto em quanto tempo procurar prazos vencidos.</summary>
    public TimeSpan Intervalo { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Máximo de sagas tratadas por rodada.</summary>
    public int Lote { get; set; } = 50;
}

/// <summary>
/// Publica PrazoExpirado para as sagas com prazo vencido, sem depender de plugin de
/// mensagem atrasada no RabbitMQ (ADR-006). O aviso e a limpeza do prazo são gravados
/// juntos, pelo outbox: se a saga mudar ao mesmo tempo, a concorrência otimista desfaz
/// os dois e a próxima rodada reavalia.
/// </summary>
public class VerificadorPrazosSaga
{
    private readonly IServiceScopeFactory _escopos;
    private readonly IOptions<VerificadorPrazosSagaOptions> _opcoes;
    private readonly ILogger<VerificadorPrazosSaga> _logger;

    public VerificadorPrazosSaga(
        IServiceScopeFactory escopos,
        IOptions<VerificadorPrazosSagaOptions> opcoes,
        ILogger<VerificadorPrazosSaga> logger)
    {
        _escopos = escopos;
        _opcoes = opcoes;
        _logger = logger;
    }

    /// <returns>Quantos avisos de prazo foram gravados nesta rodada.</returns>
    public async Task<int> VerificarAsync(CancellationToken ct = default)
    {
        var agora = DateTime.UtcNow;

        List<Guid> vencidas;
        await using (var escopo = _escopos.CreateAsyncScope())
        {
            var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
            vencidas = await db.Set<OrdemServicoSagaState>().AsNoTracking()
                .Where(s => s.PrazoExpiraEm != null && s.PrazoExpiraEm <= agora)
                .OrderBy(s => s.PrazoExpiraEm)
                .Select(s => s.CorrelationId)
                .Take(_opcoes.Value.Lote)
                .ToListAsync(ct);
        }

        var avisos = 0;
        foreach (var correlationId in vencidas)
        {
            if (await AvisarAsync(correlationId, agora, ct))
                avisos++;
        }
        return avisos;
    }

    private async Task<bool> AvisarAsync(Guid correlationId, DateTime agora, CancellationToken ct)
    {
        await using var escopo = _escopos.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        var publicador = escopo.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        var saga = await db.Set<OrdemServicoSagaState>()
            .SingleOrDefaultAsync(s => s.CorrelationId == correlationId && s.PrazoExpiraEm != null && s.PrazoExpiraEm <= agora, ct);
        if (saga is null)
            return false;

        await publicador.Publish(new PrazoExpirado
        {
            CorrelationId = saga.CorrelationId,
            OsId = saga.OsId,
            NumeroOS = saga.NumeroOS,
            OcorridoEm = agora,
            EstadoNoMomento = saga.CurrentState
        }, ct);
        saga.PrazoExpiraEm = null;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogInformation(
                "Saga {CorrelationId} mudou enquanto o prazo era verificado; reavaliada na próxima rodada.", correlationId);
            return false;
        }

        _logger.LogInformation(
            "Prazo da saga expirado. Evento={Evento} NumeroOS={NumeroOS} Estado={Estado}",
            "saga_prazo_expirado", saga.NumeroOS, saga.CurrentState);
        return true;
    }
}

/// <summary>Roda o <see cref="VerificadorPrazosSaga"/> a cada <see cref="VerificadorPrazosSagaOptions.Intervalo"/>.</summary>
public class VerificadorPrazosSagaHostedService : BackgroundService
{
    private readonly VerificadorPrazosSaga _verificador;
    private readonly IOptions<VerificadorPrazosSagaOptions> _opcoes;
    private readonly ILogger<VerificadorPrazosSagaHostedService> _logger;

    public VerificadorPrazosSagaHostedService(
        VerificadorPrazosSaga verificador,
        IOptions<VerificadorPrazosSagaOptions> opcoes,
        ILogger<VerificadorPrazosSagaHostedService> logger)
    {
        _verificador = verificador;
        _opcoes = opcoes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_opcoes.Value.Intervalo);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await _verificador.VerificarAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Banco fora do ar, por exemplo: tenta de novo na próxima rodada.
                _logger.LogError(ex, "Falha ao verificar prazos da saga. Evento={Evento}", "saga_falha_verificacao_prazos");
            }
        }
    }
}
