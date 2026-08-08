# syntax=docker/dockerfile:1
#
# Genérico para cualquier proyecto ejecutable del monorepo; docker-compose.yml decide qué publicar
# y qué ensamblado arrancar vía build args (PROJECT_PATH, ASSEMBLY_NAME).
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG PROJECT_PATH
WORKDIR /src
COPY . .
RUN dotnet publish "${PROJECT_PATH}" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime

# curl para los healthchecks HTTP de docker-compose.yml, instalado una vez en la imagen compartida.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

ARG ASSEMBLY_NAME
ENV ASSEMBLY_NAME=${ASSEMBLY_NAME}

ENTRYPOINT ["sh", "-c", "exec dotnet \"${ASSEMBLY_NAME}.dll\""]
