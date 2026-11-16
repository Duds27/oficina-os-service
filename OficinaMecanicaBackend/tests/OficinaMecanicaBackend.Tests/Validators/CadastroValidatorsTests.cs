using FluentValidation;
using OficinaMecanica.Application.DTOs.Clientes;
using OficinaMecanica.Application.DTOs.Pecas;
using OficinaMecanica.Application.DTOs.Servicos;
using OficinaMecanica.Application.DTOs.Veiculos;
using OficinaMecanica.Application.Validators;

namespace OficinaMecanicaBackend.Tests.Validators;

/// <summary>Regras de entrada dos cadastros (FluentValidation, executado antes do controller).</summary>
public class CadastroValidatorsTests
{
    private static IReadOnlyList<string> Erros<T>(IValidator<T> validator, T dto) =>
        validator.Validate(dto).Errors.Select(e => e.PropertyName).Distinct().ToList();

    // ────── Cliente ──────

    private static CreateClienteDto ClienteValido => new("529.982.247-25", "Maria Souza", "maria@example.com", "11999990000", null, null, null);

    [Fact]
    public void CreateCliente_Valido_NaoTemErros() =>
        Assert.Empty(Erros(new CreateClienteValidator(), ClienteValido));

    [Fact]
    public void CreateCliente_CamposInvalidos_ApontaCadaCampo()
    {
        var dto = ClienteValido with { CpfCnpj = "123", Nome = "", Email = "nao-e-email", Telefone = new string('9', 21) };

        Assert.Equal(["CpfCnpj", "Nome", "Email", "Telefone"], Erros(new CreateClienteValidator(), dto));
    }

    [Fact]
    public void CreateCliente_SemEmailETelefone_EValido() =>
        Assert.Empty(Erros(new CreateClienteValidator(), ClienteValido with { Email = null, Telefone = null }));

    [Fact]
    public void UpdateCliente_Valida()
    {
        var validator = new UpdateClienteValidator();

        Assert.Empty(Erros(validator, new UpdateClienteDto("Maria", null, null, null, null, null)));
        Assert.Equal(["Nome", "Email", "Telefone"],
            Erros(validator, new UpdateClienteDto(new string('x', 201), "x@", new string('1', 21), null, null, null)));
    }

    // ────── Veículo ──────

    private static CreateVeiculoDto VeiculoValido => new("ABC1D23", "Ford", "Ka", 2020, "Prata", null, 1);

    [Fact]
    public void CreateVeiculo_Valido_NaoTemErros() =>
        Assert.Empty(Erros(new CreateVeiculoValidator(), VeiculoValido));

    [Fact]
    public void CreateVeiculo_CamposInvalidos_ApontaCadaCampo()
    {
        var dto = VeiculoValido with { Placa = "12", Marca = "", Modelo = new string('m', 101), Ano = 1800, ClienteId = 0 };

        Assert.Equal(["Placa", "Marca", "Modelo", "Ano", "ClienteId"], Erros(new CreateVeiculoValidator(), dto));
    }

    [Theory]
    [InlineData(1899, false)]
    [InlineData(1900, true)]
    [InlineData(2100, false)]
    public void UpdateVeiculo_AnoNoIntervalo(int ano, bool valido)
    {
        var erros = Erros(new UpdateVeiculoValidator(), new UpdateVeiculoDto("Ford", "Ka", ano, null, null));

        Assert.Equal(valido, !erros.Contains("Ano"));
    }

    [Fact]
    public void UpdateVeiculo_MarcaEModeloObrigatorios() =>
        Assert.Equal(["Marca", "Modelo"], Erros(new UpdateVeiculoValidator(), new UpdateVeiculoDto("", "", 2020, null, null)));

    // ────── Peça ──────

    private static CreatePecaDto PecaValida => new("Filtro de óleo", null, "FO-01", 45.90m, 10, 2, true);

    [Fact]
    public void CreatePeca_Valida_NaoTemErros() =>
        Assert.Empty(Erros(new CreatePecaValidator(), PecaValida));

    [Fact]
    public void CreatePeca_CamposInvalidos_ApontaCadaCampo()
    {
        var dto = PecaValida with { Nome = "", Codigo = new string('c', 51), PrecoUnitario = 0, QuantidadeEstoque = -1, QuantidadeMinima = -1 };

        Assert.Equal(["Nome", "Codigo", "PrecoUnitario", "QuantidadeEstoque", "QuantidadeMinima"], Erros(new CreatePecaValidator(), dto));
    }

    [Fact]
    public void UpdatePeca_Valida()
    {
        var validator = new UpdatePecaValidator();

        Assert.Empty(Erros(validator, new UpdatePecaDto("Filtro", null, null, 10m, 0, 0, true)));
        Assert.Equal(["Nome", "Codigo", "PrecoUnitario", "QuantidadeEstoque", "QuantidadeMinima"],
            Erros(validator, new UpdatePecaDto("", null, new string('c', 51), -1m, -5, -5, true)));
    }

    // ────── Serviço ──────

    [Fact]
    public void CreateServico_Valida()
    {
        var validator = new CreateServicoValidator();

        Assert.Empty(Erros(validator, new CreateServicoDto("Troca de óleo", null, 120m, 1.5m, true)));
        Assert.Empty(Erros(validator, new CreateServicoDto("Troca de óleo", null, 120m, null, true)));
        Assert.Equal(["Nome", "PrecoBase", "TempoEstimadoHoras"],
            Erros(validator, new CreateServicoDto("", null, 0m, 0m, true)));
    }

    [Fact]
    public void UpdateServico_Valida()
    {
        var validator = new UpdateServicoValidator();

        Assert.Empty(Erros(validator, new UpdateServicoDto("Alinhamento", null, 80m, null, true)));
        Assert.Equal(["Nome", "PrecoBase", "TempoEstimadoHoras"],
            Erros(validator, new UpdateServicoDto(new string('n', 201), null, -1m, -1m, false)));
    }
}
