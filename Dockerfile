# --- Estágio de Compilação ---
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Instala dependências nativas necessárias para compilar AOT (clang, zlib)
RUN apt-get update && apt-get install -y clang zlib1g-dev

# Copia e restaura dependências
COPY ["MyHighPerfApp.csproj", "./"]
RUN dotnet restore "MyHighPerfApp.csproj"

# Copia o restante do código
COPY . .

# Publica em modo Release com AOT para Linux x64
# O flag -o define a saída para /app/publish
RUN dotnet publish "MyHighPerfApp.csproj" -c Release -r linux-x64 -o /app/publish /p:PublishAot=true

# --- Estágio Final (Runtime) ---
# Usa a imagem 'runtime-deps' versão 'chiseled' (Ubuntu talhado).
# Esta imagem contém apenas as dependências nativas mínimas para rodar binários AOT.
# Sem shell, sem gerenciador de pacotes, sem root (segurança máxima).
FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-jammy AS final
WORKDIR /app

# Instala dependências de globalização (ICU) exigidas pelo SqlClient
RUN apt-get update && apt-get install -y --no-install-recommends libicu70 && rm -rf /var/lib/apt/lists/*

# Copia o executável nativo do estágio de build
COPY --from=build /app/publish/MyHighPerfApp .

# Configurações de ambiente para otimização
# Informa ao .NET que está em contêiner (ajuda no cálculo de heurísticas de GC)
ENV DOTNET_RunningInContainer=true
# Desabilita globalização para garantir consistência com a flag do csproj
# ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

# Define o ponto de entrada. Como é nativo, chamamos o executável diretamente.
ENTRYPOINT ["./MyHighPerfApp"]
