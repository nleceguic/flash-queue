# syntax=docker/dockerfile:1
#
# Dockerfile genérico para cualquier proyecto ejecutable del monorepo — no hay un Dockerfile por
# proyecto, docker-compose.yml decide qué publicar y qué ensamblado arrancar vía build args:
#
#   docker build --build-arg PROJECT_PATH=src/FlashQueue.Api/FlashQueue.Api.csproj \
#                --build-arg ASSEMBLY_NAME=FlashQueue.Api .
#
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG PROJECT_PATH
WORKDIR /src
COPY . .
RUN dotnet publish "${PROJECT_PATH}" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime

# curl: lo usan los healthchecks HTTP de docker-compose.yml. Todos los servicios de este monorepo
# exponen /health (o /health/dependencies) aunque su trabajo real no sea servir peticiones — ver
# README-DOCKER.md — así que vale la pena instalarlo aquí una vez, en la imagen base compartida,
# en vez de en cinco Dockerfiles casi idénticos.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

ARG ASSEMBLY_NAME
ENV ASSEMBLY_NAME=${ASSEMBLY_NAME}

ENTRYPOINT ["sh", "-c", "exec dotnet \"${ASSEMBLY_NAME}.dll\""]
