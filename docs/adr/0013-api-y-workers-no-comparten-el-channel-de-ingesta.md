# ADR 0013: Api y Workers no compartían el channel de ingesta — unificados en un solo proceso

- **Fecha**: 2026-08-08
- **Estado**: Aceptada — implementada. Diagnostica la limitación y documenta la corrección aplicada
  en la misma ADR: nunca llegó a publicarse solo como diagnóstico, así que no tiene sentido
  partirla en dos.

## Contexto

`ReservationIngestChannel` es un singleton registrado en el contenedor de
DI de cada proceso (`builder.Services.AddSingleton(sp => new
ReservationIngestChannel(...))`, tanto en `FlashQueue.Api/Program.cs` como
en `FlashQueue.Workers/Program.cs`). Un singleton de DI vive dentro de un
único proceso — no hay nada que sincronice dos instancias en procesos
distintos. `docker-compose.yml` (fase 10) despliega `api` y `workers` como
dos contenedores separados, cada uno arrancando su propio proceso .NET.

## Qué pasaba antes de esta corrección, con precisión

Con `Api` y `Workers` como procesos separados (docker-compose, o
simplemente dos `dotnet run` distintos en local — el problema era de
procesos, no de contenedores):

1. Un `POST /events/{eventId}/reservations` llega a `FlashQueue.Api`,
   pasa el rate limiter, y `ReservationsEndpoints.CreateReservationAsync`
   escribe el item en **el channel de Api**.
2. `FlashQueue.Workers` nunca lee de ese channel — lee del suyo propio,
   que es una instancia completamente distinta y que nadie escribe nunca
   (Workers no expone ningún endpoint de ingesta).
3. Mientras el channel de Api tiene hueco (`ReservationIngest:Capacity`,
   500 por defecto), `WriteAsync` completa de inmediato y el cliente
   recibe `202 Accepted`. **Ese 202 es un falso positivo**: la reserva
   queda en el channel de Api, sin lector, hasta que el proceso termina —
   nunca se persiste en Postgres, nunca pasa a `Confirmed`/`Rejected`,
   nunca se publica su evento a RabbitMQ.
4. Agotada esa capacidad, cada `WriteAsync` siguiente se queda **bloqueada
   indefinidamente** (`BoundedChannelFullMode.Wait`, nadie libera hueco
   nunca): no hay excepción ni código de error, la petición HTTP
   simplemente no responde hasta que el cliente agota su propio timeout.

**Conclusión sin rodeos, para poder repetirla en una entrevista: no era una
degradación parcial ni un problema de fairness — era pérdida total.** El
100% de las reservas enviadas a `Api` en esa topología se perdían, solo
que una fracción de ellas (~500 de 20.000 en el test de carga, ~2%) lo
hacía de forma silenciosa tras devolver 202, y el resto lo hacía colgando
la petición sin respuesta. Verificado empíricamente corriendo
`load-tests/flashqueue-spike.js` contra `docker compose up -d` (no fue una
hipótesis) — ver ADR 0008 para las cifras exactas de esa ejecución (que
quedan como registro histórico de la topología ya corregida).

### Qué NO afecta

- **No afecta al fairness round-robin entre eventos** (ADR 0001): ese
  mecanismo vive dentro de `ReservationProcessingWorker`, en el proceso
  de Workers, operando sobre el channel de Workers. El mecanismo en sí
  sigue siendo correcto — simplemente nunca recibe tráfico real en esta
  topología, porque nadie escribe en el channel que lee.
- **No afecta a la garantía de cero overselling** (ADR 0002, 0010):
  `ReservationRepositoryOversellingTests` prueba `ReservationRepository`
  directamente, sin pasar por el channel ni por ningún proceso de
  ingesta. Esa garantía es independiente de este bug y sigue siendo
  válida.
- **No se detecta con los tests de integración existentes**: tanto
  `ReservationProcessingWorkerWiringTests` como
  `ReservationEventFanoutTests` construyen un único `IHost` donde el
  mismo `ReservationIngestChannel` se registra una vez y tanto el
  "escritor" de la prueba como `ReservationProcessingWorker` comparten
  esa instancia — exactamente el escenario de un solo proceso. Por
  diseño, ninguno de los dos prueba la topología multi-proceso real de
  `docker-compose.yml`, así que ninguno atrapa este bug. La única
  verificación empírica de esta limitación es el test de carga con k6
  contra el sistema desplegado (ADR 0008).

## Por qué se llegó aquí

No es un descuido descubierto ahora por accidente — es una consecuencia
de tres decisiones tomadas correctamente en su momento, cada una para el
problema que tenía delante, sin que ninguna revisara la anterior a la luz
de la siguiente:

1. **ADR 0009** eligió `Channel<T>` en memoria, dentro de un único
   proceso, para resolver backpressure de ingesta con el mínimo coste
   (sin red ni serialización). En ese momento solo existía un proceso —
   la decisión nunca necesitó contemplar más de uno.
2. Al dockerizar el sistema (fase 10), `Api` y `Workers` se desplegaron
   como contenedores/servicios independientes en `docker-compose.yml`,
   siguiendo la separación en dos componentes que CLAUDE.md (sección 2)
   ya describía a nivel de arquitectura lógica ("1. API de ingesta",
   "2. Task queue con backpressure"). Esa separación lógica se tradujo
   directamente en dos procesos deployables sin volver a comprobar si el
   channel en memoria de ADR 0009 seguía siendo válido una vez que dejó
   de haber garantía de que ambos compartieran el mismo proceso del BCL.
3. El hueco se detectó y se documentó explícitamente, en dos momentos
   distintos y con severidad creciente: primero en **ADR 0006**, al
   instrumentar trazas ("cada proceso tiene su propia instancia de
   `ReservationIngestChannel`" — hubo que decidir cómo reconectar la
   traza precisamente *porque* no hay un canal compartido); después, de
   forma mucho más severa, en **ADR 0008**, al correr el test de carga de
   verdad y comprobar que no es solo "una reserva no llega", sino que el
   channel se bloquea indefinidamente pasadas ~500 peticiones. En ese
   punto se decidió explícitamente no arreglarlo como parte de esa tarea
   ("decidido explícitamente que no, por el usuario... Queda como trabajo
   futuro" — ADR 0008, alternativas descartadas).

## Decisión: unificar Api y Workers en un solo proceso

La solución más simple que las tres ADRs citaban como pendiente: que un
único proceso posea la única instancia de `ReservationIngestChannel` que
importa. Se implementó como "Workers también hospeda el endpoint de
ingesta", concretamente:

- `FlashQueue.Api` deja de ser un ejecutable propio (se retiran su
  `Program.cs`, `appsettings*.json`, `launchSettings.json` y `.http`) y
  pasa a ser una librería de endpoints minimal API + rate limiting
  (`Microsoft.NET.Sdk` en vez de `Microsoft.NET.Sdk.Web`, con
  `FrameworkReference` explícita a `Microsoft.AspNetCore.App`, igual que
  ya hacía `FlashQueue.Workers`). El proyecto se mantiene separado en la
  solución a propósito — sigue reflejando la separación conceptual entre
  "ingesta" y "procesamiento" en el código, solo que ya no como dos
  procesos deployables.
- `FlashQueue.Workers.csproj` referencia `FlashQueue.Api.csproj` y añade
  el paquete `Microsoft.AspNetCore.OpenApi`.
- `FlashQueue.Workers/Program.cs` es ahora el único host: registra el
  `ReservationIngestChannel` (una sola vez), llama a
  `AddReservationsRateLimiting()` y `MapReservationsEndpoints()` (de
  `FlashQueue.Api`), y sigue registrando `ReservationProcessingWorker`
  como `IHostedService` — ambos comparten literalmente la misma instancia
  de canal, resuelta del mismo contenedor de DI.
- `docker-compose.yml` pasa de dos servicios (`api`, `workers`) a uno
  solo (`workers`), que expone los dos puertos históricos (`5257` para
  ingesta, `5280` para `/health/dependencies`) sobre el mismo proceso.

### Efecto colateral en los tests de ingesta

`ReservationsEndpointTests`, `ReservationsBackpressureTests` y
`ReservationsResponsivenessTests` usaban `WebApplicationFactory<Program>`
apuntando a `FlashQueue.Api` como smoke test aislado, sin Postgres ni
RabbitMQ. Al apuntar ahora a `FlashQueue.Workers.Program` (que sí llama a
`AddInfrastructure`), necesitan Postgres/RabbitMQ reales vía Testcontainers
como cualquier otro test de este proyecto — y, al construirse el host
también arranca `ReservationProcessingWorker` de verdad, que compite por
leer el channel. `ReservationsBackpressureTests` y
`ReservationsResponsivenessTests` retiran ese `IHostedService` real del
host de prueba (necesitan observar el channel de forma aislada y
controlada); `ReservationsEndpointTests` lo deja correr y pasó a verificar
persistencia real en Postgres en vez de leer el channel a mano — es,
literalmente, la prueba de que el bug que documenta este ADR ya no ocurre.

Un detalle no obvio: `InfrastructureServiceCollectionExtensions.AddInfrastructure`
lee `ConnectionStrings:FlashQueueDb` de forma *eager*, antes de
`WebApplicationBuilder.Build()` — las sobrescrituras de configuración de
`WebApplicationFactory.WithWebHostBuilder(...).ConfigureAppConfiguration(...)`
solo se aplican en el momento (diferido) del propio `Build()`, demasiado
tarde para esa lectura. La solución fue inyectar Postgres/RabbitMQ por
variable de entorno en vez de por configuración en memoria — el mismo
mecanismo que ya usa `docker-compose.yml` en producción, no un rodeo
específico de los tests (ver `Support/InfrastructureEnvironmentVariables.cs`).

## Consecuencias

- `POST /events/{eventId}/reservations` ahora persiste de forma fiable en
  la topología real de `docker-compose.yml`: el mismo proceso que acepta
  la petición HTTP es el que la procesa. La garantía de negocio central
  del proyecto (cero overselling) ya estaba probada de forma independiente
  de este bug (`ReservationRepositoryOversellingTests`) y sigue estándolo.
- `load-tests/flashqueue-spike.js` y `load-tests/README.md` quedan
  desactualizados en su aviso sobre la limitación — se actualizan aparte
  para reflejar la topología unificada; los números de la última ejecución
  documentados en el README quedan como hallazgo histórico de la topología
  ya corregida, no como medición vigente.
- `ReservationProcessingWorkerWiringTests` queda parcialmente redundante:
  ya probaba exactamente este escenario (un solo host, un solo channel).
  Se conserva como red de seguridad de regresión, no se retira.
- Se pierde, sobre el papel, la posibilidad de escalar ingesta y
  procesamiento como contenedores independientes — capacidad que
  `docker-compose.yml` ya ofrecía formalmente antes de esta ADR, pero que
  nunca llegó a funcionar de verdad por el propio bug que aquí se corrige.
