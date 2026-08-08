# ADR 0007: Downgrade de MassTransit a 8.x (v9 exige licencia comercial)

- **Fecha**: 2026-08-07
- **Estado**: Aceptada — corrige ADR 0003

## Contexto

Al levantar por primera vez el sistema completo con `docker compose up`
(este ADR nace directamente del trabajo de esa tarea), `workers` y los tres
`FlashQueue.Consumers.*` fallaban al arrancar con:

```
MassTransit.ConfigurationException: The bus configuration is invalid:
[Failure] License must be specified with SetLicense/SetLicenseLocation or
by setting the MT_LICENSE/MT_LICENSE_PATH environment variables.
```

`MassTransit.RabbitMQ` estaba fijado en `9.2.0` desde ADR 0003. MassTransit
v9 pasó a licencia comercial sin nivel gratuito — v8 (y anteriores) sigue
siendo libre y de código abierto (Apache 2.0), con soporte de seguridad
confirmado al menos hasta finales de 2026. No hay ninguna forma gratuita de
obtener una licencia v9 para este caso de uso.

## Decisión

Downgrade de `MassTransit.RabbitMQ` a la última versión estable de la serie
8.x (`8.5.10`) en `FlashQueue.Infrastructure.csproj` — el único
`PackageReference` explícito a MassTransit del monorepo; `MassTransit` y
`MassTransit.Abstractions` llegan como dependencias transitivas de ese mismo
paquete, así que basta con cambiar una línea.

Se comprobó que la API usada en todo el proyecto (`AddMassTransit`,
`UsingRabbitMq`, `cfg.Host(...)`, `cfg.UseMessageRetry(...)`,
`cfg.ConfigureEndpoints(...)`, `KebabCaseEndpointNameFormatter`, `IBus`,
`IPublishEndpoint.Publish<T>`) es idéntica entre 8.x y 9.x — no hizo falta
tocar ni una línea de `FlashQueue.Infrastructure/Messaging/` ni de ningún
consumidor. Los 68 tests (unitarios + integración, estos últimos contra
RabbitMQ real vía Testcontainers) siguen en verde sin cambios tras el
downgrade.

## Alternativas descartadas

- **Obtener y configurar una licencia MassTransit v9**: descartado — no
  existe un nivel gratuito, y este es un proyecto de portfolio sin
  presupuesto para licencias comerciales de terceros.
- **Sustituir MassTransit por otro cliente de RabbitMQ** (p. ej.
  `RabbitMQ.Client` directo): descartado por desproporcionado — habría que
  reimplementar a mano el enrutamiento por tipo de mensaje, el
  `KebabCaseEndpointNameFormatter`, el registro de consumidores y los
  reintentos del lado consumidor (ADR 0003) que MassTransit ya resuelve, sin
  ganar nada frente a simplemente fijar una versión anterior de la misma
  librería.

## Consecuencias

- Cualquier futura actualización de `MassTransit.RabbitMQ` debe permanecer
  dentro de la serie 8.x mientras este proyecto no tenga presupuesto para una
  licencia — comprobar el modelo de licencia del release antes de subir de
  versión, no asumir que un `dotnet add package` sin especificar versión
  seguirá siendo seguro.
- Este ADR corrige, no sustituye, a ADR 0003: la decisión de usar MassTransit
  sobre RabbitMQ para publicar eventos de dominio se mantiene igual: cambia
  únicamente la versión fijada del paquete.
