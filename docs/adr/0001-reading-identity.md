# A Reading is identified by Device, Boot and Sequence number

The spec de-duplicates telemetry on (device_id, sequence_no), but a real device's counter can reset when it powers on, which would make fresh Readings look like duplicates and silently drop them. Each Device therefore sends a Boot counter that rises on every power-on and a Sequence number that restarts at 0 each Boot, and a Reading is unique by (Device, Boot, Sequence number). When a duplicate arrives, the first stored copy wins; identical and conflicting duplicates are counted separately so a device bug shows up as conflicts rather than hiding as redeliveries.

## Considered options

- **(Device, Sequence number) with a counter that never resets**: matches the spec, but depends on every device persisting its counter forever; one lost counter drops new data as duplicates.
- **(Device, Device time)**: needs no counter, but two Readings in the same millisecond collide, and a device clock that jumps backwards produces false duplicates.
