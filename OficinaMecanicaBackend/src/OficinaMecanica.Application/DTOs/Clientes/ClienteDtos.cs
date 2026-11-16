namespace OficinaMecanica.Application.DTOs.Clientes;

public record CreateClienteDto(
    string CpfCnpj,
    string Nome,
    string? Email,
    string? Telefone,
    string? Endereco,
    string? Cidade,
    string? Estado
);

public record UpdateClienteDto(
    string Nome,
    string? Email,
    string? Telefone,
    string? Endereco,
    string? Cidade,
    string? Estado,
    bool Ativo = true
);

public record ClienteDto(
    int Id,
    string CpfCnpj,
    string Nome,
    string? Email,
    string? Telefone,
    string? Endereco,
    string? Cidade,
    string? Estado,
    bool Ativo,
    DateTime DataCriacao,
    DateTime? DataAtualizacao
);

/// <summary>
/// Resposta do endpoint interno por CPF, usado pela Lambda de autenticação.
/// Só o necessário para emitir o token: id (claim clienteId), nome e se está ativo.
/// </summary>
public record ClienteInternoDto(int Id, string Nome, bool Ativo);
