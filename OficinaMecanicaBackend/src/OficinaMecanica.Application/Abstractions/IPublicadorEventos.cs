namespace OficinaMecanica.Application.Abstractions;

/// <summary>
/// Publica eventos pelo outbox: a mensagem é gravada na mesma unidade de trabalho
/// e só sai para o broker depois do commit. Sem <see cref="IUnitOfWork.SalvarAsync"/>,
/// nada é publicado.
/// </summary>
public interface IPublicadorEventos
{
    Task PublicarAsync<T>(T evento, CancellationToken ct = default) where T : class;
}
