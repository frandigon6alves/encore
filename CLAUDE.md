# Encore

Learning project: .NET 10 backend (REST, GraphQL, gRPC, RabbitMQ worker, EF Core + Dapper on Postgres, OpenTelemetry) + React/TS frontend. See README.md and docs/architecture.md.

- `dotnet` lives in `~/.dotnet`; prepend it to PATH.
- `dotnet build` treats warnings as errors. `dotnet test` needs Docker (Testcontainers).
- Package versions live only in `Directory.Packages.props`. Stay on .NET 10 (LTS) packages: 11.x ASP.NET/EF packages do not target net10.
- Domain-generated keys need `ValueGeneratedNever()` (see docs/architecture.md, bug 2).
- Changing search/caching logic: bump the cache key version.
- When asked to help with the exercises in docs/guia-de-estudo.md, guide and review; the owner writes the code to learn.
