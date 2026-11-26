# Guia de estudo do Encore

Este guia ajuda a entender o projeto por dentro e a ampliá-lo. Tem quatro partes:

1. **Mapa de temas**: cada tema de backend, onde ele está no código e a ideia principal.
2. **Roteiro de leitura**: em que ordem ler o código para entender tudo.
3. **Perguntas para revisar** com resposta curta, ligadas ao código.
4. **Exercícios** para fazer por conta própria, do mais fácil ao mais difícil. Aqui é onde se aprende de verdade.

---

## 1. Mapa de temas

| Tema | Onde está no Encore | Ideia principal |
|---|---|---|
| APIs REST robustas | [LibraryEndpoints.cs](../src/Encore.Api/Library/LibraryEndpoints.cs) | Minimal APIs, `TypedResults`, 202 + `Location` para trabalho assíncrono, problem details (RFC 9457) |
| gRPC | [recommendations.proto](../protos/recommendations.proto), [RecommendationsService.cs](../src/Encore.Recommender/Services/RecommendationsService.cs), [RecommendationEndpoints.cs](../src/Encore.Api/Recommendations/RecommendationEndpoints.cs) | Contrato primeiro, deadline em toda chamada, degradação para 503 quando o serviço cai |
| Background services | [OutboxPublisher.cs](../src/Encore.Api/Infrastructure/OutboxPublisher.cs), [AlbumImportConsumer.cs](../src/Encore.Worker/AlbumImportConsumer.cs) | `BackgroundService`, `PeriodicTimer`, shutdown gracioso com `CancellationToken` |
| Middlewares personalizados | [Middleware.cs](../src/Encore.Api/Infrastructure/Middleware.cs), [GlobalExceptionHandler.cs](../src/Encore.Api/Infrastructure/GlobalExceptionHandler.cs) | Correlation id, Server-Timing, tratamento global de erros; **a ordem do pipeline importa** |
| Integração com terceiros | [MusicBrainzClient.cs](../src/Encore.Core/MusicBrainz/MusicBrainzClient.cs), [RateGates.cs](../src/Encore.Core/MusicBrainz/RateGates.cs) | Typed `HttpClient`, `DelegatingHandler`, retry + timeout + circuit breaker, rate limit distribuído |
| Cache | [SearchEndpoints.cs](../src/Encore.Api/Search/SearchEndpoints.cs) | HybridCache (memória + Redis), proteção contra stampede, versionar a chave |
| Fluxos de autenticação | [AuthEndpoints.cs](../src/Encore.Api/Auth/AuthEndpoints.cs), [Program.cs](../src/Encore.Api/Program.cs) | JWT bearer, validação de issuer/audience/assinatura, `sub` como id; em produção o token vem de um IdP |
| C# / .NET moderno, DI, async/await | Todo o código | Lifetimes (singleton/scoped/transient), `IDbContextFactory`, `CancellationToken` passado até o banco, nada de `.Result` |
| EF Core **e** Dapper | [EncoreDbContext.cs](../src/Encore.Core/Data/EncoreDbContext.cs), [StatsEndpoints.cs](../src/Encore.Api/Stats/StatsEndpoints.cs) | EF para escrita com regras de domínio; Dapper para SQL de relatório com plano previsível |
| PostgreSQL e otimização de queries | [migração](../src/Encore.Core/Data/Migrations/), índices em `EncoreDbContext` | Índice parcial, índice composto para keyset, `LATERAL`, `FOR UPDATE SKIP LOCKED`, `EXPLAIN ANALYZE` (exercício 3) |
| GraphQL | [Schema.cs](../src/Encore.Api/GraphQL/Schema.cs), [DataLoaders.cs](../src/Encore.Api/GraphQL/DataLoaders.cs) | Problema N+1 e DataLoader, limite de profundidade, auth por campo |
| Concorrência | [LibraryService.cs](../src/Encore.Api/Library/LibraryService.cs), teste `Concurrent_adds_...` | Índice único + retry, `xmin` para concorrência otimista, consumidores concorrentes |
| Distributed tracing | [TraceContextHeaders.cs](../src/Encore.Core/Messaging/TraceContextHeaders.cs) | `traceparent` dentro dos headers AMQP: um trace atravessa API → fila → worker |
| Latency profiling | `Server-Timing`, histogramas OTel, exercício 6 | Medir antes de otimizar; p50 vs p95/p99 |
| Tratamento de erros, telemetria | `GlobalExceptionHandler`, [EncoreTelemetry.cs](../src/Encore.Core/Telemetry/EncoreTelemetry.cs) | Erros 4xx explicam, 5xx não vazam detalhes; métricas de negócio |
| Frontend React + TypeScript | [web/src](../web/src) | TanStack Query, polling só enquanto há álbum pendente, estados de loading/erro/vazio |
| Versionamento de API | `Program.cs`, `LibraryEndpoints.cs` | Mudança que quebra contrato → nova versão; v1 continua funcionando |
| Filas de mensagens | [RabbitMqConnection.cs](../src/Encore.Core/Messaging/RabbitMqConnection.cs) | Exchange topic, retry com TTL, DLQ, ack manual, prefetch |
| Testes unitários e de integração | [tests/](../tests) | Testcontainers com Postgres e RabbitMQ reais; só o terceiro é falso |
| Docker, CI/CD | [Dockerfile](../Dockerfile), [docker-compose.yml](../docker-compose.yml), [ci.yml](../.github/workflows/ci.yml) | Multi-stage, usuário não-root, cache de restore, CI que falha se a migração não bate com o modelo |
| Decompor um monolito | Worker e Recommender separados; exercício 8 | O Recommender ainda lê o banco da API; dar a ele dados próprios é o próximo passo |
| Kubernetes / ECS | Exercício 7 | Probes live/ready já existem, prontas para k8s |
| OpenTelemetry, Prometheus/Grafana | OTel pronto; exercício 5 | Mesmo código, outro exporter |
| Ferramentas de IA | README, seção "How AI was used" | Como usei IA, onde ela errou e como os testes pegaram |

### Aprender com os bugs

A tabela no fim de [architecture.md](architecture.md#bugs-found-while-building) conta cada problema real encontrado durante a construção: o que quebrou, como foi descoberto e o que mudou. Ler esses casos ensina mais do que a teoria: um bug de concorrência achado por um teste mostra o conceito funcionando de verdade.

---

## 2. Roteiro de leitura

Leia nesta ordem. Em cada passo, rode o que está indicado e observe.

1. **Domínio** ([Domain/](../src/Encore.Core/Domain/)) e testes unitários ([tests/Encore.UnitTests](../tests/Encore.UnitTests)). Entidades com setters privados e regras dentro delas.
   Rode `dotnet test tests/Encore.UnitTests`.
2. **Banco**: [EncoreDbContext.cs](../src/Encore.Core/Data/EncoreDbContext.cs) e a migração gerada. Compare o C# com o SQL:
   `dotnet ef migrations script --project src/Encore.Core --startup-project src/Encore.Api`
3. **Composição da API**: [Program.cs](../src/Encore.Api/Program.cs) de cima a baixo. Para cada `Add...`, pergunte: qual lifetime? Por quê?
4. **Fluxo "adicionar álbum"** seguindo o dado:
   `LibraryEndpoints` → `LibraryService.AddAsync` → `OutboxPublisher` → RabbitMQ → `AlbumImportConsumer` → `AlbumEnricher`.
   Com o compose rodando: adicione um álbum pelo app, abra o dashboard (http://localhost:18888), aba Traces, e ache o trace `POST /api/v{version}/library`. Veja os spans do worker dentro dele.
5. **Falhas**: derrube a internet ou mude `MusicBrainz__BaseUrl` do worker para uma URL inválida, adicione um álbum e assista na UI do RabbitMQ (http://localhost:15672) a mensagem passar pela fila de retry e acabar na DLQ.
6. **Leitura**: [StatsEndpoints.cs](../src/Encore.Api/Stats/StatsEndpoints.cs) (Dapper) e [Schema.cs](../src/Encore.Api/GraphQL/Schema.cs) (GraphQL). Em http://localhost:8080/graphql, rode:
   ```graphql
   { myLibrary { rating album { title artist { name } tracks { title lengthMs } } } }
   ```
   e conte no dashboard quantas queries SQL a requisição fez.
7. **gRPC**: o `.proto`, o serviço e o cliente. Faça `docker compose stop recommender` e abra a aba "For you": 503 tratado, o resto do app continua.
8. **Testes de integração**: [EncoreApiFactory.cs](../tests/Encore.IntegrationTests/EncoreApiFactory.cs) e [LibraryApiTests.cs](../tests/Encore.IntegrationTests/LibraryApiTests.cs). Entenda como o teste end-to-end prova o outbox e a idempotência.

---

## 3. Perguntas para revisar

**Qual a diferença entre `AddSingleton`, `AddScoped` e `AddTransient`? Onde isso dá problema?**
Singleton: uma instância para o processo. Scoped: uma por requisição HTTP. Transient: uma a cada resolução. O erro clássico é um singleton receber um scoped (ex.: `DbContext`): ele fica preso à primeira requisição e é usado por várias threads. Aqui, o `OutboxPublisher` (singleton, por ser hosted service) usa `IDbContextFactory` e cria um contexto por lote.

**Por que não usar `.Result` ou `.Wait()`?**
Bloqueia uma thread do pool enquanto espera I/O. Sob carga, o pool esgota e a latência explode (thread pool starvation). Eu cometi esse erro no handler da v2 e corrigi; está na tabela de bugs.

**Para que serve o `CancellationToken`?**
Se o cliente desiste da requisição, o servidor para de trabalhar: o token vai do endpoint até o Npgsql e o `HttpClient`. No worker, o token de parada do host faz as mensagens em andamento voltarem para a fila.

**EF Core ou Dapper?**
Os dois, cada um onde é melhor. EF para escrita com regras de domínio, change tracking, migrações e concorrência otimista. Dapper para relatórios: SQL explícito, plano previsível, várias consultas numa ida ao banco.

**Como você publica um evento sem perder dados?**
Outbox transacional (seção 2 do architecture.md). E o consumidor precisa ser idempotente, porque a entrega é at-least-once.

**O que é "exactly once"?**
Na prática não existe ponta a ponta numa rede. O que se faz é at-least-once + idempotência, que dá o mesmo efeito: processar duas vezes não muda o resultado.

**Como você lida com uma API de terceiros instável e com rate limit?**
Typed client; resilience handler (timeout por tentativa, retry com backoff e jitter que respeita `Retry-After`, circuit breaker); rate limit de saída; cache. E o detalhe que pegou aqui: se o limite é por IP, o controle tem que ser compartilhado entre processos (Redis).

**O que é o problema N+1 no GraphQL e como resolver?**
Cada item da lista dispara uma consulta para o campo filho. DataLoader junta as chaves de um "tick" de execução e faz uma consulta só com `WHERE id = ANY(...)`.

**Paginação: offset ou cursor?**
Offset é simples, mas `OFFSET 100000` lê e descarta 100 mil linhas, e inserções no meio deslocam as páginas. Keyset (cursor) usa o índice e é estável. O custo é não poder pular para a "página 37".

**Como versionar uma API?**
Mudança aditiva (campo novo, endpoint novo) não precisa de versão. Mudança que quebra contrato vira uma versão nova, convivendo com a antiga até os clientes migrarem. Aqui: v2 do `GET /library`.

**Como você investigaria uma requisição lenta em produção?**
Achar o trace pelo correlation id ou trace id, ver qual span domina (banco? terceiro? fila?), olhar p95/p99 nas métricas e não só a média. Se for SQL: `EXPLAIN (ANALYZE, BUFFERS)`. Se for CPU ou memória: `dotnet-counters` e `dotnet-trace`.

**Liveness vs readiness?**
Liveness: o processo está vivo? Se falhar, o orquestrador reinicia. Readiness: posso receber tráfego? Se falhar, sai do load balancer. Nunca colocar dependências externas no liveness: uma queda do banco reiniciaria todos os pods em loop.

**Como garantir que duas requisições simultâneas não criem duplicatas?**
Índice único no banco (a única garantia real) + tratar a violação (`23505`) relendo o vencedor. Check-then-insert na aplicação não basta: duas threads passam no check ao mesmo tempo.

**Por que o health do RabbitMQ retorna Degraded e não Unhealthy?**
Com o outbox, a API continua aceitando escritas sem o broker; os eventos esperam no Postgres. Tirar o pod do balanceador por isso derrubaria o app sem necessidade.

---

## 4. Exercícios

Faça na ordem. Cada um tem critério de pronto. Escreva o código à mão e use IA para revisar, não para escrever.

### Exercício 1: limpeza do outbox (fácil)
Linhas processadas se acumulam para sempre. Crie um `BackgroundService` que apaga linhas com `processed_at` mais antigas que 7 dias, em lotes de 1000.
**Pronto quando:** existe teste de integração e o delete não trava a tabela (lotes + `ExecuteDeleteAsync`).

### Exercício 2: GraphQL mutation (fácil)
Adicione `rateAlbum(albumId, rating, review)` como mutation, reutilizando o `LibraryService`.
**Pronto quando:** o frontend usa a mutation e há teste de integração.

### Exercício 3: otimização de query (médio)
Crie um script que gere 200 mil `library_entries` e 1 milhão de `tracks` (use `generate_series`). Rode `EXPLAIN (ANALYZE, BUFFERS)` na query de stats e na do recomendador.
**Pronto quando:** você consegue explicar cada nó do plano, acha o gargalo, cria um índice ou reescreve a query, e anota o antes e o depois em `docs/`.

### Exercício 4: retry no gRPC (médio)
Configure retry no cliente gRPC por *service config* (só para `Unavailable`) e um circuit breaker.
**Pronto quando:** com o recommender parado, a API para de tentar por 30 s depois de N falhas, e isso aparece nos traces.

### Exercício 5: Prometheus + Grafana (médio)
Adicione o exporter Prometheus (`/metrics`), um container Prometheus e um Grafana provisionado com um dashboard: taxa de requisições, p95 de latência, `encore.album_imports.failed`, tamanho da fila.
**Pronto quando:** `docker compose up` sobe tudo com o dashboard pronto, sem cliques manuais.

### Exercício 6: teste de carga e profiling (médio)
Escreva um script k6 que faz login, busca e adiciona álbuns. Rode com 50 usuários virtuais. Observe com `dotnet-counters monitor` (thread pool, GC, conexões).
**Pronto quando:** você sabe dizer qual recurso satura primeiro e por quê.

### Exercício 7: Kubernetes local (médio/difícil)
Com `kind` ou `k3d`: Deployments para api, worker e recommender, `livenessProbe`/`readinessProbe` apontando para os health checks, ConfigMap e Secret para configuração, HPA no worker.
**Pronto quando:** `kubectl rollout restart deploy/worker` acontece sem perder mensagens (graças ao ack manual e ao shutdown gracioso).

### Exercício 8: decompor de verdade o Recommender (difícil)
Hoje ele lê o banco da API. Dê a ele um banco próprio (ou schema próprio) com uma tabela de leitura, alimentada por eventos `AlbumEnriched` e `AlbumRated` publicados via outbox.
**Pronto quando:** o recommender não tem connection string para as tabelas da API, e um teste prova que avaliar um álbum muda a recomendação depois de alguns segundos (consistência eventual).

### Exercício 9: autenticação real (difícil)
Troque o login de demonstração por um provedor OIDC (Keycloak no compose, ou Entra ID / Auth0). A API só valida tokens; o frontend faz o fluxo Authorization Code + PKCE.
**Pronto quando:** nenhuma chave de assinatura fica na API.

### Exercício 10: IA no produto (difícil)
Endpoint que gera um texto curto de "liner notes" sobre um álbum a partir das tags e da tracklist, usando um LLM. Coloque atrás de cache, rate limit e timeout, e trate a falha do LLM como trata a do recommender.
**Pronto quando:** cair o LLM não quebra nada, e o custo por chamada aparece numa métrica.

---

## Comandos úteis

```bash
# .NET instalado em ~/.dotnet: adicione ao ~/.zshrc
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"

dotnet build                       # warnings viram erro (Directory.Build.props)
dotnet test                        # precisa do Docker rodando
dotnet ef migrations add <Nome> --project src/Encore.Core --startup-project src/Encore.Api --output-dir Data/Migrations

docker compose up --build -d       # sobe tudo
docker compose logs -f worker      # acompanha o worker
docker compose down                # para tudo (mantém o volume do Postgres)
docker compose down -v             # para e apaga os dados
```
