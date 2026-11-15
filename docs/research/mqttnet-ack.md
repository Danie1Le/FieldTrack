# MQTTnet: acknowledge after durable write at QoS 1

Research for issue #3. Researched 2026-11-14 against MQTTnet v5.2.0 (released 2026-07-01), Mosquitto v2.1.2 / v2.0.22, the OASIS MQTT 3.1.1 and 5.0 specs, and current HiveMQ docs.

Labels used below:

- **[Documented]** stated in a spec, official doc, or read directly in source code.
- **[Inferred]** my conclusion from documented facts. Not tested yet.

## Short answer

Yes. MQTTnet can hold back the QoS 1 PUBACK until a batch is committed to Postgres. Set `AutoAcknowledge = false` in the received handler and call `AcknowledgeAsync` after the commit. A crash before the ack leaves the messages unacknowledged in the broker session. The broker resends them when the worker reconnects, but only when all of the following hold:

1. Fixed `ClientId`, `CleanSession`/`CleanStart = false`.
2. On MQTT 5 (the MQTTnet default), `SessionExpiryInterval > 0`. The MQTTnet default is 0, and 0 ends the session when the connection closes. **This default silently breaks NFR-3.**
3. The broker's per-client queue never overflows. Mosquitto's default `max_queued_messages` is 1000. Above that, messages are dropped silently. At 2,000 msg/s that is about half a second of worker downtime or DB stall.
4. PUBACKs are sent in the order the PUBLISHes arrived. The spec requires this. MQTTnet does not enforce it.

Throughput (NFR-1): no published benchmark measures an MQTTnet subscriber at 2,000 msg/s. The design ceiling is about `in-flight window / batch commit latency`. Mosquitto's default window is 20, which is too small for batching. MQTT 5 `ReceiveMaximum` raises it per client. With a window of about 1,000, 2,000 msg/s looks reachable [Inferred]. A spike must measure it.

HiveMQ Cloud: **the Serverless (free) plan reaches end of life on 2026-12-31.** New Serverless clusters can no longer be created. The free self-managed replacements are capped at 25 to 100 msg/s. The spec's demo-broker choice needs revisiting.

## 1. Manual acknowledgement in MQTTnet

**[Documented, source v5.2.0]**

- `MqttApplicationMessageReceivedEventArgs` exposes `public bool AutoAcknowledge { get; set; } = true;` and `public Task AcknowledgeAsync(CancellationToken)`. A second call throws `InvalidOperationException("The application message is already acknowledged.")`.
  https://github.com/dotnet/MQTTnet/blob/v5.2.0/Source/MQTTnet/Receiving/MqttApplicationMessageReceivedEventArgs.cs
- The client dequeues received PUBLISH packets on one background task. It awaits the `ApplicationMessageReceivedAsync` handler for each message in turn, then acks only `if (eventArgs.AutoAcknowledge)`.
  https://github.com/dotnet/MQTTnet/blob/v5.2.0/Source/MQTTnet/MqttClient.cs#L677-L708
- For QoS 1, `AcknowledgeReceivedPublishPacket` sends a PUBACK unless `ProcessingFailed` is true. With `ProcessingFailed = true` it sends nothing, and the message stays unacked.
  https://github.com/dotnet/MQTTnet/blob/v5.2.0/Source/MQTTnet/MqttClient.cs#L379-L414
- The official sample `ConcurrentProcessingDisableAutoAcknowledge` sets `ea.AutoAcknowledge = false`, does the work on another task, then calls `ea.AcknowledgeAsync`. Its comment says this gives "a proper QoS1 (at-least-once) experience". It also warns that MQTT has no NACK: a failed message is redelivered only on reconnect.
  https://github.com/dotnet/MQTTnet/blob/v5.2.0/Samples/Client/Client_Subscribe_Samples.cs#L146-L172
- Known rough edge: open issue #1792 reports a `NullReferenceException` in `AcknowledgeReceivedPublishPacket` when the client disconnects between receive and ack.
  https://github.com/dotnet/MQTTnet/issues/1792

**[Inferred] Batch pattern that fits the source:**

1. In the handler, set `AutoAcknowledge = false`. Write the event args to a bounded `Channel<T>` and return at once. Do not await the DB in the handler. The handler is serial, so awaiting the commit there caps throughput at one message per commit.
2. A consumer reads from the channel and builds a batch. It flushes on size or on a timer. It upserts the batch on `(device_id, sequence_no)` and commits.
3. After the commit, the consumer calls `AcknowledgeAsync` on each message in channel order. The handler is serial, so channel order equals receive order. That satisfies the PUBACK ordering rule (section 2).
4. If the commit fails, do not ack. Disconnect and reconnect to force redelivery. MQTT has no NACK.
5. If an ack fails because the connection dropped, ignore it. The broker redelivers, and the idempotent upsert absorbs the duplicate.

Do not use a MQTT 5 PUBACK reason code of 0x80 or greater for transient failures. The broker then treats the message as acknowledged and discards it [MQTT-4.4.0-2].

## 2. What the MQTT specs require

**[Documented]** MQTT 5.0: https://docs.oasis-open.org/mqtt/mqtt/v5.0/os/mqtt-v5.0-os.html. MQTT 3.1.1: https://docs.oasis-open.org/mqtt/mqtt/v3.1.1/os/mqtt-v3.1.1-os.html

- **When to PUBACK.** The QoS 1 receiver sends PUBACK "having accepted ownership of the Application Message" (5.0 [MQTT-4.3.2-4], 3.1.1 [MQTT-4.3.2-2]). The specs set no deadline, so delaying the ack until after the commit is allowed.
- **Order.** "The Client MUST send PUBACK packets in the order in which the corresponding PUBLISH packets were received" (5.0 and 3.1.1, [MQTT-4.6.0-2]).
- **Redelivery happens only on reconnect.** On reconnect with Clean Start / CleanSession = 0 and a session present, the server MUST resend unacknowledged QoS > 0 PUBLISH packets with their original packet identifiers. "This is the only circumstance" where resending is required. In MQTT 5, servers "MUST NOT resend messages at any other time" (5.0 [MQTT-4.4.0-1], 3.1.1 [MQTT-4.4.0-1]). So a stuck but still-connected worker never gets a retry. Only a reconnect does.
- **Session state.** The server stores QoS 1 messages that were sent but not fully acknowledged, and QoS 1 messages pending transmission (5.0 section 4.1, 3.1.1 section 3.1.2.4).
- **MQTT 3.1.1.** CleanSession = 0 keeps the session after disconnect. The server MUST queue further QoS 1/2 messages for it [MQTT-3.1.2-4], [MQTT-3.1.2-5].
- **MQTT 5.** Session lifetime is set by the Session Expiry Interval: "If it is set to 0, or is absent, the Session ends when the Network Connection is closed" (section 3.1.2.11.2). Clean Start = 0 alone is not enough.
- **Receive Maximum (MQTT 5 only).** The client sends it in CONNECT. The server MUST NOT have more than that many unacknowledged QoS 1/2 PUBLISHes outstanding to the client. The default when absent is 65,535 (sections 3.1.2.11.3, 4.9, [MQTT-3.3.4-9]). MQTT 3.1.1 has no such property, so the in-flight limit is purely broker config.
- **Shared subscriptions (MQTT 5, section 4.8.2).** Suppose the connection to the chosen QoS 1 subscriber breaks before it acks. The server "MAY wait for the Client to reconnect". If that client's session terminates first, the server "SHOULD send the Application Message to another Client" in the group. The server "MAY" also redirect as soon as the connection is lost. Redistribution is therefore broker-specific.

## 3. MQTTnet session and in-flight settings

**[Documented, source v5.2.0]** https://github.com/dotnet/MQTTnet/blob/v5.2.0/Source/MQTTnet/Options/MqttClientOptions.cs

| Option | Default | Builder | Note |
|---|---|---|---|
| `ProtocolVersion` | `V500` | `WithProtocolVersion` | v5 is the default |
| `CleanSession` | `true` | `WithCleanSession(false)` / `WithCleanStart(false)` | both set the same field |
| `SessionExpiryInterval` | `0` | `WithSessionExpiryInterval(uint)` | 0 means the session dies on disconnect (MQTT 5) |
| `ReceiveMaximum` | `0` | `WithReceiveMaximum(ushort)` | the writer omits the property when 0, so the broker default applies |

The writer skips Receive Maximum when it is 0: https://github.com/dotnet/MQTTnet/blob/v5.2.0/Source/MQTTnet/Formatter/V5/MqttV5PropertiesWriter.cs#L102-L111

**[Inferred] Settings for NFR-3:** a fixed ClientId such as `fieldtrack-ingest`, `WithCleanStart(false)`, and `WithSessionExpiryInterval` of at least the worst expected outage (for example 3600 s). Set `WithReceiveMaximum` to the batch window (for example 1000). Re-subscribe after every connect. A subscribe on an existing session is harmless, and it covers the case where the session expired.

## 4. Mosquitto

**[Documented]** Config reference: https://mosquitto.org/man/mosquitto-conf-5.html

- `max_inflight_messages`: "The maximum number of outgoing QoS 1 or 2 messages that can be in the process of being transmitted simultaneously". Default 20. 0 means no maximum.
- `max_queued_messages`: QoS 1/2 messages queued per client above those in flight. Default 1000. 0 means no maximum ("not recommended").
- `max_queued_bytes`: "Once this limit has been reached, subsequent messages will be silently dropped." Default 0, no maximum.
- `persistence`: default false, so sessions live in memory only. When true, data is written to `mosquitto.db` on exit and every `autosave_interval` (default 1800 s).

**[Documented, source]** The MQTT 5 Receive Maximum overrides `max_inflight_messages` per client. `context.c` initialises `msgs_out.inflight_maximum = db.config->max_inflight_messages`. `property_broker.c` then replaces it with the client's `MQTT_PROP_RECEIVE_MAXIMUM` when present. The same logic is in v2.0.22 and v2.1.2.

- https://github.com/eclipse-mosquitto/mosquitto/blob/v2.1.2/src/context.c#L116-L119
- https://github.com/eclipse-mosquitto/mosquitto/blob/v2.1.2/src/property_broker.c#L44-L51
- https://github.com/eclipse-mosquitto/mosquitto/blob/v2.0.22/src/property_broker.c#L39-L46

**[Documented, source]** When the per-client queue is full, QoS 1 messages are dropped. The broker logs "Outgoing messages are being dropped for client %s.": https://github.com/eclipse-mosquitto/mosquitto/blob/v2.1.2/src/database.c#L488-L496

**[Documented, source]** Shared subscriptions (`$share/group/topic`) pick the group member by strict round robin. There is no check whether the member is connected. The message goes into that member's session queue: https://github.com/eclipse-mosquitto/mosquitto/blob/v2.1.2/src/subs.c#L111-L130

**[Inferred] Consequences:**

- **Data loss with defaults.** A worker restart or DB stall longer than about (20 + 1000) / 2000 s, roughly 0.5 s, loses readings silently. For NFR-3, set `max_queued_messages` high or to 0 and cap with `max_queued_bytes`/`memory_limit` instead. Watch the broker log for the drop message in the chaos test.
- **Batch deadlock.** If the worker waits for N messages before committing and N exceeds the in-flight window, the broker never sends message N+1. The batch must also flush on a timer, and the window (`ReceiveMaximum`) should be at least the batch size.
- **Shared subscriptions on Mosquitto.** They do not redistribute a crashed member's backlog. That backlog waits for the member to reconnect, or is lost when its session expires. NFR-1 asks for one container, so use a single non-shared subscriber.
- **Broker restart.** NFR-3 covers ingest-worker restarts, which in-memory sessions survive. A broker restart with `persistence false` loses sessions and queued messages. With persistence on, it loses up to `autosave_interval` worth.

## 5. HiveMQ Cloud

**[Documented]** Serverless end of life: https://docs.hivemq.com/hivemq-cloud/serverless-end-of-life.html

- "HiveMQ retires the HiveMQ Cloud Serverless plan on 2026-12-31." This covers Free and Pay As You Go. New Serverless clusters cannot be created. After that date clients cannot connect, and clusters and their data are deleted.
- Free replacements are self-managed brokers you run yourself:
  - Migration license: 100 connections and 100 msg/s for 12 months, only for accounts that owned a Serverless cluster.
  - Lab license: 25 connections and 25 msg/s.
- HiveMQ Cloud Starter is a paid managed alternative. The page gives no limits for it.

**[Documented]** HiveMQ broker shared subscriptions: https://docs.hivemq.com/hivemq/latest/user-guide/shared-subscriptions.html

- Supported for MQTT 3 and MQTT 5 clients.
- Distribution is random, not round robin. Faster consumers receive more.
- When a member leaves, HiveMQ queues its undelivered shared messages and "redistributes them to the remaining members".
- The queue limit is 500,000 messages per shared subscription. QoS 2 is downgraded to QoS 1.

**[Documented]** HiveMQ broker defaults (self-managed config reference): `max-queued-messages` 1000 per client, session-expiry max-interval 4,294,967,295, server-receive-maximum 10 (inbound from client). https://docs.hivemq.com/hivemq/latest/user-guide/configuration.html

**[Inferred]**

- Whether HiveMQ Cloud Starter exposes or changes these limits is not documented on the pages read.
- A 1000-message per-client queue has the same overflow risk as on Mosquitto.
- The free hosted path the spec assumes is gone after 2026-12-31. The free self-managed licenses (25 to 100 msg/s) cannot carry 2,000 msg/s. The spec runs NFR-1 benchmarks locally on Mosquitto, so the benchmark is unaffected. The hosted demo needs another broker, for example Mosquitto in a container, Starter, or another vendor.

## 6. Throughput evidence for one subscriber at 2,000 msg/s (NFR-1)

**[Documented]**

- No published benchmark measures MQTTnet subscriber throughput at QoS 1. MQTTnet ships BenchmarkDotNet micro-benchmarks (for example `MessageDeliveryBenchmark`, `MessageProcessingBenchmark`) but publishes no results: https://github.com/dotnet/MQTTnet/tree/v5.2.0/Source/MQTTnet.Benchmarks
- Broker side: EMQX's open benchmark ran Mosquitto 2.0.15 on a c5.4xlarge, QoS 1, 16-byte payload. Point-to-point stabilised at 37,300 msg/s pub and sub. Fan-in with shared subscriptions reached about 40,000 msg/s consumed. https://www.emqx.com/en/blog/open-mqtt-benchmarking-comparison-mosquitto-vs-nanomq (vendor of a competing broker)

**[Inferred]**

- Mosquitto is roughly 20x above the 2,000 msg/s target, so the broker is not the bottleneck.
- With deferred acks, subscriber throughput is bounded by `window / (batch fill time + commit latency + ack time)`. Two examples:
  - Default window 20, 10 ms commit: about 2,000 msg/s, with no headroom.
  - Window 1,000, 50 ms commit: about 20,000 msg/s.
- A serial handler that awaits each commit gets about 1 / commit latency, so hundreds of msg/s at best. The channel hand-off in section 1 is mandatory.
- Verify all of this with a spike: k6 or the simulator at 2,000 msg/s into local Mosquitto, one MQTTnet subscriber, batch upsert into PostGIS. Run it while killing the worker.

## Open items for follow-up

- Spike to measure throughput (NFR-1) and zero loss under kill -9 (NFR-3) with the settings above.
- Pick a replacement for the hosted demo broker before 2026-12-31.
- Decide the reconnect strategy. MQTTnet v5.2.0 ships no ManagedClient extension (https://github.com/dotnet/MQTTnet/tree/v5.2.0/Source), so the BackgroundService needs its own reconnect loop [Documented that the package is absent; the loop is Inferred].
