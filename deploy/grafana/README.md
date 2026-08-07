# Grafana dashboards

Checked-in dashboard JSON for the LGTM observability overlay
(`deploy/docker-compose.observability.yml`). The `grafana/otel-lgtm` image
auto-provisions the Prometheus, Loki, and Tempo datasources; these dashboards
bind to them via datasource template variables, so no fixed UIDs are assumed.

## Dashboards

| File | Purpose |
|---|---|
| `plantry-overview.json` | Starter overview: HTTP rate/latency/5xx, recent logs, recent AI-call traces, AI confidence histograms, household-activity counters, AI token usage and estimated cost by function. |

### AI cost panels (plantry-00lg)

The "Estimated AI cost by function" and "Estimated AI cost (selected range)" panels
derive cost from the `ai_usage_tokens_total` counter (plantry-df6p) — dimensioned by
`ai_function`, `ai_model`, and `ai_token_kind` (`input`/`output`) — multiplied by
per-model, per-token-kind prices held in the `price_input_usd_per_mtok` /
`price_output_usd_per_mtok` dashboard variables (Dashboard settings → Variables).
Pricing is deliberately kept **in the dashboard**, never in `AiOptions` or app config,
so the volatile pricing table stays out of the codebase (plantry-jp80's design). The
default variable values price `google/gemini-2.5-flash` (the current `AiOptions.Model`
default, shared by all six AI adapters). Which model's usage feeds the cost panels is
controlled by the `model` dashboard variable (single-select, populated from
`label_values(ai_usage_tokens_total, ai_model)`) — the model filter follows the
`$model` variable; update the two price variables when the model or pricing changes.
`model` is deliberately single-select: the price variables are per-model, so summing
usage from differently-priced models under one price pair would silently misprice.

The "AI calls per function" panel queries `traces_spanmetrics_calls_total` in the
bundled Prometheus — a metric produced by Tempo's own span-metrics generator (the
`span-metrics` processor, enabled by default in the `grafana/docker-otel-lgtm` image)
remote-writing call-count series per span name. Cross-check it against the token/cost
panels — a function with cost but no call-rate (or vice versa) means one of the two
pipelines is broken.

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

The "AI calls per function" panel's metric name is a particular case to double-check:
Tempo's own span-metrics generator produces `traces_spanmetrics_calls_total`, which is
**not** the same name as the OTel-collector spanmetrics-connector's
`traces_span_metrics_calls_total` (note the extra underscore) — a different component
this stack doesn't use for this path. If the panel is empty, check Grafana Explore for
both `traces_spanmetrics_calls_total` and `traces_span_metrics_calls_total` to see
which one the deployed stack actually produced.
