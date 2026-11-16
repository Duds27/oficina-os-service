using MassTransit;
using MassTransit.Testing;
using Oficina.Contracts;

namespace OficinaMecanicaBackend.Tests.Infrastructure;

/// <summary>
/// Consumidor que só registra o comando no harness. Ocupa a fila do serviço dono
/// (billing ou execution) para os testes verem o que a saga enviou.
/// </summary>
public class ComandoCapturado<T> : IConsumer<T> where T : class
{
    public Task Consume(ConsumeContext<T> context) => Task.CompletedTask;
}

public static class HarnessEspera
{
    private static readonly TimeSpan Padrao = TimeSpan.FromSeconds(20);

    /// <summary>Registra um consumidor de captura em cada fila de comando da saga.</summary>
    public static void AddCapturaDeComandos(this IBusRegistrationConfigurator x)
    {
        x.AddConsumer<ComandoCapturado<SolicitarDiagnostico>>().Endpoint(e => e.Name = "execution-solicitar-diagnostico");
        x.AddConsumer<ComandoCapturado<IniciarExecucao>>().Endpoint(e => e.Name = "execution-iniciar-execucao");
        x.AddConsumer<ComandoCapturado<CancelarExecucao>>().Endpoint(e => e.Name = "execution-cancelar-execucao");
        x.AddConsumer<ComandoCapturado<GerarOrcamento>>().Endpoint(e => e.Name = "billing-gerar-orcamento");
        x.AddConsumer<ComandoCapturado<CancelarOrcamento>>().Endpoint(e => e.Name = "billing-cancelar-orcamento");
        x.AddConsumer<ComandoCapturado<EstornarPagamento>>().Endpoint(e => e.Name = "billing-estornar-pagamento");
    }

    /// <summary>
    /// Espera uma mensagem consumida que satisfaça o filtro. Espera própria: o Any do
    /// harness deixa de esperar depois da primeira janela de inatividade do bus.
    /// </summary>
    public static Task<T> EsperarConsumidaAsync<T>(this ITestHarness harness, Func<T, bool> filtro, TimeSpan? espera = null)
        where T : class =>
        EsperarAsync(() => harness.Consumed.Select<T>().Select(m => m.Context.Message).FirstOrDefault(filtro),
            $"{typeof(T).Name} consumida", espera);

    public static Task<T> EsperarPublicadaAsync<T>(this ITestHarness harness, Func<T, bool> filtro, TimeSpan? espera = null)
        where T : class =>
        EsperarAsync(() => harness.Published.Select<T>().Select(m => m.Context.Message).FirstOrDefault(filtro),
            $"{typeof(T).Name} publicada", espera);

    /// <summary>Mensagens já consumidas, sem esperar.</summary>
    public static IReadOnlyList<T> Consumidas<T>(this ITestHarness harness, Func<T, bool> filtro) where T : class =>
        harness.Consumed.Select<T>().Select(m => m.Context.Message).Where(filtro).ToList();

    private static async Task<T> EsperarAsync<T>(Func<T?> buscar, string descricao, TimeSpan? espera) where T : class
    {
        var limite = DateTime.UtcNow + (espera ?? Padrao);
        while (true)
        {
            var encontrada = buscar();
            if (encontrada is not null)
                return encontrada;
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"Tempo esgotado esperando {descricao}.");
            await Task.Delay(50);
        }
    }
}
