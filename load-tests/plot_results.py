#!/usr/bin/env python3
"""Genera un PNG con la evolución de throughput y latencia durante flashqueue-spike.js.

Lee la serie temporal cruda que produce `k6 run --out json=...` (un objeto JSON por línea:
metadatos de métrica una vez, y un "Point" por cada muestra real) y el resumen agregado que
escribe `handleSummary()` en el propio script de k6. No necesita nada más que matplotlib —
python plot_results.py --help para las opciones.

Pensado para usarse tal cual en el README y en el vídeo demo (ver load-tests/run.sh, que
encadena k6 + este script en un solo comando).
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

try:
    import matplotlib

    matplotlib.use("Agg")  # sin display: solo escribir el PNG a disco
    import matplotlib.pyplot as plt
except ImportError:
    print(
        "Falta matplotlib. Instala las dependencias con:\n"
        "  pip install -r load-tests/requirements.txt",
        file=sys.stderr,
    )
    raise

# k6 emite hasta 100ns de precisión (7 dígitos decimales); datetime.fromisoformat de Python solo
# acepta hasta microsegundos (6 dígitos) — hay que truncar antes de parsear.
_FRACTIONAL_SECONDS_RE = re.compile(r"(\.\d{6})\d*")


def parse_k6_timestamp(raw: str) -> datetime:
    truncated = _FRACTIONAL_SECONDS_RE.sub(r"\1", raw)
    return datetime.fromisoformat(truncated)


@dataclass
class TimeSeries:
    throughput_buckets: dict[int, int] = field(default_factory=dict)  # bucket (s) -> nº peticiones
    latency_buckets: dict[int, list[float]] = field(default_factory=dict)  # bucket (s) -> [duraciones ms]


def load_raw_metrics(raw_path: Path, bucket_seconds: int) -> TimeSeries:
    series = TimeSeries()
    start: datetime | None = None
    rows: list[tuple[str, datetime, float]] = []

    with raw_path.open(encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue

            row = json.loads(line)
            if row.get("type") != "Point":
                continue

            metric = row.get("metric")
            if metric not in ("http_reqs", "http_req_duration"):
                continue

            data = row["data"]
            timestamp = parse_k6_timestamp(data["time"])
            rows.append((metric, timestamp, float(data["value"])))

            if start is None or timestamp < start:
                start = timestamp

    if start is None:
        raise ValueError(
            f"No se encontraron puntos de http_reqs/http_req_duration en {raw_path}. "
            "¿Se ejecutó k6 con --out json=...?"
        )

    for metric, timestamp, value in rows:
        offset_seconds = (timestamp - start).total_seconds()
        bucket = int(offset_seconds // bucket_seconds) * bucket_seconds

        if metric == "http_reqs":
            series.throughput_buckets[bucket] = series.throughput_buckets.get(bucket, 0) + int(value)
        else:
            series.latency_buckets.setdefault(bucket, []).append(value)

    return series


def percentile(values: list[float], pct: float) -> float:
    if not values:
        return 0.0

    ordered = sorted(values)
    index = (len(ordered) - 1) * pct
    lower = int(index)
    upper = min(lower + 1, len(ordered) - 1)
    fraction = index - lower

    return ordered[lower] + (ordered[upper] - ordered[lower]) * fraction


def plot(series: TimeSeries, summary: dict | None, bucket_seconds: int, output_path: Path) -> None:
    all_buckets = sorted(set(series.throughput_buckets) | set(series.latency_buckets))
    if not all_buckets:
        raise ValueError("No hay datos que graficar.")

    times = all_buckets
    throughput = [series.throughput_buckets.get(t, 0) / bucket_seconds for t in times]
    p50 = [percentile(series.latency_buckets.get(t, []), 0.50) for t in times]
    p95 = [percentile(series.latency_buckets.get(t, []), 0.95) for t in times]
    p99 = [percentile(series.latency_buckets.get(t, []), 0.99) for t in times]

    fig, (ax_throughput, ax_latency) = plt.subplots(2, 1, figsize=(12, 8), sharex=True)

    title = "FlashQueue — test de carga (pico + tráfico sostenido)"
    if summary:
        stock_note = "sin overselling" if summary.get("neverOversold") else "⚠ OVERSELLING DETECTADO"
        title += f"\n{summary.get('throughput', {}).get('totalRequests', '?')} peticiones · {stock_note}"
    fig.suptitle(title, fontsize=13, fontweight="bold")

    # Marca la fase de pico (spike scenario de flashqueue-spike.js: 0–50s) para distinguirla
    # visualmente del tráfico sostenido que viene después.
    spike_end = 65
    for ax in (ax_throughput, ax_latency):
        ax.axvspan(0, min(spike_end, max(times)), color="orange", alpha=0.08, label="Pico (~20k peticiones)")

    ax_throughput.plot(times, throughput, color="#1f77b4", linewidth=1.5)
    ax_throughput.set_ylabel("Peticiones / s")
    ax_throughput.set_title("Throughput")
    ax_throughput.grid(True, alpha=0.3)
    ax_throughput.legend(loc="upper right")

    ax_latency.plot(times, p50, label="p50", color="#2ca02c", linewidth=1.5)
    ax_latency.plot(times, p95, label="p95", color="#ff7f0e", linewidth=1.5)
    ax_latency.plot(times, p99, label="p99", color="#d62728", linewidth=1.5)
    ax_latency.set_ylabel("Latencia (ms)")
    ax_latency.set_xlabel("Tiempo desde el inicio del test (s)")
    ax_latency.set_title("Latencia (http_req_duration)")
    ax_latency.grid(True, alpha=0.3)
    ax_latency.legend(loc="upper right")

    fig.tight_layout()
    output_path.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(output_path, dpi=150)
    print(f"Gráfica guardada en {output_path}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--raw",
        type=Path,
        default=Path("load-tests/results/raw-metrics.json"),
        help="Serie temporal cruda de k6 (k6 run --out json=<este archivo>).",
    )
    parser.add_argument(
        "--summary",
        type=Path,
        default=Path("load-tests/results/summary.json"),
        help="Resumen agregado que escribe handleSummary() en flashqueue-spike.js.",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=Path("load-tests/results/flashqueue-spike.png"),
        help="Ruta del PNG a generar.",
    )
    parser.add_argument(
        "--bucket-seconds",
        type=int,
        default=1,
        help="Tamaño del bucket temporal para agregar throughput/latencia (por defecto 1s).",
    )
    args = parser.parse_args()

    if not args.raw.exists():
        parser.error(
            f"No existe {args.raw}. Corre primero:\n"
            f"  k6 run --out json={args.raw} load-tests/flashqueue-spike.js"
        )

    summary = None
    if args.summary.exists():
        summary = json.loads(args.summary.read_text(encoding="utf-8"))
    else:
        print(f"Aviso: no se encontró {args.summary}, la gráfica se generará sin el título resumen.", file=sys.stderr)

    series = load_raw_metrics(args.raw, args.bucket_seconds)
    plot(series, summary, args.bucket_seconds, args.output)


if __name__ == "__main__":
    main()
