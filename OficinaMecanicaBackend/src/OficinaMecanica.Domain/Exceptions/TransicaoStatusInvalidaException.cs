using OficinaMecanica.Domain.Enums;

namespace OficinaMecanica.Domain.Exceptions;

/// <summary>Tentativa de mudar a OS para um status que o fluxo não permite a partir do atual.</summary>
public class TransicaoStatusInvalidaException : InvalidOperationException
{
    public TransicaoStatusInvalidaException(StatusOrdemServico atual, StatusOrdemServico novo, string mensagem)
        : base(mensagem)
    {
        StatusAtual = atual;
        StatusNovo = novo;
    }

    public StatusOrdemServico StatusAtual { get; }
    public StatusOrdemServico StatusNovo { get; }
}
