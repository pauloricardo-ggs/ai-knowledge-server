# AI Knowledge Server

Servidor local de conhecimento para projetos de software e documentos de negócio. Ele expõe conhecimento para interfaces humanas, como um chat privado, e para agentes de IA via MCP.

## Estado Atual

Este repositório contém um MVP funcional:

- API HTTP em .NET
- Worker que observa alterações em `workspaces/`
- Store inicial baseada em filesystem
- Upload e listagem de documentos
- UI web privada em `/ui`
- Gateway MCP HTTP JSON-RPC em `/mcp`
- API compatível com OpenAI em `/v1`
- Open WebUI no Compose para chat humano pronto
- Endpoint de chat com RAG via Qdrant e geração local via Ollama
- Pull automático dos modelos configurados no Ollama
- Indexação de chunks e embeddings
- Análise determinística de C# em `workspaces/<id>/roslyn`
- Geração de grafo via Graphify quando disponível, com fallback local
- Docker Compose com API, watcher, PostgreSQL, Qdrant e Ollama

Limitações atuais: a análise C# é determinística por fonte/regex, não Roslyn compiler-semantic com MSBuildWorkspace; os jobs ainda são arquivos em filesystem, não PostgreSQL; autenticação/permissões ainda não foram implementadas.

## Requisitos

- Docker
- Docker Compose
- Driver NVIDIA e NVIDIA Container Toolkit, apenas se for usar GPU com Ollama

Para desenvolvimento local sem Docker:

- .NET SDK 10

## Rodando com Docker

As imagens do Compose e dos Dockerfiles usam tags fixas. Não usamos `latest` para evitar atualização implícita em produção.

Crie o arquivo de ambiente:

```bash
cp .env.example .env
```

Suba os serviços:

```bash
docker compose up --build
```

API:

```text
http://localhost:8080
```

Open WebUI:

```text
http://localhost:3000
```

UI técnica simples embutida:

```text
http://localhost:8080/ui
```

OpenAI-compatible API:

```text
http://localhost:8080/v1
```

MCP HTTP JSON-RPC:

```text
http://localhost:8080/mcp
```

Serviços auxiliares:

```text
PostgreSQL: localhost:5432
Qdrant:     http://localhost:6333
Ollama:     http://localhost:11434
Open WebUI: http://localhost:3000
```

## Workspaces

Cada projeto fica em `workspaces/<workspace-id>/`.

Estrutura esperada:

```text
workspaces/projeto/
├── repositories/
├── documents/
├── graphs/
├── roslyn/
├── summaries/
├── embeddings/
├── cache/
└── logs/
```

Crie um workspace:

```bash
curl -X POST http://localhost:8080/workspaces/projeto
```

Liste workspaces:

```bash
curl http://localhost:8080/workspaces
```

## Upload de documentos

```bash
curl -F "file=@./docs/exemplo.md" \
  "http://localhost:8080/workspaces/projeto/documents?category=business-rules"
```

Liste documentos:

```bash
curl http://localhost:8080/workspaces/projeto/documents
```

## Chat

O endpoint de chat usa busca vetorial no Qdrant e geração local via Ollama. Se Qdrant/Ollama/modelos ainda não estiverem prontos, ele cai para busca textual e retorna referências candidatas.

```bash
curl -X POST http://localhost:8080/workspaces/projeto/chat \
  -H "Content-Type: application/json" \
  -d '{"message":"Quais regras existem para cancelamento?","maxResults":5}'
```

## Open WebUI

O Compose sobe o Open WebUI pronto para conversar com o AI Knowledge Server usando API compatível com OpenAI.

Abra:

```text
http://localhost:3000
```

O modelo exposto pelo servidor é:

```text
knowledge-server
```

Por padrão, chamadas feitas pelo Open WebUI usam o workspace `default`. Para usar um workspace específico via API compatível com OpenAI, use o modelo no formato:

```text
knowledge-server:<workspace-id>
```

O endpoint `GET /v1/models` lista dinamicamente os workspaces existentes como modelos. Depois de criar `workspaces/projeto`, o Open WebUI passa a ver:

```text
knowledge-server:projeto
```

ou envie o campo extra `workspaceId`:

```json
{
  "model": "knowledge-server",
  "workspaceId": "projeto",
  "messages": [
    {
      "role": "user",
      "content": "Quais regras existem para cancelamento?"
    }
  ]
}
```

Endpoints compatíveis:

```text
GET  /v1/models
POST /v1/chat/completions
```

## Admin técnico

A UI embutida em `http://localhost:8080/ui` permite:

- criar workspace
- registrar repositório já clonado no servidor
- fazer upload de documentos
- disparar reindexação
- listar documentos e repositórios
- testar ferramentas MCP

Registrar repositório não executa `git clone`. Ele cria/registra o caminho esperado dentro de `workspaces/<workspace>/repositories/` para que o clone ou `git pull` continue sendo feito por automação externa, como GitHub Actions.

Via API:

```bash
curl -X POST http://localhost:8080/workspaces/projeto/repositories \
  -H "Content-Type: application/json" \
  -d '{
    "name": "backend-api",
    "relativePath": "repositories/backend-api",
    "remoteUrl": "git@github.com:empresa/backend-api.git",
    "branch": "main"
  }'
```

## Watcher

O `workspace-watcher` observa alterações dentro de `workspaces/` e cria jobs em:

```text
workspaces/<workspace>/cache/jobs/
```

Ele ignora diretórios gerados ou ruidosos:

- `.git`
- `bin`
- `obj`
- `node_modules`
- `cache`
- `logs`
- `embeddings`

O fluxo previsto é:

```text
GitHub Action faz git pull no host
↓
Arquivos mudam em workspaces/
↓
Watcher detecta
↓
Job de indexação é criado
↓
Roslyn/C# deterministic indexer, Graphify/fallback graph, embeddings e Qdrant são atualizados
```

## Graphify Runner

O worker tenta executar o comando `graphify` durante a indexação. Se o comando não estiver disponível ou falhar, gera um grafo fallback em:

```text
workspaces/<workspace>/graphs/
```

Também há uma imagem auxiliar no profile `tools` para execução manual:

```bash
docker compose run --rm graphify-runner /app/workspaces/projeto
```

## Roslyn / C# Indexer

O worker executa um indexador determinístico de C# durante a indexação. Ele grava:

```text
workspaces/<workspace>/roslyn/symbols.json
workspaces/<workspace>/roslyn/references.json
workspaces/<workspace>/roslyn/callgraph.json
workspaces/<workspace>/roslyn/endpoints.json
workspaces/<workspace>/roslyn/related-tests.json
```

Também há uma imagem auxiliar com .NET SDK/MSBuild para execução manual ou evolução futura para Roslyn compiler-semantic:

```bash
docker compose run --rm roslyn-indexer --info
```

## GPU com Ollama

Em Linux, instale no host:

- NVIDIA driver
- NVIDIA Container Toolkit

Depois descomente a seção `deploy.resources.reservations.devices` do serviço `ollama` em `compose.yaml`.

Com GPU habilitada, o container usa a GPU do host. A perda por Docker tende a ser pequena; a diferença relevante é CPU versus GPU.

## Desenvolvimento Local

Build:

```bash
dotnet build AiKnowledgeServer.slnx
```

API:

```bash
dotnet run --project src/0.CompositionRoots/KnowledgeServer.Api
```

Worker:

```bash
dotnet run --project src/0.CompositionRoots/KnowledgeServer.Worker
```

## Arquitetura

Veja [ARCHITECTURE.md](ARCHITECTURE.md).
