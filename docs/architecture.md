# Architecture and decisions

Each decision lists the problem, the choice, and what I would revisit at larger scale. File links point at the code that implements it.

## 1. Service boundaries

| Service | Owns | Talks to |
|---|---|---|
| `Encore.Api` | HTTP edge: REST, GraphQL, auth, validation, outbox publishing | Postgres, Redis, RabbitMQ (publish), Recommender (gRPC), MusicBrainz (search, lookup) |
| `Encore.Worker` | Slow, failure-prone enrichment from MusicBrainz | RabbitMQ (consume), Postgres, MusicBrainz, Redis (rate gate) |
| `Encore.Recommender` | Recommendation query | Postgres (read-only) |

**Why split the worker out:** enrichment takes seconds, depends on a third party with a strict rate limit, and fails sometimes. Inside a request it would make `POST /library` slow and fragile. As a queue consumer it scales on its own (`replicas: 2` in compose) and retries without anyone waiting.

**Why the recommender is a separate gRPC service:** to practise an internal, contract-first boundary ([protos/recommendations.proto](../protos/recommendations.proto)). It is honest about one shortcut: **it reads the API's database**. At larger scale it should own a read model fed by events (`AlbumEnriched`, `AlbumRated`), so the two services can deploy and change their schemas independently. That is the first step of a strangler-style decomposition.

## 2. Writes and events: transactional outbox

**Problem:** "save album, then publish event" can lose the event (crash between the two) or publish an event for a rolled-back write.

**Choice:** the event is a row in `outbox_messages`, saved in the same transaction as the album ([LibraryService.cs](../src/Encore.Api/Library/LibraryService.cs)). [OutboxPublisher.cs](../src/Encore.Api/Infrastructure/OutboxPublisher.cs) polls every second:

```sql
SELECT * FROM outbox_messages
WHERE processed_at IS NULL
ORDER BY occurred_at
LIMIT 20
FOR UPDATE SKIP LOCKED
```

- `SKIP LOCKED` lets several API replicas publish in parallel without sending the same row twice.
- A partial index (`WHERE processed_at IS NULL`) keeps the scan small as the table grows.
- Publisher confirms: a row is marked processed only after the broker acknowledged it.
- The row stores the request's W3C `traceparent`, so the trace continues across the delay.

**Trade-offs:** polling adds up to ~1 s of latency, and processed rows pile up (a cleanup job is a listed exercise). At scale: CDC with Debezium reading the Postgres WAL instead of polling.

## 3. Consuming: at-least-once, idempotent, retry, DLQ

[AlbumImportConsumer.cs](../src/Encore.Worker/AlbumImportConsumer.cs) and [AlbumEnricher.cs](../src/Encore.Worker/AlbumEnricher.cs).

- **Manual acks, prefetch = concurrency (4).** The broker never pushes more than the worker can handle.
- **Idempotency:** the message id goes into `processed_messages` in the same transaction as the album update. A redelivered message is skipped. If two consumers race on the same message, the primary key makes one of them fail, and that failure is treated as "already processed".
- **Network first, writes last:** the MusicBrainz calls happen before the DB transaction, so no transaction stays open while waiting on the network.
- **Retries:** on a transient error the message is copied to `worker.album-import.retry` (TTL 5 s, dead-letters back to the main queue) with `x-attempt + 1`, then the original is acked. Publish first, ack second: a crash in between duplicates, never loses.
- **DLQ:** after 3 attempts, or immediately for an unparseable payload. The album is marked `Failed` so the UI stops showing "Importing…".
- **Graceful shutdown:** on SIGTERM unacked messages return to the queue; `HostOptions.ShutdownTimeout` gives in-flight work 20 s.

## 4. Data access: EF Core and Dapper

- **EF Core** for the domain writes: change tracking, migrations, optimistic concurrency. Entities have private setters and behaviour (`Album.MarkEnriched`, `LibraryEntry.Rate`); rules live there and are unit tested.
- **Dapper** for reporting SQL ([StatsEndpoints.cs](../src/Encore.Api/Stats/StatsEndpoints.cs), [RecommendationsService.cs](../src/Encore.Recommender/Services/RecommendationsService.cs)): aggregates, `LATERAL` joins, CTEs, 3 result sets in one round trip. Hand-written SQL makes the plan predictable and easy to check with `EXPLAIN ANALYZE`.
- **One `NpgsqlDataSource`** (connection pool) shared by both.
- **Pooled `DbContextFactory`:** background services and GraphQL resolvers create short-lived contexts, because a `DbContext` is not thread-safe and GraphQL resolves fields in parallel.

**Indexes and why** ([EncoreDbContext.cs](../src/Encore.Core/Data/EncoreDbContext.cs)):

| Index | Serves |
|---|---|
| `albums(mb_id)` unique, `artists(mb_id)` unique | Dedupe the catalog; makes concurrent inserts safe |
| `library_entries(user_id, added_at DESC, album_id DESC)` | v2 keyset pagination |
| `album_tags(tag)` | "albums by tag" and the recommender join |
| `outbox_messages(occurred_at) WHERE processed_at IS NULL` | Outbox polling |
| `tracks(album_id, position)` unique | Track order and integrity |

## 5. Concurrency

- **Concurrent "add the same album"** by different users: both try to insert the artist/album, the unique index rejects one, [LibraryService.AddAsync](../src/Encore.Api/Library/LibraryService.cs) clears its change tracker and retries, finding the winner's row. Covered by `Concurrent_adds_of_the_same_album_create_one_catalog_row`.
- **Lost updates on albums:** `Album.Version` maps to Postgres' `xmin`; EF adds `WHERE xmin = @old` to updates and throws `DbUpdateConcurrencyException` (→ `409`) on a conflict.
- **EF retry-on-failure + manual transactions** must run inside `CreateExecutionStrategy().ExecuteAsync(...)` (see the outbox publisher).

## 6. HTTP API

- **Minimal APIs**, grouped per feature, thin handlers, one application service.
- **Validation:** .NET 10 built-in DataAnnotations validation for minimal APIs, plus domain rules as the last line of defence.
- **Errors:** one [`IExceptionHandler`](../src/Encore.Api/Infrastructure/GlobalExceptionHandler.cs) maps exceptions to RFC 9457 problem details with a `traceId`. 5xx never leak internals.
- **Versioning** (`Asp.Versioning`, URL segment): v2 changed the response shape of `GET /library` (array → `{ items, nextCursor }`), so it is a new version and v1 stays untouched. One OpenAPI document per version.
- **Keyset pagination:** `WHERE (added_at, album_id) < (@a, @b)` with a matching index. Unlike `OFFSET`, cost does not grow with the page number and inserts do not shift pages.
- **202 Accepted + Location** for the asynchronous import.
- **Middleware order** ([Program.cs](../src/Encore.Api/Program.cs)): exception handler → correlation id → server timing → authentication → authorization → rate limiter → endpoints.

## 7. Caching and rate limiting

- **HybridCache** (in-memory L1 + Redis L2) for MusicBrainz search and lookups. It also gives **stampede protection**: concurrent misses for one key run the factory once.
- **Cache keys are versioned** (`mb:search:v2:…`): when the search logic changed, old results in Redis would otherwise be served for an hour.
- **Inbound rate limit:** 20 searches per minute per user (ASP.NET Core rate limiter), to protect our upstream quota.
- **Outbound rate limit:** MusicBrainz allows ~1 request/s **per IP**. A per-process token bucket is wrong once the API and two workers share an IP, so the gate lives in Redis ([RateGates.cs](../src/Encore.Core/MusicBrainz/RateGates.cs)): `SET ratelimit:musicbrainz NX PX 1100`; losers wait for the key's TTL. It sits inside the resilience handler (retries, timeouts, circuit breaker), so retries are throttled too.

## 8. GraphQL

HotChocolate 15 ([Schema.cs](../src/Encore.Api/GraphQL/Schema.cs)).

- **Explicit object types** over the domain entities: hide `userId` and the `xmin` version, resolve relations through DataLoaders.
- **DataLoaders** ([DataLoaders.cs](../src/Encore.Api/GraphQL/DataLoaders.cs)) batch lookups: `myLibrary { album { artist tracks } }` for 50 albums runs 4 queries, not 151.
- **Guards:** max depth 8, built-in cost analysis, auth per field with `[Authorize]`.
- REST for commands, GraphQL for flexible reads: the library grid and the album detail ask for different fields from the same endpoint.

## 9. gRPC

- Contract in `protos/`, compiled into both client and server (`Grpc.Tools`).
- Package-level versioning (`encore.recommendations.v1`): a breaking change becomes `v2` side by side.
- **Deadline of 3 s** on every call. `Unavailable` or `DeadlineExceeded` → the API answers `503` for that endpoint only; the rest of the app keeps working.
- HTTP/2 without TLS inside the Docker network (`Kestrel:EndpointDefaults:Protocols=Http2`). In production: mTLS or a service mesh.

## 10. Observability

[EncoreTelemetry.cs](../src/Encore.Core/Telemetry/EncoreTelemetry.cs).

- **OpenTelemetry** traces, metrics and logs, exported over OTLP (to the Aspire dashboard locally; any OTLP backend in production: Datadog, Grafana Tempo/Prometheus, Honeycomb).
- **Auto-instrumentation:** ASP.NET Core, HttpClient, Npgsql, HotChocolate, runtime metrics.
- **Manual spans across the queue:** `traceparent` is injected into AMQP headers by the outbox publisher and extracted by the worker ([TraceContextHeaders.cs](../src/Encore.Core/Messaging/TraceContextHeaders.cs)). One trace shows the HTTP request, the publish, the worker, its MusicBrainz calls and its SQL.
- **Business metrics:** `encore.albums.enriched`, `encore.album_imports.failed{attempt}`, `encore.outbox.published`, `encore.album_import.duration`.
- **Correlation id** middleware: accepted from the caller (validated, since it lands in logs), echoed back, added to the log scope and span.
- **Server-Timing** header: request latency visible in the browser's Network tab without opening any tool.
- **Health:** `/health/live` (process up, no dependencies) vs `/health/ready` (Postgres + RabbitMQ). RabbitMQ down reports `Degraded`, not `Unhealthy`: the outbox keeps accepting writes, so the pod should not be pulled from the load balancer.

## 11. Testing

- **Unit:** domain rules, MusicBrainz mapping, cursor encoding. Fast, no I/O.
- **Integration:** `WebApplicationFactory` boots the real API in memory against **real Postgres and RabbitMQ containers** (Testcontainers). Only MusicBrainz is faked. Tests cover the outbox → broker → worker path, idempotency, concurrent inserts, validation, versioning, GraphQL, Dapper SQL and degradation when the recommender is down.
- **Frontend:** Vitest + Testing Library on components.
- **CI** ([.github/workflows/ci.yml](../.github/workflows/ci.yml)): build with warnings as errors, check that migrations match the model, run all tests with coverage, build every Docker image.

## Bugs found while building

Each one is a real story: what broke, how it surfaced, what fixed it.

| # | Symptom | Cause | Fix |
|---|---|---|---|
| 1 | `POST /library` → 500, "LINQ expression could not be translated" | Filtering after projecting to a record built through its constructor; EF cannot see through constructor parameters | Filter and sort on the entity, project to the DTO last |
| 2 | Worker → `DbUpdateConcurrencyException` on the first enrichment | Track ids are created in the domain; EF assumed a new track with a key already set was an existing row and issued an `UPDATE` hitting 0 rows | `ValueGeneratedNever()` on domain-generated keys |
| 3 | Recommender would always fail at runtime (caught by a new SQL test) | Dapper matches constructors by exact type; Npgsql reports `text[]` as `System.Array`, never `string[]` | Map into a class with settable properties |
| 4 | Malformed JSON body → 500 | The global handler mapped every unknown exception to 500, including `BadHttpRequestException` | Use the exception's own status code (400) |
| 5 | Searches → 502 under normal use | MusicBrainz limits per IP; the per-process limiter allowed 3 req/s across API + 2 workers | Rate gate in Redis, shared by all processes |
| 6 | Search still returned bad results after fixing the query | Old results cached in Redis for 1 h | Versioned cache keys |
| 7 | Web → 502 after redeploying only the API | nginx resolves `proxy_pass` hosts once at startup; the new container had a new IP | `resolver 127.0.0.11` + variable upstream |
| 8 | GraphQL schema failed to build | `uint` (the `xmin` token) has no GraphQL type; attribute-based ignore did not apply | Explicit `ObjectType<T>` that ignores it |
| 9 | Code review catch: v2 list handler used `.GetAwaiter().GetResult()` | Sync-over-async blocks a thread-pool thread per request; under load this starves the pool | Async handler |
