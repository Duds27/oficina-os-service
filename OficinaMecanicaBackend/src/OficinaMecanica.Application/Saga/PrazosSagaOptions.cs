namespace OficinaMecanica.Application.Saga;

/// <summary>
/// Prazos dos estados de espera da saga (seção <c>Saga:Prazos</c>). 48 h em produção;
/// na demonstração, 2 min (<c>Saga__Prazos__Aprovacao=00:02:00</c>).
/// </summary>
public class PrazosSagaOptions
{
    public const string Secao = "Saga:Prazos";

    /// <summary>Para o cliente aprovar ou recusar o orçamento.</summary>
    public TimeSpan Aprovacao { get; set; } = TimeSpan.FromHours(48);

    /// <summary>Para o pagamento ser confirmado depois da aprovação.</summary>
    public TimeSpan Pagamento { get; set; } = TimeSpan.FromHours(48);

    /// <summary>Para o execution-service iniciar a execução depois do pagamento.</summary>
    public TimeSpan Execucao { get; set; } = TimeSpan.FromHours(48);
}
