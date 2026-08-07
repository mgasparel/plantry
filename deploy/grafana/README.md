# Grafana dashboards

Checked-in dashboard JSON for the LGTM observability overlay
(`deploy/docker-compose.observability.yml`). The `grafana/otel-lgtm` image
auto-provisions the Prometheus, Loki, and Tempo datasources; these dashboards
bind to them via datasource template variables, so no fixed UIDs are assumed.

## Dashboards

| File | Purpose |
|---|---|
| `plantry-overview.json` | Starter overview: HTTP rate/latency/5xx, recent logs, recent AI-call traces, AI confidence histograms, household-activity counters. Includes a pre-wired "AI tokens by function" panel that stays empty until the `ai.usage.tokens` counter ships (plantry-df6p); the cost-derivation dashboard (plantry-00lg) builds on it. |

## Importing (current workflow)

1. From the Grafana UI, go to Dashboards → New → Import → upload the JSON file.
2. When prompted, select the auto-provisioned Prometheus/Loki/Tempo datasources.

After editing a dashboard in the UI, export it (Share → Export → *Export for
sharing externally*) and commit the JSON back here — the Grafana deployment's
persistent volume keeps state across container recreation, but this repo is
the source of truth.

## Metric-name note

Metric names follow the OTLP→Prometheus translation: dots become underscores,
counters gain `_total`, and durations gain a `_seconds` unit suffix (e.g.
`http.server.request.duration` → `http_server_request_duration_seconds_bucket`,
`plantry.recipes.cooked` → `plantry_recipes_cooked_total`). If a panel shows
no data, confirm the exact name in Explore → Prometheus → metric browser and
adjust the query.
