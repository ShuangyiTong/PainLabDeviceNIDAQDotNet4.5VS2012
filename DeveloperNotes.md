# Developer Notes

## Undefined behaviour / caveats

These are not bugs given the current host protocol convention, but anyone touching `program.cs` should be aware of them.

### 1. Combined `switch_channel` + `normalised_current_level` in `dual` mode

In `dual` mode single-stim, the host must **not** populate `switch_channel` and `normalised_current_level` in the same `StimulationControlFrame`. Doing so produces undefined behaviour from the host's perspective.

What actually happens in code (see `PainlabNIDS5Protocol.ApplyControlData`, around lines 246 and 267-270 of `program.cs`):

- Line 246 calls `controlFrame.ApplyControlData(...)` with `_selected_channel` as it stood **before** this frame. The pulse is written to that (old) AO buffer index.
- After the call returns, lines 267-270 update `_selected_channel` from `controlFrame.switch_channel`.

So a frame like `{ switch_channel: 1, normalised_current_level: 0.5 }` would stimulate AO0 (the previous channel) and only select AO1 for the *next* frame — almost certainly not what the sender intended.

**Convention:** send `{ switch_channel: N }` as a standalone frame before any stimulation frame on that channel. Never bundle them.

This is consistent with how other parameters behave: `pulse_width`, `frequency`, `stimulation_length` *are* applied to the current stim (because they are merged inside `ApplyControlData` before `generatePulses` runs), but `switch_channel` is not. Keep this asymmetry in mind. Such behaviour was designed due to the host control panel app use single sendCommand in visualisation interface to update parameter one by one (if not use scripted way, which we have only done with dual simultaneous stimulation that uses a separate function `ApplyControlDataSimultaneous` requiring all parameters to be populated).

### 2. Persisted-state update is load-bearing on in-place mutation

The block:

```csharp
_pulseLength = controlFrame.stimulation_length;
_pulseWidth  = controlFrame.pulse_width;
_frequency   = controlFrame.frequency;
```

unconditionally copies `controlFrame.*` into the persisted stim state. This is correct **only because** `controlFrame.stimulation_length / pulse_width / frequency` are guaranteed to be non-`-1` by the time control reaches this point:

- In every path that calls `ApplyControlData` (dual single-stim, control-trigger, digital), the `-1` sentinels on those three fields have already been overwritten in-place with the previous persisted values (see lines 73-86 of `StimulationControlFrame.ApplyControlData`).
- In the dual-stim path (`ApplyControlDataSimultaneous`), the sanity checks throw if any of them is `< 0`, so nothing with `-1` reaches here either.

The block is therefore load-bearing on the in-place mutation inside `StimulationControlFrame.ApplyControlData`. If that mutation is ever removed or refactored away (e.g. switching to nullable types — see TODO 1 below), this block silently starts persisting `-1` into the stim state. Update the persistence logic at the same time you change the mutation (or do them together as part of TODO 1 + TODO 2).

## TODOs (refactoring)

In rough order of leverage. Each one removes a class of subtle bugs rather than fixing a single one.

### TODO 1. Replace the `-1` sentinels with nullable types (`int?`, `double?`)

Every "optional" field on `StimulationControlFrame` (`normalised_current_level`, `stimulation_length`, `switch_channel`, `pulse_width`, `frequency`, and their `_2` counterparts) currently uses `-1` to mean "not populated". This is the largest source of accidental complexity in the file:

- `-1` collides with valid values in principle (e.g. `normalised_current_level = -1` is a legal AO voltage in the configured `±10V` range).
- "Filled or not" checks are scattered as `== -1` comparisons in both `program.cs` and call sites.
- The mutate-in-place trick (caveat 2 above) exists only because `-1` can't be distinguished from a real value without overwriting it.

Newtonsoft.Json handles `int?` / `double?` cleanly: missing JSON fields stay `null`, populated fields parse as usual. Switching to nullable types lets you replace every `== -1` with `.HasValue`, and removes the need for in-place mutation entirely.

### TODO 2. Don't mutate the incoming `StimulationControlFrame`

`ApplyControlData` is currently doing two jobs: (a) parsing — filling missing fields from persisted state — and (b) actuation — writing to the NI hardware. The persisted-state update at lines 262-264 then has to read back the (mutated) frame to know what was used.

Split these. Compute an effective `(length, width, freq, current)` tuple from `(frame, persisted state)` once, pass that to the writer, and use the same tuple to update persisted state. The line 262-264 weirdness can't exist after this, and the data flow becomes one-way.

This is most natural to do together with TODO 1, since nullable types make the "merge with persisted state" step trivial.

### TODO 3. Move `ApplyControlData` / `ApplyControlDataSimultaneous` out of `StimulationControlFrame`

`StimulationControlFrame` should be a DTO — plain serialisable data. Today it owns NI writer access, hardware timing (the `Stop`/`WriteMultiSample`/`Start`/`WaitUntilDone`/`Sleep` dance), and state mutation. Move the actuator methods into `PainlabNIDS5Protocol` or a dedicated `StimulationController` class. Benefits:

- The frame becomes trivially testable and loggable.
- The hardware-touching code lives in one place with the rest of the NI setup.
- The boundary between "what the host asked for" and "what we did" becomes explicit.

### TODO 4. Make the mode an `enum`, dispatch on it explicitly

`_channelConfig.switch_channel_method` is currently a string compared as `== "dual"` / `== "control-trigger"` / else. A typo in `Resources/channel-config.json` (e.g. `"Dual"` or `"contol-trigger"`) silently falls through to the `else` branch and runs in "digital" mode without any warning.

Parse the string into an `enum { Dual, ControlTrigger, Digital }` once at startup, throw on unknown values, and `switch` on the enum. The three branches become symmetric and the implicit-else disappears.

### TODO 5. Consolidate the stim state

`_pulseLength`, `_pulseWidth`, `_frequency`, `_selected_channel`, `_last_ts` are scattered fields on `PainlabNIDS5Protocol` that together describe "current stim state". Group them into a single struct/class (e.g. `StimState`) updated atomically.

This also makes the cross-thread access between the control thread (which mutates them in `ApplyControlData`) and the DAQ callback thread (which reads `_last_ts` in `PrepareDataFrameBytes`) much easier to reason about — currently any thread-safety claim has to be made field by field.
