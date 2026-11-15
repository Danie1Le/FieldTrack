# FieldTrack

Real-time monitoring of field equipment and vehicles: devices report position and sensor values, and the platform stores them, raises alerts, and shows them live on a map.

## Language

### Telemetry

**Reading**:
One report from a Device at one instant: its position plus whatever sensor values it carries.
_Avoid_: Message, event, data point, sample

**Boot**:
One power-on period of a Device, numbered by a counter that rises each time the Device starts.
_Avoid_: Session, epoch, restart

**Sequence number**:
The position of a Reading within its Boot, starting again at 0 each Boot. A Reading is identified by its Device, Boot and Sequence number.
_Avoid_: Message id, counter

**Device time**:
When the Device says the Reading was taken, by its own clock.
_Avoid_: Event time, timestamp

**Received time**:
When the platform received the Reading.
_Avoid_: Ingest time, server time, arrival time

**Rejected message**:
A message on a telemetry topic that the platform refused to store as a Reading, kept with the reason it was refused.
_Avoid_: Dead letter, invalid reading, bad message
