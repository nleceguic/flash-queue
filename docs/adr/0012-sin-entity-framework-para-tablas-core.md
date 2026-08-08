# ADR 0012: Por qué no se usó Entity Framework para las tablas core (`events` / `reservations`)

- **Fecha**: 2026-08-07
- **Estado**: Aceptada

## Contexto

CLAUDE.md (regla explícita, sección 5) prohíbe EF para las tablas de
stock/reserva. Este ADR documenta el porqué, no solo la regla.

## Decisión

Dapper + Npgsql directo (`ReservationRepository`). El mecanismo que
garantiza cero overselling es `SELECT ... FOR UPDATE SKIP LOCKED` dentro
de una transacción explícita, con control manual de cuándo se abre la
conexión, cuándo empieza la transacción y qué SQL exacto se ejecuta en qué
orden — control que EF Core no expone de forma directa, porque su
tracking de cambios y generación de SQL están pensados justo para ocultar
ese nivel de detalle.

## Alternativas descartadas

- **EF Core con `.FromSqlRaw("SELECT ... FOR UPDATE")` para el lock +
  `SaveChanges()` para el resto**: mezcla dos modelos mentales (tracking
  de entidades + SQL crudo para la parte crítica) sin ganar nada sobre SQL
  crudo para todo el método — la mitad de la complejidad de EF sin
  ninguna de sus ventajas.
- **EF Core con concurrencia optimista** (`[Timestamp]`/`RowVersion` +
  `DbUpdateConcurrencyException`): descartado por el mismo motivo que en
  el ADR 0010 (alta contención sobre una fila favorece el locking
  pesimista); además EF traduce el conflicto en una excepción .NET que hay
  que capturar y reintentar a mano igualmente — no evita escribir el
  bucle de reintento, solo cambia su forma.
- **Dapper también para las tablas secundarias** (catálogo de eventos,
  usuarios): CLAUDE.md permite EF ahí explícitamente porque no compromete
  el control de concurrencia del stock; usar EF solo donde no hace falta
  control fino da más velocidad de desarrollo sin sacrificar la garantía
  central del proyecto.

## Consecuencias

- Sin migraciones de EF: el esquema de estas dos tablas vive en SQL
  embebido idempotente (`SchemaMigrator`), no en `dotnet ef migrations`.
- Cualquier tabla nueva que no toque el control de stock puede añadirse
  con EF Core sin contradecir esta decisión — la regla es por
  responsabilidad, no un rechazo total de EF en el proyecto.
