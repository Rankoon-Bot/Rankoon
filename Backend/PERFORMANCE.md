# Performance Instrumentation

## Metrics and traces

`Rankoon.Data.Performance.RankoonPerformanceMetrics` exposes the `Rankoon.Performance` `ActivitySource` and `Meter` (version `1.0.0`). They use only `System.Diagnostics`, so any OpenTelemetry SDK can subscribe without a Rankoon exporter package.

| Signal | Type | Unit | Tags |
| --- | --- | --- | --- |
| `rankoon.operation.duration` | histogram | `ms` | `rankoon.operation`, `rankoon.component`, `outcome` |
| `rankoon.operation.count` | counter | `{operation}` | `rankoon.operation`, `rankoon.component`, `outcome` |
| `rankoon.database.operation.count` | counter | `{operation}` | `rankoon.operation`, `rankoon.component`, `outcome` |

Activities have the operation name, `rankoon.component`, and `rankoon.database.operation.count`. No guild, user, channel, session, or display-name identifier is emitted, avoiding high-cardinality and personal-data telemetry.

Current integration hooks are `VoiceActivityAccumulator.AccrueAsync` (`xp.voice.accrual`) and `VoiceActivityProjectionService.ProjectPendingAsync` (`xp.voice.projection.pending`). Accrual database-operation counts include every attempted Mongo read and write, including CAS retries. Projection-pending counts only its two scheduling reads; projection work remains separately observable through its existing logs and error records.

## Synthetic Baseline

`VoiceAccrualSyntheticHarness.RunCoalescingWorkload` executes the normal `VoiceActivityAccumulator.Plan` path without MongoDB, Discord, a network, or wall-clock assertions. The baseline workload is 20,000 consecutive five-second intervals for one session with identical attributes. It represents the expected watchdog steady state, where each interval coalesces into the prior segment.

The regression contract is deliberately operation-based rather than duration-based:

| Baseline result | Expected value |
| --- | ---: |
| Planned slices | 20,000 |
| Planning operations | 20,000 |
| Inputs submitted to segment merge | 39,999 |
| Maximum/final segments | 1 / 1 |

The merge-input count is `2 * slices - 1`: the first plan merges one input, and every later plan merges the existing coalesced segment plus one new segment. This makes the workload linear and catches loss of coalescing or accidental repeated planning. It is not a latency SLA: elapsed time is intentionally reported by the harness but not asserted because CI hosts vary.

Run the contract with:

```powershell
dotnet test Backend/Backend.Tests/Backend.Tests.csproj --filter FullyQualifiedName~PerformanceTelemetryTests
```

For a production OpenTelemetry SDK, add `Rankoon.Performance` to both tracing and metrics subscription lists. Export configuration, sampling, and retention intentionally remain deployment concerns.
