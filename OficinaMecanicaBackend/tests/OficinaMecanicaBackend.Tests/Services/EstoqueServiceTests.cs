using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Application.Services;
using OficinaMecanica.Domain.Entities;
using OficinaMecanica.Domain.Enums;
using OficinaMecanica.Infrastructure.Data;
using OficinaMecanica.Infrastructure.Persistence;

namespace OficinaMecanicaBackend.Tests.Services;

public class EstoqueServiceTests : IDisposable
{
    private static readonly DateTime Agora = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly EstoqueService _estoque;

    public EstoqueServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _estoque = new EstoqueService(new PecaRepository(_db));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<(OrdemServico os, Peca filtro, Peca oleo)> SeedOsComPecasAsync()
    {
        var cliente = new Cliente { CpfCnpj = "52998224725", Nome = "Teste", DataCriacao = Agora };
        var veiculo = new Veiculo { Placa = "ABC1D23", Marca = "Ford", Modelo = "Ka", Ano = 2020, Cliente = cliente, DataCriacao = Agora };
        var filtro = new Peca { Nome = "Filtro", PrecoUnitario = 40m, QuantidadeEstoque = 8, DataCriacao = Agora };
        var oleo = new Peca { Nome = "Óleo", PrecoUnitario = 30m, QuantidadeEstoque = 16, DataCriacao = Agora };
        var servico = new Servico { Nome = "Troca de óleo", PrecoBase = 100m, DataCriacao = Agora };

        var os = new OrdemServico
        {
            NumeroOS = "OS-2026-000001",
            CorrelationId = Guid.NewGuid(),
            Cliente = cliente,
            Veiculo = veiculo,
            Status = StatusOrdemServico.AguardandoAprovacao,
            DataCriacao = Agora,
            Itens =
            {
                new ItemOrdemServico { Tipo = TipoItemOrdemServico.Peca, Peca = filtro, Quantidade = 2, PrecoUnitario = 40m, PrecoTotal = 80m, DataCriacao = Agora },
                new ItemOrdemServico { Tipo = TipoItemOrdemServico.Peca, Peca = oleo, Quantidade = 4, PrecoUnitario = 30m, PrecoTotal = 120m, DataCriacao = Agora },
                new ItemOrdemServico { Tipo = TipoItemOrdemServico.Servico, Servico = servico, Quantidade = 1, PrecoUnitario = 100m, PrecoTotal = 100m, DataCriacao = Agora }
            }
        };
        _db.OrdensServico.Add(os);
        await _db.SaveChangesAsync();
        return (os, filtro, oleo);
    }

    [Fact]
    public async Task DevolverPecasAsync_DevolveTodasAsPecasEMarcaAOs()
    {
        var (os, filtro, oleo) = await SeedOsComPecasAsync();

        var devolveu = await _estoque.DevolverPecasAsync(os, Agora);
        await _db.SaveChangesAsync();

        Assert.True(devolveu);
        Assert.Equal(10, filtro.QuantidadeEstoque);
        Assert.Equal(20, oleo.QuantidadeEstoque);
        Assert.Equal(Agora, os.PecasDevolvidasEm);
    }

    [Fact]
    public async Task DevolverPecasAsync_SegundaChamada_NaoDevolveDeNovo()
    {
        var (os, filtro, _) = await SeedOsComPecasAsync();

        await _estoque.DevolverPecasAsync(os, Agora);
        var devolveuDeNovo = await _estoque.DevolverPecasAsync(os, Agora.AddMinutes(1));

        Assert.False(devolveuDeNovo);
        Assert.Equal(10, filtro.QuantidadeEstoque);
        Assert.Equal(Agora, os.PecasDevolvidasEm);
    }

    [Fact]
    public async Task DevolverItemAsync_ItemDeServico_NaoAlteraEstoque()
    {
        var (os, filtro, oleo) = await SeedOsComPecasAsync();
        var itemServico = os.Itens.Single(i => i.Tipo == TipoItemOrdemServico.Servico);

        await _estoque.DevolverItemAsync(itemServico, Agora);

        Assert.Equal(8, filtro.QuantidadeEstoque);
        Assert.Equal(16, oleo.QuantidadeEstoque);
    }
}
