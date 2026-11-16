namespace OficinaMecanica.Domain.Enums;

/// <summary>
/// Status da OS. É gravado como inteiro (HasConversion&lt;int&gt;), então os valores
/// existentes nunca podem ser renumerados: status novos entram sempre no fim.
/// </summary>
public enum StatusOrdemServico
{
    Recebida = 0,
    EmDiagnostico = 1,
    AguardandoAprovacao = 2,
    EmExecucao = 3,
    Finalizada = 4,
    Entregue = 5,
    AguardandoPagamento = 6,
    Cancelada = 7
}
