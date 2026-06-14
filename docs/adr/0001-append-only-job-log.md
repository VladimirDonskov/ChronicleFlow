# ADR 0001: Use an append-only job log

## Status

Accepted for the single-node version.

## Context

The first release should demonstrate durable job processing without requiring a
database or container runtime. It must recover the latest job state and make
every transition inspectable.

## Decision

Persist every job snapshot as one JSON line. On startup, replay the file and keep
the latest snapshot for each job ID. Treat jobs left in `Running` as interrupted
and enqueue them again.

Writes and state transitions are serialized by a `SemaphoreSlim`. This also
makes claiming a queued job atomic inside one process.

## Consequences

- The project runs with zero external infrastructure.
- The complete state history is human-readable.
- Startup time and disk usage grow with the number of transitions.
- This implementation supports one process only.
- A distributed version should implement `IJobStore` with transactional leases
  in PostgreSQL rather than sharing the JSONL file.
