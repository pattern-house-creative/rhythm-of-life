# The engine image: the MCP server, its scheduler, the one-shot migrate step and the operator commands.
# Build context is the repository root (it needs demo/engine for the demo seed):
#   docker build -f engine/Dockerfile -t rhythm-engine:dev .
# Generalized from the private platform's API image: pinned SDK, LTS runtime, locked restore, non-root user.
# The SDK is an exact release, bumped by hand (deploy/image-pins.txt) within a feature band Microsoft still services;
# engine/global.json accepts it. The SDK and runtime each have a pinned manifest digest.
FROM mcr.microsoft.com/dotnet/sdk:10.0.403@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e AS build
WORKDIR /src
COPY engine/global.json engine/
COPY engine/src/Core/Knowledge.csproj engine/src/Core/packages.lock.json engine/src/Core/
RUN dotnet restore engine/src/Core --locked-mode
COPY engine/src engine/src
COPY engine/migrations engine/migrations
COPY demo/engine demo/engine
COPY LICENSE NOTICE ./
COPY docs/licenses docs/licenses
# The rhythm CLI's adapter descriptors and the examples `rhythm init` starts from (published to adapters/, templates/).
COPY contracts/adapters contracts/adapters
# The settings schema, built into the engine: it refuses a rhythm.yaml key it does not name.
COPY contracts/rhythm.schema.json contracts/rhythm.schema.json
COPY deploy/compose/rhythm.example.yaml deploy/compose/.env.example deploy/compose/
RUN dotnet publish engine/src/Core -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e
# Kerberos library Npgsql loads at start; time zone data so rhythm.yaml's timezone resolves.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 tzdata && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
COPY engine/rhythm-engine /usr/local/bin/rhythm-engine
RUN chmod 0755 /usr/local/bin/rhythm-engine && mkdir -p /var/lib/rhythm && chown 1654:1654 /var/lib/rhythm
USER 1654:1654
# The base image already listens on 8080 (ASPNETCORE_HTTP_PORTS).
ENV RHYTHM_CONFIG=/etc/rhythm/rhythm.yaml RHYTHM_DATA=/var/lib/rhythm DOTNET_gcServer=0
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=5 \
  CMD ["rhythm-engine", "healthcheck"]
ENTRYPOINT ["rhythm-engine"]
CMD ["serve"]
