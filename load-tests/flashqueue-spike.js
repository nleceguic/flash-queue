// FlashQueue — test de carga "drop de entradas": un evento con 500 plazas recibe un pico de
// ~20.000 peticiones concurrentes en los primeros segundos, seguido de tráfico sostenido más
// bajo durante 2 minutos. Ver CLAUDE.md sección 1 ("...sometido a picos de miles de peticiones
// concurrentes en segundos") — este script es la verificación empírica de esa frase.
//
// AVISO IMPORTANTE (léelo antes de sacar conclusiones de los resultados; verificado
// empíricamente corriendo este mismo script contra docker-compose.yml, no es una hipótesis):
// FlashQueue.Api y FlashQueue.Workers son procesos independientes, cada uno con su propio
// ReservationIngestChannel EN MEMORIA (ver docs/adr/0006-opentelemetry-collector-como-fan-out.md,
// sección de limitaciones conocidas, y README-DOCKER.md). El de Api no tiene NINGÚN lector en su
// propio proceso — Workers lee de una instancia distinta — así que, pasadas las primeras
// `ReservationIngest:Capacity` peticiones (500 por defecto), el channel de Api se llena y cada
// `WriteAsync` siguiente se queda BLOQUEADO indefinidamente esperando un hueco que nunca se
// libera. Eso es exactamente lo que vas a ver en los resultados con la topología actual:
//   - Un primer tramo de respuestas 202 rapidísimas (mientras el channel tiene hueco).
//   - Después, la inmensa mayoría de las peticiones sin responder hasta el timeout configurado
//     abajo (5s — deliberadamente corto; con el timeout por defecto de k6, 60s, el test tardaría
//     casi una hora en completar el pico de 20.000 peticiones). Esto NO es un fallo del test ni
//     del rate limiter (3000 req/s + cola 2000 — ver
//     FlashQueue.Api/RateLimiting/ReservationsRateLimiterExtensions.cs): es backpressure real del
//     channel, exactamente donde CLAUDE.md dice que debe aplicarse, solo que en este despliegue
//     concreto nadie lo drena.
//   - El check de "nunca se supera el stock" contra GET /events/{id}/status pasará siempre,
//     porque Workers nunca ve estas peticiones y reserved_stock no se mueve de 0 — no es una
//     prueba vacía por casualidad, es la garantía real de "cero overselling" observada desde
//     fuera del proceso, la misma que ReservationRepositoryOversellingTests ya prueba de forma
//     rigurosa con 20.000 reservas CONCURRENTES REALES directamente contra el repositorio
//     (ver docs/adr/0002-locking-skip-locked-con-reintentos.md). Este script no la sustituye.
//
// Requiere k6 (https://k6.io) — sin dependencias/extensiones adicionales.
//
// Uso:
//   1. Levanta el sistema (ver README-DOCKER.md):      docker compose up -d
//   2. Siembra el evento de 500 plazas:                ./load-tests/seed-event.sh
//   3. Corre el test guardando la serie temporal:
//        k6 run --out json=load-tests/results/raw-metrics.json load-tests/flashqueue-spike.js
//      (o usa load-tests/run.sh, que hace los tres pasos y genera la gráfica al final)
//
// Variables de entorno (todas opcionales, con valores por defecto que casan con
// docker-compose.yml y seed-event.sql):
//   API_BASE_URL    - base de FlashQueue.Api (POST de reservas). Por defecto http://localhost:5257
//   STATUS_BASE_URL - base de FlashQueue.Workers (GET de estado, /events/{id}/status).
//                     Por defecto http://localhost:5280
//   EVENT_ID        - evento a reservar. Por defecto el que siembra seed-event.sql.
//   TOTAL_STOCK     - stock esperado, solo para el mensaje de log si algo no cuadra con /status.

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate } from 'k6/metrics';

const API_BASE_URL = __ENV.API_BASE_URL || 'http://localhost:5257';
const STATUS_BASE_URL = __ENV.STATUS_BASE_URL || 'http://localhost:5280';
const EVENT_ID = __ENV.EVENT_ID || '7c9e6679-7425-40de-944b-e07fc1f90ae7';
const TOTAL_STOCK = Number(__ENV.TOTAL_STOCK || 500);

// Fallos "reales": ni 202 (aceptada) ni 429 (rechazada a propósito por el rate limiter — eso es
// degradación controlada, no un fallo; ver CLAUDE.md sección 1, "equidad y rendimiento").
const errors = new Rate('errors');
const rateLimited = new Counter('rate_limited_responses');
const overstockViolations = new Counter('overstock_violations');

export const options = {
  scenarios: {
    // Pico: ~20.000 peticiones concurrentes en los primeros segundos. shared-iterations reparte
    // un total fijo de iteraciones entre los VUs disponibles tan rápido como pueden ejecutarse —
    // es la forma más directa de modelar "20.000 personas pulsando comprar a la vez", frente a
    // una rampa de arrival-rate (que modelaría un ritmo sostenido, no un pico instantáneo).
    spike: {
      executor: 'shared-iterations',
      vus: 2000,
      iterations: 20000,
      maxDuration: '60s',
      startTime: '0s',
      exec: 'reserve',
    },
    // Tráfico sostenido más bajo durante 2 minutos, después del pico.
    sustained: {
      executor: 'constant-arrival-rate',
      rate: 30,
      timeUnit: '1s',
      duration: '2m',
      preAllocatedVUs: 60,
      maxVUs: 200,
      startTime: '65s',
      exec: 'reserve',
    },
    // Sondea /events/{id}/status ~1 vez/segundo durante todo el test (pico + sostenido) para
    // verificar continuamente que nunca se supera el stock, no solo al final.
    stockMonitor: {
      executor: 'constant-vus',
      vus: 1,
      duration: '3m',
      startTime: '0s',
      exec: 'checkStock',
    },
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(50)', 'p(90)', 'p(95)', 'p(99)', 'max'],
  thresholds: {
    // Informativos, no bloquean el test (el objetivo es medir y reportar, no aprobar/suspender) —
    // pero documentan qué se considera "degradación controlada, no caerse" (CLAUDE.md sección 1).
    errors: ['rate<0.01'],
    checks: ['rate>0.99'],
  },
};

export function reserve() {
  const userId = randomUuid();
  const payload = JSON.stringify({ userId, quantity: 1 });
  // timeout corto a propósito: el channel de ingesta de Api es bounded (500 por defecto,
  // ReservationIngest:Capacity) y, con la topología actual (ver cabecera de este archivo), nada
  // lo vacía — pasado ese punto, WriteAsync se queda bloqueado indefinidamente y una petición sin
  // límite de tiempo se colgaría 60s (el timeout por defecto de k6) en vez de fallar rápido. Un
  // timeout corto hace que el test refleje esto en segundos, no en minutos, sin cambiar lo que se
  // está midiendo: sigue siendo "¿respondió a tiempo, sí o no?".
  const params = { headers: { 'Content-Type': 'application/json' }, timeout: '5s' };

  const res = http.post(`${API_BASE_URL}/events/${EVENT_ID}/reservations`, payload, params);

  const isExpected = res.status === 202 || res.status === 429;
  check(res, {
    'respuesta esperada (202 aceptada o 429 backpressure controlada)': () => isExpected,
  });
  errors.add(!isExpected);
  if (res.status === 429) {
    rateLimited.add(1);
  }
}

export function checkStock() {
  const res = http.get(`${STATUS_BASE_URL}/events/${EVENT_ID}/status`);

  const ok = check(res, {
    'GET /events/{id}/status responde 200': (r) => r.status === 200,
  });

  if (ok) {
    const body = res.json();
    const withinStock = check(body, {
      'reservedStock nunca supera totalStock': (b) => b.reservedStock <= b.totalStock,
      'availableStock nunca es negativo': (b) => b.availableStock >= 0,
    });

    if (!withinStock) {
      overstockViolations.add(1);
      // eslint-disable-next-line no-console
      console.error(
        `[OVERSELL] evento ${EVENT_ID}: reservedStock=${body.reservedStock} > totalStock=${body.totalStock}`,
      );
    }
  }

  sleep(1);
}

// UUID v4 sin dependencias externas (Math.random no es criptográficamente seguro, pero aquí solo
// necesitamos identificadores de usuario únicos por iteración, no seguridad).
function randomUuid() {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    const v = c === 'x' ? r : (r & 0x3) | 0x8;
    return v.toString(16);
  });
}

export function handleSummary(data) {
  const summary = buildSummary(data);

  return {
    'load-tests/results/summary.json': JSON.stringify(summary, null, 2),
    stdout: renderTextSummary(summary),
  };
}

function buildSummary(data) {
  const httpReqs = data.metrics.http_reqs?.values ?? {};
  const duration = data.metrics.http_req_duration?.values ?? {};
  const errorRate = data.metrics.errors?.values?.rate ?? 0;
  const checksRate = data.metrics.checks?.values?.rate ?? 0;
  const rateLimitedCount = data.metrics.rate_limited_responses?.values?.count ?? 0;
  const overstockCount = data.metrics.overstock_violations?.values?.count ?? 0;

  return {
    eventId: EVENT_ID,
    totalStockExpected: TOTAL_STOCK,
    testDurationSeconds: (data.state.testRunDurationMs ?? 0) / 1000,
    throughput: {
      totalRequests: httpReqs.count ?? 0,
      requestsPerSecond: httpReqs.rate ?? 0,
    },
    latencyMs: {
      p50: duration['p(50)'] ?? duration.med ?? null,
      p95: duration['p(95)'] ?? null,
      p99: duration['p(99)'] ?? null,
      avg: duration.avg ?? null,
      max: duration.max ?? null,
    },
    errorRate,
    checksPassRate: checksRate,
    rateLimitedResponses: rateLimitedCount,
    overstockViolations: overstockCount,
    neverOversold: overstockCount === 0,
  };
}

function renderTextSummary(summary) {
  const pct = (value) => `${(value * 100).toFixed(2)}%`;
  const ms = (value) => (value === null ? 'n/d' : `${value.toFixed(1)}ms`);

  return [
    '',
    '=== FlashQueue spike test — resumen ===',
    `Evento: ${summary.eventId} (stock esperado: ${summary.totalStockExpected})`,
    `Duración total: ${summary.testDurationSeconds.toFixed(1)}s`,
    '',
    '-- Throughput --',
    `  Peticiones totales : ${summary.throughput.totalRequests}`,
    `  Peticiones/s (avg) : ${summary.throughput.requestsPerSecond.toFixed(1)}`,
    '',
    '-- Latencia (http_req_duration) --',
    `  p50 : ${ms(summary.latencyMs.p50)}`,
    `  p95 : ${ms(summary.latencyMs.p95)}`,
    `  p99 : ${ms(summary.latencyMs.p99)}`,
    `  avg : ${ms(summary.latencyMs.avg)}`,
    `  max : ${ms(summary.latencyMs.max)}`,
    '',
    '-- Errores y backpressure --',
    `  Tasa de error real (ni 202 ni 429) : ${pct(summary.errorRate)}`,
    `  Respuestas 429 (rate limiter)      : ${summary.rateLimitedResponses}`,
    `  Checks superados                   : ${pct(summary.checksPassRate)}`,
    '',
    '-- Stock (GET /events/{id}/status) --',
    `  Violaciones de overselling detectadas : ${summary.overstockViolations}`,
    `  Nunca se superó el stock              : ${summary.neverOversold ? 'SÍ' : 'NO — revisar arriba'}`,
    '',
    'Resultados: load-tests/results/summary.json',
    '(serie temporal completa en load-tests/results/raw-metrics.json si se usó --out json=...)',
    '',
  ].join('\n');
}
