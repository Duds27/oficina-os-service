# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copia os arquivos de projeto primeiro para aproveitar o cache de restore
COPY OficinaMecanicaBackend.sln ./
COPY src/OficinaMecanica.Domain/OficinaMecanica.Domain.csproj src/OficinaMecanica.Domain/
COPY src/OficinaMecanica.Application/OficinaMecanica.Application.csproj src/OficinaMecanica.Application/
COPY src/OficinaMecanica.Infrastructure/OficinaMecanica.Infrastructure.csproj src/OficinaMecanica.Infrastructure/
COPY src/OficinaMecanica.API/OficinaMecanica.API.csproj src/OficinaMecanica.API/
RUN dotnet restore src/OficinaMecanica.API/OficinaMecanica.API.csproj

# Copia o restante do código e publica
COPY src/ src/
RUN dotnet publish src/OficinaMecanica.API/OficinaMecanica.API.csproj -c Release -o /publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /publish .

# Observabilidade: desde a Fase 4, o OpenTelemetry da própria aplicação manda traces,
# métricas e logs por OTLP (src/OficinaMecanica.API/Observabilidade). O agente .NET do
# New Relic da Fase 3 saiu da imagem para não instrumentar tudo duas vezes.
# SERVICE_VERSION vira o service.version dos traces; o CD passa o SHA do commit.
ARG SERVICE_VERSION=dev
ENV SERVICE_VERSION=${SERVICE_VERSION}

# Executa como usuário não-root 'app' (UID 1654, já presente nas imagens .NET 8).
# Cria o diretório de logs (usado pelo Serilog) e transfere a propriedade dos
# arquivos publicados para esse usuário, garantindo permissão de escrita em runtime.
RUN mkdir -p /app/logs && chown -R app:app /app
USER app

EXPOSE 8080
ENTRYPOINT ["dotnet", "OficinaMecanica.API.dll"]
