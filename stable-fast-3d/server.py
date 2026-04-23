"""
FastAPI server for Stable Fast 3D model.
Receives an image and returns a 3D GLB model.

Usage:
    python server.py

The server will run on http://0.0.0.0:8000
"""

import io
import os
import socket
import tempfile
import time
from contextlib import asynccontextmanager, nullcontext
from typing import Any

import numpy as np
import rembg
import torch
from fastapi import FastAPI, File, HTTPException, Query, Request, UploadFile
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, JSONResponse
from PIL import Image

import sf3d.utils as sf3d_utils
from sf3d.system import SF3D

# Constants
COND_WIDTH = 512
COND_HEIGHT = 512
COND_DISTANCE = 1.6
COND_FOVY_DEG = 40
BACKGROUND_COLOR = [0.5, 0.5, 0.5]
MAX_UPLOAD_SIZE_MB = 50  # 50MB max for camera images

PROFILE_AUTO = "auto"
PROFILE_LITE = "lite"
PROFILE_FULL = "full"


def detect_runtime_profile() -> str:
    """Choose an inference profile based on env override or available VRAM."""
    env_profile = os.getenv("SF3D_PROFILE", PROFILE_AUTO).strip().lower()
    if env_profile in {PROFILE_LITE, PROFILE_FULL}:
        return env_profile

    if torch.cuda.is_available():
        total_vram_gb = torch.cuda.get_device_properties(0).total_memory / (1024**3)
        return PROFILE_FULL if total_vram_gb >= 12 else PROFILE_LITE

    return PROFILE_LITE


def get_profile_defaults(profile: str) -> tuple[int, int]:
    if profile == PROFILE_FULL:
        return 1024, -1

    return 512, 5000


RUNTIME_PROFILE = detect_runtime_profile()
DEFAULT_TEXTURE_SIZE, DEFAULT_VERTEX_COUNT = get_profile_defaults(RUNTIME_PROFILE)

# Global model and session references
model = None
rembg_session = None
device = None
c2w_cond = None
intrinsic = None
intrinsic_normed_cond = None


def get_lan_ip() -> str:
    """Get the LAN IP address of this machine."""
    try:
        # Connect to external address to determine local IP
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except Exception:
        return "127.0.0.1"


@asynccontextmanager
async def lifespan(app: FastAPI):
    """Load the model on startup and clean up on shutdown."""
    global model, rembg_session, device, c2w_cond, intrinsic, intrinsic_normed_cond

    print("Loading SF3D model...")
    device = sf3d_utils.get_device()
    print(f"Using device: {device}")
    if torch.cuda.is_available():
        total_vram_gb = torch.cuda.get_device_properties(0).total_memory / (1024**3)
        print(f"Detected VRAM: {total_vram_gb:.1f} GB")
    print(f"Selected profile: {RUNTIME_PROFILE.upper()}")

    # Load model
    model = SF3D.from_pretrained(
        "stabilityai/stable-fast-3d",
        config_name="config.yaml",
        weight_name="model.safetensors",
    )
    model.eval()
    model = model.to(device)

    # Initialize rembg session
    rembg_session = rembg.new_session()

    # Pre-compute camera parameters (cached, doesn't change)
    c2w_cond = sf3d_utils.default_cond_c2w(COND_DISTANCE)
    intrinsic, intrinsic_normed_cond = sf3d_utils.create_intrinsic_from_fov_deg(
        COND_FOVY_DEG, COND_HEIGHT, COND_WIDTH
    )

    print("Model loaded successfully!")

    # Print LAN URL for easy testing
    lan_ip = get_lan_ip()
    print("\n" + "=" * 50)
    print("Server ready! Access from Quest 3 using:")
    print(f"  Health check: http://{lan_ip}:8000/health")
    print(f"  Generate:     http://{lan_ip}:8000/generate")
    print(f"  API docs:     http://{lan_ip}:8000/docs")
    print("-" * 50)
    print(f"  MODE: {RUNTIME_PROFILE.upper()}")
    print(f"  Texture: {DEFAULT_TEXTURE_SIZE}, Vertices: {DEFAULT_VERTEX_COUNT}")
    print("=" * 50 + "\n")

    yield

    # Cleanup
    print("Shutting down...")


app = FastAPI(
    title="Stable Fast 3D API",
    description="API for generating 3D models from images using Stable Fast 3D",
    version="1.0.0",
    lifespan=lifespan,
)

# Add CORS middleware for Unity WebGL or other cross-origin requests
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],  # Allow all origins for local development
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.middleware("http")
async def log_requests(request: Request, call_next):
    """Log all requests with client IP, endpoint, duration, and status."""
    start_time = time.time()
    client_ip = request.client.host if request.client else "unknown"

    # Check upload size before processing
    content_length = request.headers.get("content-length")
    if content_length and int(content_length) > MAX_UPLOAD_SIZE_MB * 1024 * 1024:
        print(f"[{client_ip}] {request.method} {request.url.path} - REJECTED (file too large)")
        return JSONResponse(
            status_code=413,
            content={"error": "File too large", "max_size_mb": MAX_UPLOAD_SIZE_MB},
        )

    try:
        response = await call_next(request)
        duration = time.time() - start_time
        print(f"[{client_ip}] {request.method} {request.url.path} - {response.status_code} ({duration:.2f}s)")
        return response
    except Exception as e:
        duration = time.time() - start_time
        print(f"[{client_ip}] {request.method} {request.url.path} - ERROR ({duration:.2f}s): {e}")
        raise


def create_batch(input_image: Image.Image) -> dict[str, Any]:
    """Create model input batch from PIL Image."""
    img_cond = (
        torch.from_numpy(
            np.asarray(input_image.resize((COND_WIDTH, COND_HEIGHT))).astype(np.float32)
            / 255.0
        )
        .float()
        .clip(0, 1)
    )
    mask_cond = img_cond[:, :, -1:]
    rgb_cond = torch.lerp(
        torch.tensor(BACKGROUND_COLOR)[None, None, :], img_cond[:, :, :3], mask_cond
    )

    batch_elem = {
        "rgb_cond": rgb_cond,
        "mask_cond": mask_cond,
        "c2w_cond": c2w_cond.unsqueeze(0),
        "intrinsic_cond": intrinsic.unsqueeze(0),
        "intrinsic_normed_cond": intrinsic_normed_cond.unsqueeze(0),
    }
    # Add batch dim
    batched = {k: v.unsqueeze(0) for k, v in batch_elem.items()}
    return batched


def process_image(image: Image.Image, foreground_ratio: float) -> Image.Image:
    """Remove background and resize foreground."""
    # Remove background
    image_rgba = sf3d_utils.remove_background(image.convert("RGBA"), rembg_session)
    # Resize foreground
    image_processed = sf3d_utils.resize_foreground(
        image_rgba, foreground_ratio, out_size=(COND_WIDTH, COND_HEIGHT)
    )
    return image_processed


def generate_mesh(
    input_image: Image.Image,
    texture_size: int = 1024,
    remesh_option: str = "none",
    vertex_count: int = -1,
) -> str:
    """Generate 3D mesh from image and return path to GLB file."""
    start = time.time()

    with torch.no_grad():
        with (
            torch.autocast(device_type=device, dtype=torch.bfloat16)
            if "cuda" in device
            else nullcontext()
        ):
            model_batch = create_batch(input_image)
            model_batch = {k: v.to(device) for k, v in model_batch.items()}
            trimesh_mesh, _glob_dict = model.generate_mesh(
                model_batch, texture_size, remesh_option, vertex_count
            )
            trimesh_mesh = trimesh_mesh[0]

    # Export to temporary GLB file
    tmp_file = tempfile.NamedTemporaryFile(delete=False, suffix=".glb")
    trimesh_mesh.export(tmp_file.name, file_type="glb", include_normals=True)

    print(f"Generation took: {time.time() - start:.2f}s")

    if torch.cuda.is_available():
        print(f"Peak Memory: {torch.cuda.max_memory_allocated() / 1024 / 1024:.2f} MB")

    return tmp_file.name


@app.get("/")
async def root():
    """Health check endpoint."""
    return {
        "status": "ok",
        "message": "Stable Fast 3D API is running",
        "device": device,
    }


@app.get("/health")
async def health():
    """Health check endpoint for Quest 3 connectivity testing."""
    return {"status": "ok"}


@app.post("/generate")
async def generate_3d_model(
    image: UploadFile = File(..., description="Input image file (PNG, JPG, JPEG)"),
    foreground_ratio: float = Query(
        0.85, ge=0.5, le=1.0, description="Foreground ratio (0.5-1.0)"
    ),
    texture_size: int = Query(
        None, ge=256, le=2048, description="Texture resolution (256-2048), default based on runtime profile"
    ),
    remesh_option: str = Query(
        "none",
        pattern="^(none|triangle|quad)$",
        description="Remeshing option: none, triangle, or quad",
    ),
    vertex_count: int = Query(
        None, ge=-1, le=50000, description="Target vertex count (-1 for no limit)"
    ),
):
    """
    Generate a 3D GLB model from an uploaded image.

    - **image**: Input image file (PNG, JPG, JPEG)
    - **foreground_ratio**: How much of the image should be foreground (0.5-1.0)
    - **texture_size**: Resolution of the output texture (256-2048)
    - **remesh_option**: Mesh topology option (none/triangle/quad)
    - **vertex_count**: Target vertex count (-1 for automatic)

    Returns: GLB file containing the 3D model
    """
    # Apply runtime-profile defaults if not specified
    if texture_size is None:
        texture_size = DEFAULT_TEXTURE_SIZE
    if vertex_count is None:
        vertex_count = DEFAULT_VERTEX_COUNT
    # Validate file type
    if not image.content_type or not image.content_type.startswith("image/"):
        raise HTTPException(
            status_code=400,
            detail="Invalid file type. Please upload an image (PNG, JPG, JPEG)",
        )

    try:
        # Read and process image
        contents = await image.read()
        pil_image = Image.open(io.BytesIO(contents))

        # Process image (remove background, resize)
        processed_image = process_image(pil_image, foreground_ratio)

        # Generate 3D mesh
        glb_path = generate_mesh(
            processed_image,
            texture_size=texture_size,
            remesh_option=remesh_option,
            vertex_count=vertex_count,
        )

        # Return the GLB file
        return FileResponse(
            glb_path,
            media_type="model/gltf-binary",
            filename="model.glb",
            headers={
                "Content-Disposition": "attachment; filename=model.glb",
                "Access-Control-Expose-Headers": "Content-Disposition",
            },
        )

    except Exception as e:
        print(f"Error generating model: {e}")
        raise HTTPException(status_code=500, detail=f"Error generating model: {str(e)}")


@app.post("/generate/base64")
async def generate_3d_model_base64(
    image: UploadFile = File(..., description="Input image file (PNG, JPG, JPEG)"),
    foreground_ratio: float = Query(0.85, ge=0.5, le=1.0),
    texture_size: int = Query(None, ge=256, le=2048),
    remesh_option: str = Query("none", pattern="^(none|triangle|quad)$"),
    vertex_count: int = Query(None, ge=-1, le=50000),
):
    """
    Generate a 3D GLB model and return it as base64 encoded string.
    Useful for Unity when you need the data directly instead of a file download.
    """
    import base64

    # Apply runtime-profile defaults if not specified
    if texture_size is None:
        texture_size = DEFAULT_TEXTURE_SIZE
    if vertex_count is None:
        vertex_count = DEFAULT_VERTEX_COUNT

    # Validate file type
    if not image.content_type or not image.content_type.startswith("image/"):
        raise HTTPException(
            status_code=400,
            detail="Invalid file type. Please upload an image (PNG, JPG, JPEG)",
        )

    try:
        # Read and process image
        contents = await image.read()
        pil_image = Image.open(io.BytesIO(contents))

        # Process image (remove background, resize)
        processed_image = process_image(pil_image, foreground_ratio)

        # Generate 3D mesh
        glb_path = generate_mesh(
            processed_image,
            texture_size=texture_size,
            remesh_option=remesh_option,
            vertex_count=vertex_count,
        )

        # Read the GLB file and encode as base64
        with open(glb_path, "rb") as f:
            glb_bytes = f.read()

        glb_base64 = base64.b64encode(glb_bytes).decode("utf-8")

        return {
            "success": True,
            "model_base64": glb_base64,
            "format": "glb",
        }

    except Exception as e:
        print(f"Error generating model: {e}")
        raise HTTPException(status_code=500, detail=f"Error generating model: {str(e)}")


if __name__ == "__main__":
    import uvicorn

    print("Starting Stable Fast 3D API server...")
    print("Binding to 0.0.0.0:8000 (accessible from LAN)")
    uvicorn.run(
        app,
        host="0.0.0.0",
        port=8000,
        timeout_keep_alive=120,  # Keep connections alive for slower networks
    )
