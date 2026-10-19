# MongoDB scan classification

| Workload | Classification | Bound |
| --- | --- | --- |
| Voice ledger copy and deletion | Cursor migration | `_id` cursor and a fixed source high-water mark |
| Voice projection repair | Due-work queue | `ProjectionBatchSize`; the predicate is time and lease based, so a durable cursor would skip newly due work |
| OAuth token migration | Predicate-draining migration | Fixed batch; each successful update removes the document from the legacy predicate |
| Startup compatibility repairs | Predicate-draining repair | `MongoStartupMaintenance:RepairBatchSize`; each repair removes the document from its predicate |
| Legacy manual-adjustment migration | Predicate-draining migration | `MongoStartupMaintenance:RepairBatchSize`; deterministic grant keys make retries safe |
| Voice migration parity verification | Full collection verification | Required before authority cutover and deletion approval; it remains the only unbounded migration read |

Full collection verification is intentionally not performed on request paths. It may be expensive on a large historical ledger, but it is required to prove global, member, and season parity before the compressed voice history becomes authoritative.
