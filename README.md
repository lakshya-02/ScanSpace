# ScanSpace

ScanSpace is a Meta Quest 3 mixed reality app that turns a real-world object into an interactive 3D model. Point the headset at an object, capture an image, send it to the local Stable Fast 3D server, and place the generated model back into MR space.

## What It Does

- Captures a passthrough camera image on Meta Quest 3.
- Lets the user crop the object before generation.
- Sends the image to a FastAPI Stable Fast 3D backend.
- Receives a generated `.glb` model.
- Places the model in mixed reality.
- Supports dragging, moving, rotating, scaling, removing, and saving the generated model locally.
- Restores the latest saved model on app restart.

## Controls

- `A`: Capture image.
- `Right stick`: Move crop frame while crop mode is active.
- `Left stick`: Resize crop frame while crop mode is active.
- `B`: Generate model from the cropped image.
- `Right trigger`: Drag/place generated model.
- `Right stick`: Move generated model.
- `Left stick X`: Rotate model on Y axis.
- `Left stick Y`: Scale model.
- `Grip + Left stick X`: Rotate model on Z axis.
- `Grip + Left stick Y`: Rotate model on X axis.
- `X`: Save generated model locally.
- `Y`: Remove model or clear preview.

## Backend

The backend is in:

```text
stable-fast-3d/
```

Start the LAN server:

```powershell
cd stable-fast-3d
.\start_server_lan.bat
```

The Unity app reads the server URL from:

```text
Assets/ServerConfig.asset
```

For local Quest testing, set `baseUrl` to the PC LAN address, for example:

```text
http://192.168.0.182:8000
```

Health check:

```text
http://192.168.0.182:8000/health
```

## Unity Setup

Important scene references:

- `SnapshotCapture`
  - `APIClient`
  - `ModelLoader`
  - `PassthroughCameraAccess`
  - `CenterEyeAnchor`
  - Preview canvas/image/status text
  - Audio source and clips

- `APIClient`
  - `ServerConfig`
  - `ModelLoader`
  - Optional status text/loading indicator

- `ModelLoader`
  - `CenterEyeAnchor`

Audio slots in `SnapshotCapture`:

- `Opening Sound`
- `Capture Sound`
- `Generating Sound`
- `Success Sound`

## Build Notes

- Target device: Meta Quest 3.
- Keep the FastAPI server running while using the APK.
- Quest and PC must be on the same Wi-Fi for LAN mode.
- If using a tunnel such as ngrok, update `Assets/ServerConfig.asset` before building.

## Team

- Lakshya Singh
- Ayush Kumar

Note: commits or activity under the name `yeshwanth` were also made by Lakshya Singh from another device.

## Contact

Lakshya Singh
Email: lakshya.singh2706@gmail.com
GitHub: [lakshya-02](https://github.com/lakshya-02)
