# Hugging Face Server Concept

ScanSpace can use a Hugging Face-hosted GPU server for the image-to-3D part of the pipeline.

## Role In The Pipeline

Unity handles the mixed reality experience:

1. Capture passthrough image.
2. Crop the object.
3. Upload the crop to a server.
4. Receive a generated `.glb` file.
5. Load and place the model in MR.

The Hugging Face server handles the generation step:

1. Receive the cropped image.
2. Run an image-to-3D model or pipeline.
3. Export the generated asset as GLB.
4. Return the GLB bytes to Unity.

## Model Stage

The model is the core image-to-3D component of the system. It takes the cropped object image from the headset and generates a 3D asset that can be loaded in Unity.

Expected model behavior:

- Input: one cropped object image.
- Output: a textured or material-ready `.glb` model.
- Runtime target: GPU-backed inference on a hosted server.
- Unity handoff: binary GLB bytes returned through HTTP.

This repository does not publish the private model, weights, or training/inference code. For a public rebuild, a developer can connect any compatible image-to-3D model that can produce GLB output.

## Example API Shape

```http
POST /generate
Content-Type: multipart/form-data

field: image = cropped object image
```

Expected response:

```http
200 OK
Content-Type: model/gltf-binary

<binary GLB bytes>
```

## Deployment Options

A public concept implementation can use:

- Hugging Face Spaces with a Gradio/FastAPI app.
- A GPU runtime for image-to-3D generation.
- A private Space or protected endpoint when the model is expensive or should not be public.

## Unity Connection

In Unity, set the server URL in `ScanSpaceServerConfig`:

```text
baseUrl: https://your-hugging-face-space.hf.space
generatePath: /generate
bearerToken: optional token for private deployments
```

The template does not include private model code, weights, or deployment files. It only documents the expected server contract so the public repo can explain how the full prototype works.
