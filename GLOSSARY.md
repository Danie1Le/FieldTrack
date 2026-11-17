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
The live condition of an Asset worked out from its Device and Active Alerts, never set by hand: No device, Alerting, Offline or Normal, in that order of precedence.
_Avoid_: State, health, lifecycle

**Archived**:
Said of an Asset or Device removed from use but kept with its history; it no longer appears on the map or in lists.
_Avoid_: Deleted, retired, inactive

**Geofence**:
A named area on the map; it affects Assets only through an Alert Rule that names it.
_Avoid_: Zone, area, boundary, fence

### People

**Admin**:
A user who manages Assets, Devices, Geofences, Alert Rules and users.
_Avoid_: Administrator, superuser, manager

**Operator**:
A user who watches the map and acknowledges and resolves Alerts but changes nothing else.
_Avoid_: Viewer, dispatcher, user

**Demo Operator**:
The shared Operator account offered to visitors of the cloud demo, created and reset on every deploy while demo mode is on. Exempt from lockout; its password cannot be changed (ADR 0002).
_Avoid_: Guest, demo user, test account

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

### Alerts

**Alert Rule**:
A condition an Admin sets up for one Asset or every Asset in an Asset Group: leaving or entering a Geofence, a sensor value past a threshold, or no Readings for a set time.
_Avoid_: Trigger, condition, policy

**Alert**:
A record that an Alert Rule's condition became true for one Asset; an Asset has at most one Active Alert per Alert Rule.
_Avoid_: Alarm, incident, notification, event

**Active**:
Said of an Alert that is Open or Acknowledged, that is, not yet Resolved.
_Avoid_: Live, current, unresolved

**Acknowledge**:
To mark an Open Alert as seen by a user without ending it.
_Avoid_: Ack, accept, claim

**Resolve**:
To end an Alert, done by the platform when its condition clears or by a user at any time; a Resolved Alert never reopens.
_Avoid_: Close, dismiss, clear

**Latch**:
What holds an Alert Rule quiet after a user Resolves its Alert while the condition still holds: the rule raises nothing new for that Asset until the condition clears and breaches again.
_Avoid_: Snooze, mute, suppression

**Rule state**:
What an Alert Rule remembers about one Asset between Readings: whether its condition holds and which Reading it evaluated last. It starts afresh when the rule's condition is edited or the Asset's Device is detached.
_Avoid_: Rule status, alert state

**Hysteresis**:
The margin a sensor value must move back past a threshold before a threshold Alert resolves itself, so a value hovering at the threshold does not raise Alert after Alert.
_Avoid_: Deadband, tolerance, buffer

### Simulation

**Scenario**:
A script for a simulator run that says which Devices suffer which Faults, Power offs, Coverage gaps or clock problems, and when, so a run can be repeated exactly.
_Avoid_: Script, test plan, playbook

**Fault**:
A simulated equipment problem that changes a Device's sensor values over time, such as overheating or a fuel leak.
_Avoid_: Failure, error, anomaly, incident

**Power off**:
A simulated period when a Device sends nothing and keeps nothing; it starts a new Boot when it comes back.
_Avoid_: Outage, shutdown, dropout

**Coverage gap**:
A simulated period when a Device keeps taking Readings but cannot send them, then sends them late in the same Boot when it reconnects.
_Avoid_: Dropout, signal loss, buffering

**Injection**:
One Fault, Power off, Coverage gap or clock problem that a simulator run applies to one Device for a window of time, scripted by a Scenario or drawn at random. The Manifest lists every one with its start and end.
_Avoid_: Event, incident, chaos

**Manifest**:
The simulator's record of what one run actually published and injected, used as ground truth to score lost Readings, offline detection and false Alerts.
_Avoid_: Log, report, expected results
