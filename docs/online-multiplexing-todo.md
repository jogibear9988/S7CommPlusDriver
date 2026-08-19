# S7CommPlus online multiplexing notes

Status: implemented in the driver refactor. One `S7CommPlusClient` owns exactly one PLC connection. The library must not silently open additional PLC connections. If an application wants more physical PLC connections, it creates more clients explicitly.

- TIA opened two ES connections to the PLC, not one connection per watched block.
- Online block view traffic used one long-lived watch connection.
- Additional opened or expanded blocks created additional TIS jobs/subscriptions on that same watch connection.
- Responses and notifications were interleaved; notifications are routed by subscription object id.

Implemented driver changes:

- Added a receive dispatcher per protocol session, but arbitrary PLC requests are not run in parallel.
  - Thomas' 2025 note is correct for fragmented response bodies: once a large S7CommPlus response is fragmented, the individual fragments do not carry the request sequence number. Sending multiple fragment-producing requests concurrently can make fragment ownership ambiguous.
  - One foreground request stays in flight per physical S7CommPlus connection.
  - Responses are matched by function code and sequence number.
  - Notifications are routed by `Notification.SubscriptionObjectId`.
  - Alarm part-2 notifications are also routed by `Notification.P2SubscriptionObjectId` when present.
  - Consumed notifications are not left in the dispatcher queue for a second delivery.
- Public API rule:
  - One `S7CommPlusClient` means one PLC connection.
  - Multiple physical connections are created only when the application creates multiple clients.
- Replaced singleton subscription state with per-subscription handles.
  - Tags: subscription object id, reference-id-to-tag map, next credit limit.
  - Alarms: subscription object id, next credit limit.
  - TIS watch: job object id, subscription object id, subscription-ref object id, result model, poll counter.
- Multiple subscriptions can coexist on one client.
  - Create/delete/read/metadata operations stay serialized as normal session requests.
  - Notification loops wait for their own subscription object id.
  - Reads/metadata may run while subscriptions are active, but not in parallel with another foreground request on the same physical connection.
  - If an application needs true parallel large metadata/block transfers, it should create a second `S7CommPlusClient` explicitly.
- Explicit helper APIs stay explicit.
  - Snapshot helpers may accept another `S7CommPlusClient`, but should not create one implicitly.
- For online engineering features prefer `S7CommPlusDefaults.RemoteTsapEs`.
