namespace OficinaMecanica.Domain.Enums;

/// <summary>Quem provocou a mudança de status da OS.</summary>
public enum OrigemMudancaStatus
{
    /// <summary>Ação de um usuário pela API (ex.: entrega ao cliente).</summary>
    Usuario = 0,

    /// <summary>Saga orquestrada, em resposta a um evento de outro serviço.</summary>
    Saga = 1
}
