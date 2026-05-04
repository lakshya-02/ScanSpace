# ScanSpace

ScanSpace is a Meta Quest 3 mixed reality concept that turns a real-world object into an interactive 3D model inside your space. The public version of this repository is kept intentionally conceptual: it documents the experience, Unity-facing interaction flow, and demo, while the full AI generation pipeline is kept private for closed-source development.

## Demo

[![Watch the ScanSpace demo](https://img.youtube.com/vi/fjbvRinMEPQ/maxresdefault.jpg)](https://www.youtube.com/watch?v=fjbvRinMEPQ)

Watch on YouTube: https://www.youtube.com/watch?v=fjbvRinMEPQ

## Concept

1. Capture a passthrough image on Meta Quest 3.
2. Crop the real-world object from the headset view.
3. Send the cropped image to a private 3D generation pipeline.
4. Receive a generated `.glb` model.
5. Place, move, rotate, scale, remove, and save the model in mixed reality.

## Public Repo Scope

This repository is meant to show the idea, interaction model, and Unity-side mixed reality experience without exposing the private model-generation backend.

Included:

- Unity project structure for the ScanSpace MR concept.
- Meta Quest 3 passthrough capture flow.
- Conceptual client-side model placement and manipulation flow.
- Demo video and project overview.

Not included:

- The production AI model-generation pipeline.
- Backend server implementation.
- Private setup scripts, model weights, local outputs, or deployment details.

For a complete closed-source build, create a separate private repository that contains the full configured project and the private generation pipeline.

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

## Build Notes

- Target device: Meta Quest 3.
- This public repository is not a full production build because the private generation pipeline is excluded.
- Connect the Unity client to your own compatible backend if you are developing a private end-to-end version.
- Keep private backend credentials, model weights, generated outputs, and deployment scripts out of the public repository.

## Team

- Lakshya Singh
- Ayush Kumar

Note: commits or activity under the name `yeshwanth` were also made by Lakshya Singh from another device.

## Contact

Lakshya Singh  
Email: lakshya.singh2706@gmail.com  
GitHub: [lakshya-02](https://github.com/lakshya-02)
