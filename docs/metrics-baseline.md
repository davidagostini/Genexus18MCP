# MCP performance baseline and targets

This document defines the measurement contract for `gateway:metrics`. Values below are acceptance targets, not measurements. A benchmark run must record fresh values for the same fixture KB and tool arguments before comparing against them.

## Required dimensions

- Latency: p50 and p95 in milliseconds per tool.
- Payload: p50, p95, and max estimated tokens in/out; the current estimate is JSON bytes divided by four.
- Reliability: success rate, error rate, timeout rate, cancellation rate, and retry count.
- Cache: semantic cache hits, misses, and hit rate by tool.
- Workflow efficiency: number of MCP calls needed to complete a representative workflow.

## Initial targets

| Scenario | p50 | p95 | response estimate | success |
| --- | ---: | ---: | ---: | ---: |
| Cached metadata/read | <= 100 ms | <= 500 ms | <= 2,000 tokens | >= 99% |
| Warm indexed query/list | <= 500 ms | <= 2,000 ms | <= 4,000 tokens | >= 99% |
| Narrow object read | <= 1,000 ms | <= 5,000 ms | <= 6,000 tokens | >= 98% |
| Dry-run mutation | <= 2,000 ms | <= 10,000 ms | <= 5,000 tokens | >= 98% |
| Confirmed mutation | fixture-dependent | fixture-dependent | <= 8,000 tokens | >= 97% |
| Long-running lifecycle operation | accepted <= 1,000 ms | accepted <= 2,000 ms | <= 2,000 tokens | 100% accepted or explicit error |

## Workflow targets

- Object explanation: discovery/bootstrap + at most 3 focused reads.
- Safe mutation: dry-run + apply + verification, normally 3 calls.
- Timeout recovery: status/result + read-back, with no blind mutation retry.
- Completion: first response within 500 ms on a warm index and at most 25 candidates.

## Measurement rules

1. Run each scenario at least 30 times after one warm-up pass.
2. Report fixture KB identity, SDK version, Gateway/Worker commit, tool arguments, cache state, and whether the run is live-KB or hermetic.
3. Do not compare live-KB and hermetic timings in the same percentile.
4. Treat any timeout, unknown commit state, stale-cache read, or protocol disconnect as a failed scenario even if a later retry succeeds.
5. Re-run regressions with the narrowest test first, then the full repository gates.

The metrics surface must expose enough data to calculate these targets without parsing log files.
