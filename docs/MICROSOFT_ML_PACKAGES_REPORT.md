# Microsoft.ML Package Opportunity Report for *lucid*RAG

Date: 2026-02-08

## Executive Summary

Current state: the repo already uses a substantial part of the Microsoft ML ecosystem (ONNX Runtime, Tokenizers, TorchSharp, ML.NET base, Vision).

Best net-new opportunities:

1. **Microsoft.ML.AutoML**: highest leverage for ranking/quality model selection and weight tuning.
2. **Microsoft.ML.TimeSeries**: strong fit for anomaly detection on ingestion/retrieval quality metrics.
3. **Microsoft.ML.Recommender**: optional, only if you want personalized collection/document recommendations.

Lower-priority or no-go right now:

- **Microsoft.ML.OnnxTransformer**: useful in ML.NET pipelines, but likely redundant with current direct ONNX Runtime integration.
- **Microsoft.ML.ImageAnalytics**: overlaps with existing image pipeline architecture.
- **Microsoft.ML.TensorFlow**: comparatively weak fit versus existing ONNX + TorchSharp direction.
- **Microsoft.ML.OnnxRuntimeGenAI**: promising but still fast-moving/less mature; treat as experimental spike only.

## What You Already Use (Repo Inventory)

`Microsoft.ML*` references currently found in project files:

- `Microsoft.ML` `5.0.0` in `src/Mostlylucid.GraphRag/Mostlylucid.GraphRag.csproj`
- `Microsoft.ML.TorchSharp` `0.23.0` in `src/Mostlylucid.GraphRag/Mostlylucid.GraphRag.csproj`
- `Microsoft.ML.Vision` `5.0.0` in `SignSummarizer.Core/SignSummarizer.Core.csproj`
- `Microsoft.ML.OnnxRuntime` `1.23.2` in multiple projects
- `Microsoft.ML.OnnxRuntime.Gpu` `1.23.2` in `src/ImageSummarizer.Core/ImageSummarizer.Core.csproj`
- `Microsoft.ML.OnnxRuntime.DirectML` `1.23.0` in multiple projects
- `Microsoft.ML.OnnxRuntime.Extensions` `0.15.2-dev-*` in `SignSummarizer.Core/SignSummarizer.Core.csproj`
- `Microsoft.ML.Tokenizers` `2.0.0` in `src/DoomSummarizer.Core/DoomSummarizer.Core.csproj` and `src/Mostlylucid.DocSummarizer.Core/Mostlylucid.DocSummarizer.Core.csproj`

Implication: you are already “deep ONNX + selective ML.NET,” not starting from scratch.

## External Package Landscape (as of 2026-02-08)

- `Microsoft.ML` has a `5.0.0` release and explicitly points to related packages like `TimeSeries`, `AutoML`, `Recommender`, `OnnxTransformer`, and `Tokenizers`.
- `Microsoft.ML.AutoML` has `0.23.0` available.
- `Microsoft.ML.TimeSeries` has `5.0.0` available.
- `Microsoft.ML.OnnxTransformer` has `5.0.0` available.
- `Microsoft.ML.OnnxRuntime` has `1.24.1` available; repo currently uses `1.23.2`.
- `Microsoft.ML.OnnxRuntime.Gpu` has `1.24.1` available; repo currently uses `1.23.2`.
- `Microsoft.ML.OnnxRuntime.DirectML` latest listed is `1.23.0`, which matches current usage.
- `Microsoft.ML.OnnxRuntime.Extensions` stable is `0.14.0`; repo currently pins a dev prerelease build (`0.15.2-dev-*`).

## Recommended Additions

## 1) Microsoft.ML.AutoML (Adopt)

Why it fits:

- You already do multi-signal retrieval fusion and scoring.
- AutoML can optimize a learned reranker/classifier over your retrieval telemetry and labels.
- Practical use: train a “relevance/answerability quality model” from query/result traces.

Potential capabilities:

- Learn feature weights from data rather than hand-tuning all ranking coefficients.
- Auto-select model family + hyperparameters for ranking proxy tasks.
- Build offline evaluation loops for “which retrieval config performs best.”

Concrete first use case in *lucid*RAG:

- Predict `is_good_result` from retrieval features:
  - dense score, bm25 score, salience, freshness, domain match, venue quality, query type, source count
- Use model output as an extra ranking signal or a post-filter before synthesis.

Risks:

- Needs labeled data (implicit feedback or curated eval set).
- Must avoid training-serving skew (feature computation must be identical online and offline).

Recommendation: **Yes, pilot now**.

## 2) Microsoft.ML.TimeSeries (Adopt)

Why it fits:

- You have time-varying operational signals where drift/anomaly detection is valuable.
- Official ML.NET positioning includes anomaly/time-series capabilities.

Potential capabilities:

- Detect sudden drops in retrieval quality (e.g., avg top-k score, clickthrough, citation success).
- Detect ingestion pipeline regressions (OCR quality, parse failure spikes, vectorization failures).
- Trigger automated alerts or temporary routing changes.

Concrete first use case in *lucid*RAG:

- Daily per-tenant anomaly detection on:
  - `retrieval_success_rate`
  - `no_result_rate`
  - `median_answer_latency`
  - `synthesis_cache_hit_rate`

Risks:

- Requires clean time-series aggregation pipeline.
- False positives if seasonality not handled.

Recommendation: **Yes, pilot now**.

## 3) Microsoft.ML.Recommender (Conditional)

Why it may fit:

- If product goals include personalized “what to read/search next,” recommender models are relevant.
- ML.NET provides matrix-factorization-based recommendation tutorials and APIs.

Potential capabilities:

- Per-user suggested collections/documents.
- “Related documents” module based on engagement history.
- Suggested follow-up queries.

Risks:

- Requires meaningful user-event data (views/clicks/ratings).
- Privacy + tenant isolation constraints need careful design.

Recommendation: **Pilot only if personalization is on roadmap in next 1-2 quarters**.

## Packages to Skip (for now)

## Microsoft.ML.OnnxTransformer (Skip for now)

Rationale:

- Good package, but best when you are standardizing on ML.NET `IDataView` pipelines for ONNX scoring.
- Current architecture already has robust direct ONNX Runtime services and model orchestration.
- Introducing this now likely duplicates paths and increases maintenance complexity.

## Microsoft.ML.ImageAnalytics (Skip for now)

Rationale:

- Existing image stack appears custom and richer than generic ML.NET image transforms.
- Low incremental value compared with your current dedicated image pipelines.

## Microsoft.ML.TensorFlow (Skip for now)

Rationale:

- Existing direction is ONNX Runtime + TorchSharp.
- TensorFlow integration adds extra dependency surface with unclear near-term benefit.

## Microsoft.ML.OnnxRuntimeGenAI (Experimental only)

Rationale:

- Interesting for future local GenAI runtime integration.
- Ecosystem/package line is evolving quickly; only do a contained spike, not production commitment yet.

## Version/Dependency Hygiene Recommendations

1. Evaluate bumping `Microsoft.ML.OnnxRuntime` from `1.23.2` to `1.24.1` in a compatibility branch.
2. Evaluate bumping `Microsoft.ML.OnnxRuntime.Gpu` from `1.23.2` to `1.24.1` where used.
3. Keep `Microsoft.ML.OnnxRuntime.DirectML` at `1.23.0` unless a newer compatible release is available.
4. Reassess `Microsoft.ML.OnnxRuntime.Extensions` prerelease pin (`0.15.2-dev-*`):
   - if required feature exists only in prerelease, keep and isolate usage;
   - otherwise prefer stable `0.14.0` for production hardening.

## Proposed 30-Day Plan

Week 1:

- Build offline dataset for retrieval quality modeling.
- Define stable feature schema and labels.

Week 2:

- AutoML experiment for relevance/answerability classifier.
- Baseline against current handcrafted fusion.

Week 3:

- TimeSeries anomaly detector over production telemetry aggregates.
- Wire alert thresholds and dashboards.

Week 4:

- A/B shadow evaluation in staging.
- Decide go/no-go for production rollout.

## Success Criteria

- +5-10% improvement in retrieval quality metric (your chosen offline score or judged relevance).
- Fewer no-result or low-value synthesis calls.
- Meaningful anomaly alerts with acceptable false-positive rate.
- No regression in P95 latency or cost budget.

## Sources

- Microsoft.ML (NuGet, features + related packages): https://www.nuget.org/packages/Microsoft.ML/5.0.0
- Microsoft ML.NET extra dependency guidance (`TimeSeries`/`AutoML` + MKL/libomp): https://learn.microsoft.com/en-us/dotnet/machine-learning/how-to-guides/install-extra-dependencies
- Microsoft.ML.AutoML package (`0.23.0`): https://www.nuget.org/packages/Microsoft.ML.AutoML/0.23.0
- Microsoft.ML.TimeSeries package (`5.0.0`): https://www.nuget.org/packages/Microsoft.ML.TimeSeries/
- Microsoft.ML.OnnxTransformer package (`5.0.0`): https://www.nuget.org/packages/Microsoft.ML.OnnxTransformer/
- Microsoft.ML.Vision package (`5.0.0`): https://www.nuget.org/packages/Microsoft.ML.Vision/5.0.0
- Microsoft.ML.Tokenizers package: https://www.nuget.org/packages/Microsoft.ML.Tokenizers
- Microsoft.ML.OnnxRuntime package (`1.24.1`, version history): https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime/
- Microsoft.ML.OnnxRuntime.DirectML package (`1.23.0`): https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.DirectML
- Microsoft.ML.OnnxRuntime.Extensions package (`0.14.0` + prerelease availability): https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.Extensions
- ML.NET recommendation tutorial (matrix factorization): https://learn.microsoft.com/en-us/dotnet/machine-learning/tutorials/movie-recommendation
- ML.NET anomaly detection tutorial: https://learn.microsoft.com/en-us/dotnet/machine-learning/tutorials/sales-anomaly-detection
