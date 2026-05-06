# ScanSpace

ScanSpace is a Meta Quest 3 mixed reality concept prototype: capture a real-world object, generate a 3D model from it, and place that model back into your room.

This repository is a lightweight public template. It intentionally does not include the full Unity project, private backend, generated models, credentials, model weights, or deployment files. The goal is to show that the concept is possible and document the pipeline clearly.

## Demo

[![Watch the ScanSpace demo](https://img.youtube.com/vi/fjbvRinMEPQ/maxresdefault.jpg)](https://www.youtube.com/watch?v=fjbvRinMEPQ)

Watch the demo on YouTube: https://www.youtube.com/watch?v=fjbvRinMEPQ

## Concept Pipeline

1. Capture a passthrough image on Meta Quest 3.
2. Crop the object from the headset view.
3. Send the cropped object image to an image-to-3D server.
4. Run the generation model on a GPU backend.
5. Return a generated `.glb` file to Unity.
6. Load the `.glb` at runtime.
7. Place, move, rotate, scale, save, or remove the model in mixed reality.

## Model Stage

The model stage is an image-to-3D generation step. In the full prototype, the cropped object image is sent to a GPU-backed server that runs a 3D generation model and exports the result as a binary `.glb` file.

At a high level, the model is expected to:

- Take a single object image as input.
- Reconstruct a usable 3D mesh from the image.
- Generate or preserve basic texture/material detail.
- Export the result in GLB format so Unity can load it at runtime.

This public repo does not include model weights or private generation code. It only documents where that model fits into the pipeline.

## What Is Included

This repo includes only a small Unity-style template:

- `Packages/manifest.json` with the main Unity package ideas.
- `ProjectSettings/ProjectVersion.txt` to document the Unity editor version used in the prototype.
- `Assets/Scenes/.gitkeep` as an empty scene folder placeholder.
- `Assets/Scripts/ScanSpaceServerConfig.cs` as a placeholder backend configuration object.
- `Assets/Scripts/ScanSpacePipelineTemplate.cs` as a conceptual Unity client flow.
- `docs/hugging-face-server.md` explaining how a Hugging Face hosted server can fit into the pipeline.

This is not a drop-in production build. It is a public concept skeleton for understanding and rebuilding the idea.

## Hugging Face Server Idea

The private generation backend can be represented publicly as a Hugging Face-hosted server. A practical setup would be:

- Host the image-to-3D model on Hugging Face Spaces or another GPU-backed Hugging Face deployment.
- Expose an HTTP endpoint such as `POST /generate`.
- Accept the cropped object image from Unity as multipart form data.
- Run the image-to-3D pipeline on the server.
- Return a binary `.glb` model to the Unity client.

The Unity template in this repo does not include the private model code, but it shows where the endpoint URL, optional token, and client request flow would connect.

## Public Scope

Included:

- Project overview.
- Demo video.
- High-level pipeline.
- Minimal Unity concept template.
- Hugging Face server explanation.

Not included:

- Full Unity source project.
- Private image-to-3D backend.
- API keys or credentials.
- Model weights.
- Generated `.glb` files.
- Local build artifacts.

## Team

- Lakshya Singh
- Ayush Kumar

## Contact

Lakshya Singh  
Email: lakshya.singh2706@gmail.com  
GitHub: [lakshya-02](https://github.com/lakshya-02)
