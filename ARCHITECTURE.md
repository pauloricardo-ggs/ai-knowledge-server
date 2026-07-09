# AI Knowledge Server - Arquitetura

## Objetivo

Criar uma plataforma local de conhecimento para desenvolvimento de software e áreas de negócio. O servidor deverá permitir que IAs e pessoas consultem projetos grandes consumindo menos tokens, com mais rastreabilidade e com referências para código, documentos e decisões.

A plataforma consolida conhecimento proveniente de:

- Código-fonte
- Grafos de relacionamento
- Análise .NET/C# com Roslyn
- Documentação técnica
- Regras de negócio
- Contratos
- PDFs e documentos operacionais
- APIs
- Bancos de dados
- Resumos técnicos gerados por LLM

Esse conhecimento é exposto por duas superfícies:

- Interfaces humanas: chat privado, portal de desenvolvedor e administração.
- Interfaces de máquina: MCP Gateway para Codex, Claude, Cursor, Copilot, Gemini e outros agentes.

---

## Princípios

- Docker First
- Local First
- Open Source sempre que possível
- Componentes desacoplados
- Arquitetura plugável por providers
- Escalável para múltiplos projetos
- Independente da IA utilizada
- Independente do fornecedor de grafo
- Independente do fornecedor de embeddings
- Otimizada para projetos .NET/C#, sem ficar limitada a eles
- Sem acoplamento a uma empresa, cliente ou domínio específico

---

## Visão Geral

```text
Usuários humanos
├── Open WebUI
├── Business Chat UI técnico
├── Developer Portal
└── Admin Console
        │
        ▼
Knowledge API

Agentes de IA
└── MCP Gateway
        │
        ▼
Knowledge API

Knowledge API
├── Workspace Provider
├── RAG Provider
├── Summary Provider
├── Graph Provider
├── .NET Code Intelligence Provider
└── Document Provider

Background
├── Workspace Watcher
├── Indexing Jobs
├── Roslyn Indexer
├── Graphify Runner
├── Document Indexer
├── Summary Generator
└── Embedding Generator

Storage / Runtime
├── PostgreSQL
├── Qdrant
├── Ollama
└── Workspaces em bind mount
```

---

## Decisão Arquitetural Principal

O AI Knowledge Server não deve ser centrado em uma ferramenta específica.

Graphify, Roslyn, Qdrant, Ollama e PostgreSQL são providers padrão do MVP, mas a arquitetura deve permitir substituir ou adicionar providers sem reescrever o sistema.

Roslyn não é um provider de embeddings. Ele é uma fonte estruturada e determinística de inteligência de código .NET/C#. Suas saídas podem alimentar grafo, resumos e embeddings, mas ele deve continuar separado do RAG vetorial.

---

## Docker First

Todos os componentes da plataforma devem rodar em containers sempre que possível.

O host precisa fornecer:

- Docker ou runtime compatível
- Driver NVIDIA e NVIDIA Container Toolkit, apenas quando for usar GPU
- Diretório `workspaces/` em bind mount

Os serviços persistentes rodam continuamente:

- `api`
- `workspace-watcher`
- `postgres`
- `qdrant`
- `ollama`

Os runners e indexers devem ser efêmeros:

- `graphify-runner`
- `roslyn-indexer`
- `document-indexer`
- `summary-generator`
- `embedding-generator`

---

## Providers Padrão do MVP

| Área | Provider padrão | Função |
|---|---|---|
| Grafo amplo | Graphify | Mapeamento geral de código, docs e relações |
| Análise .NET | Roslyn | Símbolos, referências, implementações, call graph e impacto |
| Vetores/RAG | Qdrant | Busca vetorial de documentos, regras e resumos |
| LLM local | Ollama | Embeddings e geração de respostas/resumos |
| Metadados | PostgreSQL | Workspaces, configurações, logs, histórico e jobs |
| Arquivos | Bind mount `workspaces/` | Repositórios, documentos e artefatos gerados |
| Interface para agentes | MCP Gateway | Expor ferramentas para assistentes de IA |
| Interface humana | Web UI | Chat privado, upload de documentos e portal técnico |

---

## Graphify vs Roslyn vs Embeddings

### Graphify

Responsável por visão ampla e multi-fonte.

Bom para:

- Mapa geral do projeto
- Relações entre código, documentos e PDFs
- Visão multi-linguagem
- Grafo consultável
- Perguntas amplas de arquitetura
- Caminhos entre conceitos, serviços e documentos

### Roslyn

Responsável por análise profunda de .NET/C#.

Bom para:

- Encontrar referências com precisão
- Entender interfaces e implementações
- Montar call graph confiável para C#
- Identificar uso real de DTOs, entidades e serviços
- Analisar controllers, endpoints, handlers e dependency injection
- Avaliar impacto de alteração em método, classe ou contrato
- Encontrar testes relacionados

### Embeddings / RAG

Responsável por recuperação semântica aproximada.

Bom para:

- Encontrar documentos semanticamente próximos
- Recuperar regras de negócio e trechos relevantes
- Buscar por intenção quando os termos exatos são diferentes
- Alimentar respostas naturais com referências
- Fazer ponte entre linguagem de negócio e artefatos técnicos

### Uso conjunto

Graphify responde melhor: "como as coisas se relacionam no ecossistema".

Roslyn responde melhor: "como o código .NET realmente funciona".

RAG responde melhor: "quais trechos/documentos parecem relevantes para essa pergunta".

O Knowledge API e o MCP Gateway combinam os três quando a pergunta envolve código, regras e impacto.

---

## Interfaces Humanas

### Business Chat UI

Interface para áreas de negócio consultarem a IA local do servidor. No MVP, a interface principal é o Open WebUI apontando para a API OpenAI-compatible do AI Knowledge Server.

Responsabilidades:

- Chat privado com IA local
- Perguntas sobre regras de negócio
- Respostas naturais com referências
- Upload de PDFs, DOCX, Markdown e documentos operacionais
- Seleção de workspace/projeto
- Histórico de conversas
- Feedback em respostas
- Listagem de documentos indexados

Fluxo de upload:

```text
Upload
↓
Salva em workspaces/<workspace>/documents/
↓
Registra metadados
↓
Enfileira job de indexação
↓
Document Indexer processa
↓
Gera chunks, embeddings e resumos
↓
Atualiza Qdrant/PostgreSQL
↓
Disponibiliza para Chat e MCP
```

### Developer Portal

Interface para desenvolvedores consultarem conhecimento técnico.

Responsabilidades:

- Busca técnica no projeto
- Perguntas sobre código
- Referências e impacto via Roslyn
- Endpoints, controllers, handlers e consumers
- Testes relacionados
- Logs de indexação
- Estado do último commit indexado
- Botão para reindexar workspace/repositório
- Acesso à visualização Graphify

### Admin Console

Interface operacional.

Responsabilidades:

- Gerenciar workspaces
- Ver status dos providers
- Ver jobs e falhas
- Configurar modelos locais
- Controlar permissões futuras
- Ver uso de storage e índices

---

## Interfaces de Máquina

### MCP Gateway

Interface para agentes de IA.

Transportes previstos:

- HTTP/SSE para servidor central/remoto
- stdio opcional para desenvolvimento local

Ferramentas previstas:

- `find_related_code`
- `find_references`
- `find_impacted_services`
- `search_business_rules`
- `get_service_summary`
- `explain_flow`
- `compare_business_rule_with_code`
- `find_divergences`

O MCP Gateway não deve acessar storage diretamente quando houver regra de aplicação. Ele deve usar a Knowledge API ou os mesmos serviços de aplicação usados pela UI.

---

## Core Services

### Knowledge API

API central usada pelas interfaces humanas e pelo MCP Gateway.

Responsável por:

- Gerenciar workspaces
- Receber upload de documentos
- Consultar RAG, resumos, grafo e Roslyn
- Enfileirar reindexações
- Retornar respostas com referências
- Padronizar acesso aos providers
- Expor `/v1/models` e `/v1/chat/completions` para UIs compatíveis com OpenAI, como Open WebUI

### Workspace Provider

Gerencia múltiplos projetos.

Cada workspace contém:

- Repositórios
- Documentos
- Grafos
- Análises Roslyn
- Resumos
- Embeddings
- Cache
- Logs
- Configuração

### Workspace Watcher

Substitui o scheduler como mecanismo principal de atualização.

Responsável por:

- Observar `workspaces/`
- Detectar alterações feitas por `git pull`, upload ou edição direta
- Ignorar diretórios como `.git`, `bin`, `obj`, `node_modules`, `cache`, `logs`
- Aplicar debounce para agrupar várias mudanças
- Criar jobs de indexação

O watcher não executa processamento pesado diretamente.

### Indexing Jobs

Representam trabalho pendente de indexação.

No MVP inicial podem ser arquivos em `workspaces/<workspace>/cache/jobs/`. Em produção, PostgreSQL deve se tornar a fonte de verdade.

### Repository Indexer

Responsável por:

- Inventariar repositórios em `repositories/`
- Ler branch e commit atual
- Detectar alterações desde a última indexação
- Disparar Roslyn Indexer
- Disparar Graphify Runner
- Disparar geração de resumos
- Disparar geração de embeddings

O clone inicial e o `git pull` são responsabilidade do usuário, GitHub Action ou automação externa.

### Graphify Provider

Responsável por gerar grafo amplo do projeto.

Entradas:

- Código
- Markdown
- PDFs
- Documentos
- Configurações

Saídas:

- `graph.json`
- `GRAPH_REPORT.md`
- HTML interativo
- Relações entre código, documentação e conceitos

### .NET Code Intelligence Provider

Provider baseado em Roslyn.

Entradas:

- `.sln`
- `.slnx`
- `.csproj`
- `.cs`
- `.razor`
- `.cshtml`

Saídas previstas:

- Símbolos
- Referências
- Implementações
- Call graph
- Mapa de dependency injection
- Controllers e endpoints
- Handlers e consumers
- Impacto de alteração
- Testes relacionados

### Document Indexer

Processa:

- PDF
- DOCX
- Markdown
- Contratos
- Documentação funcional
- Especificações
- ADRs
- RFCs

### RAG Provider

Provider padrão:

- Qdrant

Responsabilidades:

- Armazenar embeddings
- Buscar documentos semanticamente relevantes
- Retornar trechos com metadados
- Permitir rastreabilidade por documento, página, versão e origem

### Embedding Provider

Provider padrão:

- Ollama Embeddings

Deve ser plugável para permitir outros modelos no futuro.

### Summary Provider

Provider padrão:

- Ollama para geração
- PostgreSQL para armazenamento

Exemplos:

- Resumo de serviço
- Resumo de classe
- Resumo de domínio
- Resumo de endpoint
- Resumo de fluxo
- Dependências
- Regras identificadas
- Pontos de atenção

---

## Organização dos Dados

```text
workspaces/
├── projeto-a/
│   ├── workspace.json
│   ├── repositories/
│   ├── documents/
│   ├── graphs/
│   ├── roslyn/
│   ├── summaries/
│   ├── embeddings/
│   ├── cache/
│   └── logs/
│
└── projeto-b/
    ├── workspace.json
    ├── repositories/
    ├── documents/
    ├── graphs/
    ├── roslyn/
    ├── summaries/
    ├── embeddings/
    ├── cache/
    └── logs/
```

Exemplo de workspace:

```text
workspaces/projeto/
├── workspace.json
├── repositories/
│   ├── repo1/
│   ├── repo2/
│   └── repo3/
├── documents/
│   ├── contracts/
│   ├── business-rules/
│   ├── manuals/
│   ├── policies/
│   └── raw/
├── graphs/
│   ├── graph.json
│   ├── GRAPH_REPORT.md
│   └── index.html
├── roslyn/
│   ├── symbols.json
│   ├── references.json
│   ├── callgraph.json
│   └── endpoints.json
├── summaries/
├── embeddings/
├── cache/
│   └── jobs/
└── logs/
```

---

## Fluxo de Atualização

```text
GitHub Action
↓
SSH no servidor
↓
git pull em workspaces/<workspace>/repositories/<repo>
↓
Workspace Watcher detecta alteração
↓
Cria indexing job
↓
Repository Indexer identifica o que mudou
↓
Executa Roslyn Indexer quando houver .NET/C#
↓
Executa Graphify Runner quando grafo amplo precisar atualizar
↓
Executa Document Indexer quando docs mudarem
↓
Gera resumos técnicos
↓
Atualiza embeddings
↓
Atualiza Qdrant/PostgreSQL/artefatos do workspace
↓
Disponibiliza para Web UI e MCP Gateway
```

---

## Estado do MVP neste repositório

Implementado:

- Knowledge API em .NET
- Workspace Watcher em .NET
- Store inicial em filesystem
- Upload de documentos
- Listagem de workspaces, documentos e jobs
- UI web privada em `/ui`
- Open WebUI no Compose em `:3000`
- API OpenAI-compatible em `/v1`
- MCP Gateway HTTP JSON-RPC em `/mcp`
- Chat endpoint com RAG via Qdrant e geração local via Ollama
- Pull automático de modelos configurados do Ollama
- Geração de embeddings e upsert no Qdrant
- Indexador determinístico de C# com símbolos, referências, call graph, endpoints e testes relacionados
- Graphify via processo quando disponível, com grafo fallback local
- Docker Compose com API, watcher, PostgreSQL, Qdrant e Ollama
- Imagens auxiliares para Graphify Runner e Roslyn/MSBuild

Ainda planejado:

- Persistência principal em PostgreSQL
- Roslyn compiler-semantic com MSBuildWorkspace
- Autenticação e autorização
- Administração avançada de usuários/workspaces
- Workers distribuídos e fila transacional
