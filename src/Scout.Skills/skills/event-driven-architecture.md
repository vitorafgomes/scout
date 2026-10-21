---
name: event-driven-architecture
description: Answering event-driven architecture questions about the user's projects — events vs commands, publish/subscribe, message brokers (Kafka, RabbitMQ, SQS/SNS, Service Bus), choreography vs orchestration, idempotency, message ordering, exactly-once vs at-least-once delivery, dead-letter queues, event schema evolution, and eventual consistency. Not general system-design or boundaries, not sync HTTP/REST API design, not a specific language's syntax, not debugging a single error.
---

# Event-Driven Architecture

Use this when a user asks how to design, assess, or fix an event-driven or messaging-based system: whether to model something as an event or a command, how to use a broker, how to keep consumers correct under retries and reordering, how schemas evolve, or how to reason about eventual consistency.

You ADVISE only. You read code and explain; you never edit files, commit, run commands, or mutate a broker, queue, or cloud resource. Showing a short illustrative event payload, topic config, or consumer snippet in your reply is fine — it describes a recommendation, it does not apply it. For broker CLIs or IaC (Terraform, CloudFormation, Bicep, k8s manifests), describe or recommend the command/manifest; never execute it.

**Ground advice in their real code.** Use `search_project` for producers, consumers, topic/queue/exchange names, serializers, and broker config. Use `find_symbol` to trace who publishes and who handles a given event type — that reveals the true flow. Use `get_repository` for stack and broker context. If you can't find the relevant code, say so and answer conditionally.

## Events vs commands

- **Event** = a statement of fact, past tense, "something happened" (`OrderPlaced`). The producer does not know or care who reacts. Many subscribers, no expected response.
- **Command** = an instruction, imperative, "do this" (`PlaceOrder`). Directed at one owner who may reject it. Expects an outcome.
- Misnaming a command as an event (or vice versa) leaks coupling. If the publisher dictates what the receiver must do, it is a command, not an event.

## Publish/subscribe and brokers

- Pub/sub decouples producers from consumers in time and identity: producers emit, the broker fans out, consumers subscribe independently.
- Match the broker to the need: **Kafka/Kinesis** for high-throughput, replayable, partition-ordered logs; **RabbitMQ/Service Bus** for flexible routing and per-message work queues; **SQS/SNS** for managed cloud fan-out + queue. Log-based brokers retain and replay; queue brokers typically consume-and-delete.
- One topic/event-type per business fact. Avoid a single "events" topic carrying unrelated payloads — it forces every consumer to filter and couples them.

## Choreography vs orchestration

- **Choreography**: services react to each other's events, no central coordinator. Loose coupling, easy to extend, but the end-to-end flow is implicit and hard to trace.
- **Orchestration**: a coordinator (saga/workflow engine) issues commands and tracks state. Visible, testable flow; the orchestrator becomes a dependency and a coupling point.
- Rule of thumb: choreography for simple fan-out reactions; orchestration when a multi-step business transaction needs explicit state, ordering, and compensation.

## Idempotency (almost always required)

- Most brokers deliver **at-least-once**, so duplicates and redeliveries are normal, not exceptional. Consumers must be idempotent.
- Use a stable event/message ID and a dedup store (processed-IDs table or cache), or make the handler naturally idempotent (upsert, set-absolute-value, conditional update). Never assume "handled exactly once."
- Flag any handler that increments, appends, or sends a side effect (email, payment) without a dedup guard — that double-fires on redelivery.

## Ordering and "exactly-once"

- Global ordering across a topic is usually unavailable. Kafka orders **within a partition**; ordering needs a partition key (e.g. by aggregate/entity ID) so related events land in order on one partition.
- True end-to-end exactly-once is rare and costly. The practical target is **at-least-once delivery + idempotent processing** = effectively-once. Treat vendor "exactly-once" claims as scoped (often broker-internal only).
- If a consumer assumes events arrive in order or never repeat, that is a latent bug — call it out.

## Dead-letter handling

- Route messages that fail after bounded retries to a **dead-letter queue (DLQ)** instead of retrying forever (poison messages block the partition/queue and stall the consumer).
- Retry transient failures with backoff; send non-transient (deserialization, validation, business-rule) failures straight to DLQ. Recommend monitoring/alerting on DLQ depth and a documented redrive path.
- A consumer with infinite retries and no DLQ is a stall waiting to happen.

## Schema evolution

- Producers and consumers deploy independently, so payloads must evolve **backward- and forward-compatibly**: add optional fields, never repurpose or remove a field consumers still read, never change a field's type or meaning in place.
- Prefer a schema registry (Avro/Protobuf/JSON Schema) with compatibility checks. Version the event (`v1`/`v2` type or field) for breaking changes and run old + new in parallel during migration.
- Consumers should ignore unknown fields, not crash on them. Flag tight deserialization that rejects extra fields.

## Eventual consistency

- Crossing an async boundary trades immediate consistency for decoupling: subscribers see the new state slightly later. Confirm the business actually tolerates the staleness window.
- Don't read-your-write across a topic and expect instant visibility. Design UIs/APIs for "pending"/"processing" states.
- Keep one writer (owner) per piece of data; other services react to its events rather than writing the same entity. Multiple writers = corruption.

## Common pitfalls to flag

- Events used as disguised commands (publisher dictating receiver behavior).
- Non-idempotent consumers under at-least-once delivery.
- Assuming global ordering or exactly-once; missing/poor partition key.
- No DLQ, or unbounded retries on poison messages.
- Breaking schema changes with no versioning or registry; consumers that crash on unknown fields.
- One mega-topic for unrelated events; chatty event storms (one action emits a cascade).
- Eventual consistency used where the business needs a strong read.
- Lost events: publishing inside a DB transaction without an outbox, so a crash drops the event (recommend the transactional outbox pattern).
