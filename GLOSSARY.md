# FieldTrack

Real-time monitoring of field equipment and vehicles: devices report position and sensor values, and the platform stores them, raises alerts, and shows them live on a map.

## Language

### Equipment

**Asset**:
A piece of field equipment being tracked, such as a truck, excavator, generator or sensor.
_Avoid_: Vehicle, equipment, unit, thing

**Device**:
The transmitter that reports Readings, identified by its serial number and attached to at most one Asset at a time.
_Avoid_: Tracker, sensor, transmitter, gateway

**Attachment**:
The period during which a Device was fitted to an Asset, from when it was attached until it was detached.
_Avoid_: Assignment, pairing, binding

**Asset Group**:
A named set of Assets, such as a site or fleet; an Asset belongs to at most one.
_Avoid_: Fleet, site, tag, category

**Asset Status**:
The live condition of an Asset worked out from its Device and open Alerts, never set by hand: No device, Alerting, Offline or Normal, in that order of precedence.
_Avoid_: State, health, lifecycle

**Archived**:
Said of an Asset or Device removed from use but kept with its history; it no longer appears on the map or in lists.
_Avoid_: Deleted, retired, inactive

**Geofence**:
A named area on the map; it affects Assets only through an Alert Rule that names it.
_Avoid_: Zone, area, boundary, fence

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
