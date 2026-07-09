FROM mcr.microsoft.com/dotnet/sdk:10.0.301 AS build
WORKDIR /src

COPY . .
RUN dotnet publish src/0.CompositionRoots/KnowledgeServer.Worker/KnowledgeServer.Worker.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/sdk:10.0.301
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-pip python3-venv \
    && python3 -m venv /opt/graphify \
    && /opt/graphify/bin/pip install --no-cache-dir --upgrade pip \
    && /opt/graphify/bin/pip install --no-cache-dir graphifyy==0.9.11 \
    && ln -s /opt/graphify/bin/graphify /usr/local/bin/graphify \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "KnowledgeServer.Worker.dll"]
