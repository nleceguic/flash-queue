# ADR 0008: Test de carga con k6 y `GET /events/{id}/status`

- **Fecha**: 2026-08-07
- **Estado**: Aceptada. Las Decisiones 1 y 2 (endpoint en Workers, `shared-iterations` para el
  pico) siguen vigentes. La Decisión 3 (el hallazgo empírico de bloqueo indefinido del channel) es
  ahora historia resuelta — la causa raíz que documenta queda diagnosticada y corregida en
  [ADR 0013](0013-api-y-workers-no-comparten-el-channel-de-ingesta.md); las cifras de esta página
  son del sistema *antes* de esa corrección.

## Contexto

CLAUDE.md (sección 1) pide que el sistema "se comporte correctamente
(degradarse de forma controlada, no caerse) bajo carga real, con métricas
verificables (throughput, p95/p99, tasa de error)". Hasta ahora esa
afirmación no tenía una verificación empírica repetible — solo los tests de
integración (`ReservationRepositoryOversellingTests`, 20.000 reservas
concurrentes directas contra el repositorio) prueban la garantía de
no-overselling, y lo hacen a nivel de repositorio, no como una carga HTTP
real contra el sistema desplegado.

## Decisión 1: `GET /events/{eventId}/status` vive en `FlashQueue.Workers`, no en `FlashQueue.Api`

El endpoint (`FlashQueue.Workers/Events/EventStatusEndpoints.cs`) lee
`total_stock`/`reserved_stock` directamente de Postgres vía `NpgsqlDataSource`
(ya registrado por `AddInfrastructure`). Se descartó ponerlo en
`FlashQueue.Api` porque ese proceso, por diseño (CLAUDE.md sección 2, punto
1), es puramente de ingesta — nunca toca Postgres directamente, solo escribe
en el channel. Añadirle una consulta de solo lectura a Postgres rompería esa
frontera solo para un endpoint de diagnóstico usado por un test de carga, no
por el flujo de negocio. `FlashQueue.Workers` ya tiene acceso a Postgres y ya
expone una pequeña superficie HTTP de solo lectura (`/health/dependencies`,
ADR 0004) — este es exactamente el mismo tipo de endpoint.

Consecuencia práctica: `load-tests/flashqueue-spike.js` necesita dos URLs
base (`API_BASE_URL` para los `POST`, `STATUS_BASE_URL` para el `GET` de
estado), no una — refleja fielmente la topología real de dos procesos
independientes.

## Decisión 2: `shared-iterations` para el pico, no `ramping-arrival-rate`

El pico ("20.000 peticiones concurrentes en los primeros segundos") se
modela con el executor `shared-iterations` de k6 (2.000 VUs, 20.000
iteraciones totales, repartidas tan rápido como los VUs puedan ejecutarlas) —
no con una rampa de `arrival-rate`, que modelaría un *ritmo* sostenido
creciente, no un *pico* instantáneo de un número fijo de peticiones. Es la
traducción más directa de "20.000 personas pulsando comprar a la vez" al
modelo de ejecución de k6.

## Decisión 3: el resultado empírico del pico revela una limitación real, más severa de lo documentado hasta ahora — y así se deja documentado en el propio script

Al correr `flashqueue-spike.js` de verdad contra `docker compose up -d`
(no es una hipótesis, se verificó en desarrollo), el pico de 20.000
peticiones produce prácticamente 100% de fallos tras los primeros ~500. La
causa no es un fallo de esta tarea ni del rate limiter: `ReservationIngestChannel`
de `FlashQueue.Api` es un `Channel` **bounded** (`ReservationIngest:Capacity`,
500 por defecto) y, con la topología de procesos separados ya documentada en
ADR 0006, **nada lo vacía dentro del propio proceso de Api** — Workers lee de
una instancia completamente distinta. `docs/adr/0006` y `README-DOCKER.md`
ya avisaban de que una reserva aceptada no llega a persistirse; este ADR deja
constancia de la consecuencia adicional, más severa, descubierta al someter
el sistema a carga real: pasadas las primeras ~500 peticiones, cada
`WriteAsync` siguiente se queda bloqueado indefinidamente (no falla, no
responde) hasta que el cliente agota su propio timeout.

Por eso `reserve()` en el script usa un timeout de petición corto y
explícito (5s, no los 60s por defecto de k6): con el timeout por defecto, el
pico de 20.000 peticiones habría tardado casi una hora en completarse
esperando a que cada una colgara los 60s completos, sin aportar ninguna
información adicional frente a fallar rápido a los 5s. La cabecera de
`flashqueue-spike.js` documenta esto en detalle para quien vaya a interpretar
los resultados o grabar el vídeo demo con ellos.

`load-tests/run.sh` también tiene que tolerar que `k6 run` termine con código
de salida distinto de cero (los `thresholds` del script se incumplen, como es
de esperar) sin abortar el resto del pipeline (`set -e` + `set +e`
localizado) — la gráfica debe generarse igual, con los datos obtenidos, en
vez de quedar a medias.

## Alternativas descartadas

- **Arreglar el canal de ingesta cross-proceso como parte de esta tarea**:
  decidido explícitamente que no, por el usuario, al confirmarle este hueco
  antes de escribir el script — es un cambio de arquitectura mayor,
  independiente de "escribir un test de carga", con su propio análisis y
  riesgo. Queda como trabajo futuro.
- **Bajar `vus`/`iterations` del pico para "que salga bien"**: descartado —
  ocultaría precisamente el comportamiento que un test de carga real existe
  para revelar. El test se ajustó (timeout de petición, duración de las
  fases) para que sea *rápido de correr y fácil de interpretar*, no para que
  "pase".

## Consecuencias

- El test de carga, tal y como está hoy, mide con rigor el comportamiento de
  `FlashQueue.Api` bajo un pico real (throughput, latencia, backpressure del
  channel) y confirma — vía `GET /events/{id}/status`, sondeado
  continuamente durante todo el test, no solo al final — que nunca se supera
  el stock, aunque en la topología actual esa comprobación esté
  necesariamente vacía de contenido adicional (ya lo garantiza, de forma
  rigurosa, `ReservationRepositoryOversellingTests`). No sustituye a esa
  prueba; la complementa desde fuera del proceso.
- Cuando exista un puente real entre `FlashQueue.Api` y `FlashQueue.Workers`
  (la mejora futura mencionada arriba), este mismo script, sin cambios,
  empezará a ejercitar también el pipeline de persistencia bajo el pico
  completo — es una de las señales que confirmarán que ese trabajo futuro
  quedó bien resuelto.
