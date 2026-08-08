# ADR 0010: Locking pesimista vs. concurrencia optimista en Postgres

- **Fecha**: 2026-08-07
- **Estado**: Aceptada

## Contexto

Decidir la estrategia de control de concurrencia para decrementar
`events.reserved_stock` sin overselling. Este ADR es la decisión de nivel
superior (pesimista vs. optimista); el ADR 0002 documenta el mecanismo
concreto elegido dentro de "pesimista" (`SKIP LOCKED` con reintentos).

## Decisión

Locking pesimista: `SELECT ... FOR UPDATE`. Bajo el escenario que este
proyecto existe para demostrar — miles de peticiones concurrentes contra
la **misma fila**, un "drop" de entradas — la contención es la norma, no
la excepción: casi todos los intentos van a colisionar contra la misma
fila. Un lock pesimista serializa el acceso desde el principio en vez de
dejar que el trabajo avance para descubrir el conflicto al final.

## Alternativas descartadas

- **Concurrencia optimista** (columna `version`, `UPDATE ... WHERE version
  = @v`, reintentar en conflicto): gana al lock pesimista cuando la
  contención es baja (muchas filas distintas, pocos conflictos), porque
  evita el coste del lock cuando no hace falta. Pero bajo alta contención
  sobre una única fila — el caso exacto de FlashQueue — casi todos los
  `UPDATE` competirían y fallarían por conflicto de versión, generando una
  tasa de reintentos aplicativos aún mayor que `SKIP LOCKED`, que al menos
  evita hacer trabajo de escritura hasta tener el lock garantizado.
- **Sin lock explícito, confiando en `READ COMMITTED` + un `CHECK` de
  stock**: bajo `READ COMMITTED` dos transacciones pueden leer el mismo
  `reserved_stock` antes de que ninguna haga commit, y ambas confirmarían
  la reserva → overselling real. Haría falta `SERIALIZABLE` (con su propio
  coste de reintentos por conflictos de serialización) para conseguir lo
  mismo sin lock explícito, sin ventaja clara sobre `FOR UPDATE`.

## Consecuencias

- El throughput bajo un único evento muy popular está acotado por
  (tamaño de la sección crítica) × (número de peticiones) — inherente a
  garantizar cero overselling sobre una fila compartida, no una
  limitación evitable de esta implementación.
- La prueba de que esto funciona bajo contención extrema es empírica, no
  solo teórica: `ReservationRepositoryOversellingTests` (20.000 reservas
  concurrentes reales contra un evento con 500 plazas).
