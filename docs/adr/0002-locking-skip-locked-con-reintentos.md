# ADR 0002: `SELECT ... FOR UPDATE SKIP LOCKED` con reintentos en ReservationRepository

- **Fecha**: 2026-08-07
- **Estado**: Aceptada

## Contexto

`ReservationRepository.ReserveAsync` debe garantizar, bajo miles de intentos
concurrentes contra la misma fila de `events`, que nunca se reserve más stock
del disponible (cero overselling) y que ninguna petición se pierda ni quede
en un estado ambiguo. CLAUDE.md (sección 2, punto 3) pide explícitamente
`SELECT ... FOR UPDATE SKIP LOCKED` "para garantizar consistencia de stock
bajo concurrencia sin bloqueos innecesarios" — este ADR documenta cómo se
interpreta y resuelve esa frase en código real.

`FOR UPDATE` (sin `SKIP LOCKED`) también sería correcto para la consistencia:
Postgres serializaría las transacciones que compiten por la misma fila,
haciéndolas esperar en una cola interna hasta que la primera libere el lock.
El problema no es de corrección sino de recursos: cada transacción que espera
mantiene su conexión (`NpgsqlConnection`) ocupada e inactiva durante toda la
espera. Con miles de peticiones concurrentes contra el mismo evento (el caso
de un "drop" popular, justo el escenario que este proyecto existe para
demostrar), eso satura el pool de conexiones de Npgsql con conexiones que no
hacen nada más que esperar, y ese agotamiento del pool puede propagarse a
otros eventos que no tienen ninguna contención real.

## Decisión

`SELECT ... FOR UPDATE SKIP LOCKED` nunca bloquea: si la fila ya está
bloqueada por otra transacción, devuelve cero filas de inmediato. Sobre esa
base, `ReservationRepository.AcquireEventStockAsync` implementa un bucle de
reintento explícito:

1. Antes de intentar el lock, se comprueba (sin bloquear) que el evento
   existe — así se distingue "la fila está ocupada, reintenta" de "el evento
   no existe", que de otro modo son indistinguibles (ambos devuelven cero
   filas).
2. Se ejecuta `SELECT ... FOR UPDATE SKIP LOCKED` dentro de la misma
   transacción. Si devuelve una fila, se tiene el lock y se continúa.
3. Si no devuelve fila (otra transacción tiene el lock en ese instante), se
   espera un intervalo corto (`ReservationRepositoryOptions.LockRetryDelay`,
   2ms por defecto, con un pequeño jitter aleatorio de 0–2ms para evitar que
   varios reintentadores despierten exactamente a la vez) y se reintenta el
   mismo `SELECT` en la misma transacción — reintentar dentro de la
   transacción ya abierta es válido porque un intento fallido no deja ningún
   lock a medio adquirir.
4. Si se supera `LockAcquisitionTimeout` (30s por defecto) sin conseguir el
   lock, se lanza `TimeoutException`, que el llamante (`ReservationProcessingWorker`)
   ya captura y registra sin tumbar el worker.

Cada intento, con o sin éxito, es una operación puntual y no bloqueante: la
conexión nunca queda "colgada" esperando un lock de Postgres, solo espera de
forma asíncrona (sin ocupar un hilo) un intervalo corto controlado por
nuestro propio código antes de reintentar.

## Alternativas descartadas

- **`FOR UPDATE` bloqueante (sin `SKIP LOCKED`)**: correcto pero, como se
  explica arriba, cada esperador mantiene una conexión del pool ocupada e
  inactiva durante toda la cola de espera de Postgres — el riesgo de
  agotamiento del pool bajo el escenario de carga que este proyecto está
  diseñado para demostrar es justo lo que CLAUDE.md pide evitar con "sin
  bloqueos innecesarios".
- **Concurrencia optimista (columna `version` + `UPDATE ... WHERE version = @v`,
  reintentando en caso de conflicto)**: evita el lock pesimista por completo,
  pero bajo un evento con miles de intentos simultáneos casi todos los
  `UPDATE` competirían por la misma fila y fallarían por conflicto de
  versión, generando una tasa de reintentos aún mayor que con `SKIP LOCKED`
  (que al menos evita hacer ningún trabajo de escritura hasta tener el lock
  garantizado). Además complica la lógica de reintento en la capa de
  aplicación sin beneficio claro sobre el enfoque pesimista para este caso de
  uso (contención muy alta sobre una sola fila, no contención ocasional entre
  muchas filas distintas, que es donde el optimismo suele ganar).
- **Cola de espera en memoria por evento dentro del propio proceso (en vez de
  reintentar contra Postgres)**: ya existe una forma de esto — el
  round-robin de `ReservationProcessingWorker` (ver ADR 0001) limita cuántas
  reservas del mismo evento se despachan a la vez a nivel de proceso. Pero
  eso no elimina la contención a nivel de fila cuando varias réplicas del
  worker (o, más adelante, varios procesos) atacan el mismo evento
  simultáneamente — la garantía final de no-overselling tiene que vivir en
  la base de datos, no solo en la orquestación en memoria de un único
  proceso.

## Consecuencias

- El tiempo total para procesar un aluvión de peticiones contra un único
  evento está dominado por el tamaño de la sección crítica (un `UPDATE` de
  una fila + un `INSERT`, típicamente submilisegundos) multiplicado por el
  número de peticiones, porque solo una transacción puede tener el lock a la
  vez — esto es inherente a garantizar cero overselling sobre una fila
  compartida, no una limitación evitable de esta implementación en concreto.
- `LockRetryDelay` y `LockAcquisitionTimeout` son configurables
  (`ReservationRepositoryOptions`, sección `ReservationRepository` en
  configuración) para poder ajustarlos según el hardware de destino sin
  recompilar.
- El test de integración `ReservationRepositoryOverselling` (Testcontainers,
  20.000 reservas concurrentes contra un evento con stock 500) es la
  verificación empírica de que este mecanismo no pierde peticiones ni permite
  overselling bajo contención extrema.
