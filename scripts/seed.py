"""Popula o os-service com dados de exemplo: serviços, peças, clientes e veículos.

Só usa a biblioteca padrão do Python. Pode rodar de novo sem duplicar nada: cada item é
procurado antes (serviço pelo nome, peça pelo código, cliente pelo CPF, veículo pela placa).

    python scripts/seed.py                       # os-service em http://localhost:8080
    python scripts/seed.py --url http://host:porta
    docker compose -f docker-compose.full.yml --profile seed run --rm seed

O login é o do admin da equipe (Jwt:AdminUsername / Jwt:AdminPassword). Os padrões são os do
appsettings.json de desenvolvimento; use --usuario/--senha ou as variáveis ADMIN_USER e
ADMIN_PASSWORD em outro ambiente.
"""
import argparse
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request

SERVICOS = [
    {"nome": "Troca de óleo", "descricao": "Óleo e filtro", "precoBase": 120.00, "tempoEstimadoHoras": 1, "ativo": True},
    {"nome": "Troca de pastilhas", "descricao": "Pastilhas dianteiras", "precoBase": 150.00, "tempoEstimadoHoras": 1, "ativo": True},
    {"nome": "Alinhamento e balanceamento", "descricao": "Quatro rodas", "precoBase": 180.00, "tempoEstimadoHoras": 1.5, "ativo": True},
]

PECAS = [
    {"nome": "Óleo 5W30 (litro)", "descricao": "Sintético", "codigo": "OLEO-5W30", "precoUnitario": 45.90, "quantidadeEstoque": 40, "quantidadeMinima": 10, "ativo": True},
    {"nome": "Filtro de óleo", "descricao": "Rosca padrão", "codigo": "FILT-OLEO", "precoUnitario": 39.90, "quantidadeEstoque": 20, "quantidadeMinima": 5, "ativo": True},
    {"nome": "Pastilha de freio (jogo)", "descricao": "Dianteira", "codigo": "PAST-DIANT", "precoUnitario": 189.90, "quantidadeEstoque": 10, "quantidadeMinima": 2, "ativo": True},
]

# CPFs válidos, de teste. O e-mail recebe o orçamento (simulado) do billing-service.
CLIENTES = [
    {
        "cliente": {"cpfCnpj": "529.982.247-25", "nome": "Maria Souza", "email": "maria@example.com", "telefone": "11999990001", "cidade": "São Paulo", "estado": "SP"},
        "veiculo": {"placa": "ABC1D23", "marca": "Volkswagen", "modelo": "Gol", "ano": 2020, "cor": "Prata"},
    },
    {
        "cliente": {"cpfCnpj": "111.444.777-35", "nome": "João Lima", "email": "joao@example.com", "telefone": "11999990002", "cidade": "Campinas", "estado": "SP"},
        "veiculo": {"placa": "BRA2E19", "marca": "Fiat", "modelo": "Argo", "ano": 2022, "cor": "Branco"},
    },
]


def so_digitos(texto):
    return re.sub(r"\D", "", texto or "")


def so_alfanumerico(texto):
    return re.sub(r"[^A-Za-z0-9]", "", texto or "").upper()


class Api:
    def __init__(self, url):
        self.url = url.rstrip("/")
        self.token = None

    def chamar(self, metodo, caminho, corpo=None):
        req = urllib.request.Request(
            self.url + caminho,
            method=metodo,
            data=None if corpo is None else json.dumps(corpo).encode(),
            headers={"Content-Type": "application/json"},
        )
        if self.token:
            req.add_header("Authorization", "Bearer " + self.token)
        try:
            with urllib.request.urlopen(req, timeout=15) as r:
                texto = r.read().decode()
                return r.status, json.loads(texto) if texto else None
        except urllib.error.HTTPError as e:
            texto = e.read().decode()
            try:
                return e.code, json.loads(texto)
            except ValueError:
                return e.code, texto

    def esperar_pronto(self, segundos):
        fim = time.time() + segundos
        while time.time() < fim:
            try:
                with urllib.request.urlopen(self.url + "/health/ready", timeout=5) as r:
                    if r.status == 200:
                        return
            except (urllib.error.URLError, OSError):
                pass
            time.sleep(2)
        sys.exit(f"O os-service em {self.url} não ficou pronto em {segundos} s.")

    def listar(self, caminho):
        status, corpo = self.chamar("GET", caminho)
        if status != 200:
            sys.exit(f"GET {caminho} respondeu {status}: {corpo}")
        return corpo if isinstance(corpo, list) else corpo.get("itens", corpo.get("items", []))


def garantir(api, caminho, corpo, ja_existe, rotulo):
    existente = next((x for x in api.listar(caminho) if ja_existe(x)), None)
    if existente:
        print(f"  já existe  {rotulo}")
        return existente
    status, criado = api.chamar("POST", caminho, corpo)
    if status not in (200, 201):
        sys.exit(f"POST {caminho} ({rotulo}) respondeu {status}: {criado}")
    print(f"  criado     {rotulo}")
    return criado


def main():
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--url", default=os.environ.get("OS_SERVICE_URL", "http://localhost:8080"))
    p.add_argument("--usuario", default=os.environ.get("ADMIN_USER", "admin"))
    p.add_argument("--senha", default=os.environ.get("ADMIN_PASSWORD", "Admin@123"))
    p.add_argument("--esperar", type=int, default=120, help="segundos esperando o /health/ready")
    args = p.parse_args()

    api = Api(args.url)
    api.esperar_pronto(args.esperar)
    status, login = api.chamar("POST", "/api/auth/login", {"username": args.usuario, "password": args.senha})
    if status != 200:
        sys.exit(f"Login do admin falhou ({status}). Confira --usuario/--senha.")
    api.token = login["token"]

    print("Serviços")
    for s in SERVICOS:
        garantir(api, "/api/servicos", s, lambda x, s=s: x.get("nome") == s["nome"], s["nome"])

    print("Peças")
    for pc in PECAS:
        garantir(api, "/api/pecas", pc, lambda x, pc=pc: x.get("codigo") == pc["codigo"], f'{pc["codigo"]} {pc["nome"]}')

    print("Clientes e veículos")
    for item in CLIENTES:
        c = item["cliente"]
        cliente = garantir(api, "/api/clientes", c, lambda x, c=c: so_digitos(x.get("cpfCnpj")) == so_digitos(c["cpfCnpj"]), f'{c["nome"]} ({c["cpfCnpj"]})')
        v = dict(item["veiculo"], clienteId=cliente["id"])
        veiculo = garantir(api, "/api/veiculos", v, lambda x, v=v: so_alfanumerico(x.get("placa")) == so_alfanumerico(v["placa"]), f'{v["placa"]} {v["marca"]} {v["modelo"]}')
        print(f'             clienteId={cliente["id"]} veiculoId={veiculo["id"]}')

    print("\nPronto. Para abrir uma OS: POST /api/ordens-servico com um clienteId e um veiculoId acima.")
    print(f"Na AWS, o cliente entra pelo POST /auth da Lambda com o CPF {CLIENTES[0]['cliente']['cpfCnpj']}.")


if __name__ == "__main__":
    main()
