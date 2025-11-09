# Vive Ultimate Tracker Without HMD (Unity)
for [日本語](readme_ja.md)
Minimal Unity integration for reading Vive Ultimate Tracker poses directly from the USB dongle without requiring a headset. It parses raw HID reports, exposes tracker states, applies a simple coordinate conversion (X / Yaw / Roll inversion), and supports a single global transform offset.

## Install via Unity Package Manager (Git URL)
You can install this repository directly as a Unity package via Git URL:

- Open Unity: Window > Package Manager
- Click the + button > "Add package from git URL..."
- Paste the following URL and press Add:
  - https://github.com/neon-izm/ViveUltimateTrackerWithoutHmdUnity.git?path=/Assets/ViveUltimateTrackerStandalone

Unity will fetch the package under Packages/, and you can start using the components in your project.

## Sample Scene
A minimal sample scene is included to demonstrate basic usage. Open the sample scene (for example under `Assets/Scenes/`) and press Play:
- The scene shows how `UltimateTrackerReceiver` connects to the dongle and how the adjusted Unity pose is applied.
- Use it as a reference template to wire the receiver and the pose applier to your own GameObjects.

## Overview
Components:
- `UltimateTrackerReceiver` (Runtime): Opens the dongle (VID=0x0BB4, PID=0x0350), spawns a read loop, parses incoming reports, maintains up to 5 tracker slots, emits events.
- `TrackerReportParser`: Decodes raw input reports (pose packets, RF status, ACK responses, pairing events) into `SimpleTrackerState` instances.
- `UltimateTrackerPoseApplier`: Minimal MonoBehaviour that applies the adjusted Unity pose to a target `Transform`.
- `TrackerProtocol`: Constants and lightweight data structures (no logic).

## Tracker State (`SimpleTrackerState`)
Relevant fields:
- `Position`, `Rotation`: Raw pose from the dongle packet.
- `UnityPosition`, `UnityRotation`: Pose after axis inversion (X position sign, Yaw, Roll angles).
- `UnityPositionAdjusted`, `UnityRotationAdjusted`: Final pose after applying the global offset matrix.
- `TrackingState`, `Buttons`, `PacketIndex`, timing and status fields for diagnostics.

## Events
`UltimateTrackerReceiver` exposes:
- `OnTrackerPose`: Raw tracker pose parsed (after basic extraction, before user-level transform beyond axis inversion + offset).
- `OnTrackerConnected` / `OnTrackerDisconnected`
- `OnRfStatus` / `OnVendorStatus` / `OnPairEvent` / `OnAck`

(If you only need the final Unity-space pose, read `UnityPositionAdjusted` / `UnityRotationAdjusted` from states during `OnTrackerPose`.)

## Coordinate Conversion Logic
1. Raw pose (right-handed device space) is read into `Position` / `Rotation`.
2. Axis inversion applied:
   - X position sign flipped.
   - Yaw (Y axis) and Roll (Z axis) Euler angle signs flipped.
3. Quaternion is normalized.
4. The global offset matrix is left-multiplied: `finalMatrix = GlobalOffset * TRS(unityPos, unityRot)`.
5. Columns of the resulting matrix produce `UnityPositionAdjusted` (translation) and `UnityRotationAdjusted` (LookRotation with forward=Z column, up=Y column).

## Global Offset
A single `Matrix4x4` (`_unityOffset`) applies to all trackers.
Setter methods:
- `SetGlobalUnityOffset(Matrix4x4 offset)`
- `SetGlobalUnityOffset(Vector3 positionOffset, Quaternion rotationOffset)`
- `ClearGlobalUnityOffset()` / `ClearTracker()` resets to identity.

Typical usage: capture a tracker at a calibration pose and store the inverse of its current TRS as the global offset.

## Quick Start
1. Add `UltimateTrackerReceiver` to a GameObject in your scene.
2. (Optional) Enable `autoConnectOnStart` to connect automatically.
3. Add `UltimateTrackerPoseApplier` to any GameObject you want to drive; assign the receiver.
4. Run the scene; tracker poses will be applied when packets arrive.
5. Access calibration:
   ```csharp
   // Example: set a custom offset
   receiver.SetGlobalUnityOffset(new Vector3(0f, 1f, 0f), Quaternion.Euler(0, 90, 0));
   // Reset
   receiver.ClearGlobalUnityOffset();
   ```

## Diagnostics
- `ReportsParsed`, `PosePacketsParsed` counters available from receiver.
- `LastUpdateUtcTicks` and `IsActive` indicate freshness (< ~1000ms) per tracker.
- Enable `verboseLog` for detailed packet-level output.

## Extensibility / Next Steps
Potential improvements you can add:
- Additional coordinate system modes (e.g., full right-handed -> left-handed matrix conversion variants).
- Per-tracker offsets (re-introduce dictionary approach if needed).
- Smoothing / prediction layer (exponential smoothing, velocity-based extrapolation).
- Network broadcast (OSC/UDP) of adjusted poses.
- Editor tooling: calibration wizard & live gizmos.

## Limitations
- Axis inversion uses Euler angles; edge cases (gimbal singularities) may require a more robust approach.
- Only one global offset matrix is supported.
- Pairing/unpair logic is heuristic; production use may need refinement.

## License
See `LICENSE` in the repository.

## Disclaimer
Reverse engineering aspects and protocol handling are simplified; adjust reliability and error handling for production.

## Reference Project
This project was informed and inspired by the excellent reverse‑engineering and experimentation work in:

- https://github.com/mgschwan/ViveUltimateTrackerMobile ("ViveUltimateTrackerMobile")

That repository focuses on a mobile / Android pathway (Java/Kotlin + USB HID bridging) for the Vive Ultimate Tracker dongle, including experimenting with pairing, HID feature reports, and pose packet decoding. While this Unity implementation was written independently, the general packet structure observations and the idea of operating the Ultimate Tracker without a headset are aligned with that project's exploration. Please visit and credit the original work when building derivative tooling.

Differences in this repo:
- Single global Matrix4x4 offset (rather than per‑device abstractions)
- Minimal Unity MonoBehaviours (`UltimateTrackerReceiver`, `UltimateTrackerPoseApplier`)
- Simplified axis inversion (X position / Yaw / Roll) and calibration flow

If you extend this project, consider contributing improvements or protocol clarifications back to the original reference repository as well.
