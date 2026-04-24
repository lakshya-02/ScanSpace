"""
Scan Space Stable Fast 3D backend.

Compatible with the current Unity client:
- GET /health
- POST /generate
- POST /generate/base64

Environment variables:
- SF3D_PROFILE=auto|lite|full
- SCANSPACE_HOST=0.0.0.0
- SCANSPACE_PORT=8000
- SCANSPACE_ALLOW_ORIGINS=*
- SCANSPACE_API_TOKEN=<optional bearer token>
- SCANSPACE_CACHE_ENABLED=true|false
- SCANSPACE_CACHE_DIR=<optional cache directory>
- SCANSPACE_MAX_UPLOAD_MB=50
"""

import asyncio
import base64
import hmac
import io
import json
import os
import socket
import tempfile
import time
from contextlib import asynccontextmanager, nullcontext
from hashlib import sha256
from pathlib import Path
from typing import Any

import numpy as np
import rembg
import torch
from fastapi import FastAPI, File, HTTPException, Query, Request, Response, UploadFile
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from PIL import Image

import sf3d.utils as sf3d_utils
from sf3d.system import SF3D

# Image and camera conditioning constants.
COND_WIDTH = 512
COND_HEIGHT = 512
COND_DISTANCE = 1.6
COND_FOVY_DEG = 40
BACKGROUND_COLOR = [0.5, 0.5, 0.5]

PROFILE_AUTO = "auto"
PROFILE_LITE = "lite"
PROFILE_FULL = "full"

PROJECT_ROOT = Path(__file__).resolve().parent
MAX_UPLOAD_SIZE_MB = int(os.getenv("SCANSPACE_MAX_UPLOAD_MB", "50"))
MAX_UPLOAD_SIZE_BYTES = MAX_UPLOAD_SIZE_MB * 1024 * 1024
HOST = os.getenv("SCANSPACE_HOST", "0.0.0.0").strip() or "0.0.0.0"
PORT = int(os.getenv("SCANSPACE_PORT", "8000"))
EXPECTED_BEARER_TOKEN = (
    os.getenv("SCANSPACE_API_TOKEN", "") or os.getenv("SCANSPACE_BEARER_TOKEN", "")
).strip()


def env_flag(name: str, default: bool) -> bool:
    value = os.getenv(name)
    if value is None:
        return default
    return value.strip().lower() in {"1", "true", "yes", "on"}


def split_origins() -> list[str]:
    raw_value = os.getenv("SCANSPACE_ALLOW_ORIGINS", "*").strip()
    if raw_value == "*" or not raw_value:
        return ["*"]

    origins = [origin.strip() for origin in raw_value.split(",") if origin.strip()]
    return origins or ["*"]


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
CACHE_ENABLED = env_flag("SCANSPACE_CACHE_ENABLED", True)
CACHE_DIR = Path(
    os.getenv("SCANSPACE_CACHE_DIR", str(PROJECT_ROOT / "output" / "cache"))
).resolve()

# Global model/session references loaded once at startup.
model = None
rembg_session = None
device = None
c2w_cond = None
intrinsic = None
intrinsic_normed_cond = None

generation_lock = asyncio.Lock()
metrics = {
    "requests_total": 0,
    "requests_inflight": 0,
    "generate_requests_total": 0,
    "successful_generations": 0,
    "failed_generations": 0,
    "cache_hits": 0,
    "cache_misses": 0,
    "last_generation_seconds": None,
    "last_error": None,
    "last_request_hash": None,
}


def get_lan_ip() -> str:
    """Get the LAN IP address of this machine."""
    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.connect(("8.8.8.8", 80))
        ip = sock.getsockname()[0]
        sock.close()
        return ip
    except Exception:
        return "127.0.0.1"


def get_gpu_info() -> dict[str, Any]:
    if not torch.cuda.is_available():
        return {"available": False}

    props = torch.cuda.get_device_properties(0)
    return {
        "available": True,
        "name": props.name,
        "total_vram_gb": round(props.total_memory / (1024**3), 2),
    }


@asynccontextmanager
async def lifespan(app: FastAPI):
    """Load the model on startup and prepare cache directories."""
    global model, rembg_session, device, c2w_cond, intrinsic, intrinsic_normed_cond

    CACHE_DIR.mkdir(parents=True, exist_ok=True)

    print("Loading Scan Space Stable Fast 3D backend...")
    device = sf3d_utils.get_device()
    print(f"Using device: {device}")

    gpu_info = get_gpu_info()
    if gpu_info["available"]:
        print(f"Detected GPU: {gpu_info['name']}")
        print(f"Detected VRAM: {gpu_info['total_vram_gb']:.1f} GB")

    print(f"Selected profile: {RUNTIME_PROFILE.upper()}")
    print(f"Cache enabled: {CACHE_ENABLED}")
    print(f"Cache directory: {CACHE_DIR}")
    print(f"Auth enabled: {bool(EXPECTED_BEARER_TOKEN)}")

    model = SF3D.from_pretrained(
        "stabilityai/stable-fast-3d",
        config_name="config.yaml",
        weight_name="model.safetensors",
    )
    model.eval()
    model = model.to(device)

    rembg_session = rembg.new_session()
    c2w_cond = sf3d_utils.default_cond_c2w(COND_DISTANCE)
    intrinsic, intrinsic_normed_cond = sf3d_utils.create_intrinsic_from_fov_deg(
        COND_FOVY_DEG, COND_HEIGHT, COND_WIDTH
    )

    lan_ip = get_lan_ip()
    print("\n" + "=" * 58)
    print("Scan Space backend ready")
    print(f"  Health:   http://{lan_ip}:{PORT}/health")
    print(f"  Generate: http://{lan_ip}:{PORT}/generate")
    print(f"  Docs:     http://{lan_ip}:{PORT}/docs")
    print("-" * 58)
    print(f"  Mode:          {RUNTIME_PROFILE.upper()}")
    print(f"  Texture size:  {DEFAULT_TEXTURE_SIZE}")
    print(f"  Vertex count:  {DEFAULT_VERTEX_COUNT}")
    print("=" * 58 + "\n")

    yield

    print("Shutting down Scan Space backend...")


app = FastAPI(
    title="Scan Space Stable Fast 3D API",
    description="Quest-ready Stable Fast 3D API for Scan Space",
    version="1.1.0",
    lifespan=lifespan,
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=split_origins(),
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
    expose_headers=[
        "Content-Disposition",
        "X-ScanSpace-Cache",
        "X-ScanSpace-Request-Hash",
        "X-ScanSpace-Profile",
        "X-ScanSpace-Duration",
    ],
)


@app.middleware("http")
async def log_requests(request: Request, call_next):
    """Log all requests with client IP, endpoint, duration, and status."""
    start_time = time.time()
    client_ip = request.client.host if request.client else "unknown"
    metrics["requests_total"] += 1
    metrics["requests_inflight"] += 1

    content_length = request.headers.get("content-length")
    if content_length and int(content_length) > MAX_UPLOAD_SIZE_BYTES:
        metrics["requests_inflight"] -= 1
        print(f"[{client_ip}] {request.method} {request.url.path} - REJECTED (file too large)")
        return JSONResponse(
            status_code=413,
            content={"error": "File too large", "max_size_mb": MAX_UPLOAD_SIZE_MB},
        )

    try:
        response = await call_next(request)
        duration = time.time() - start_time
        print(
            f"[{client_ip}] {request.method} {request.url.path} "
            f"- {response.status_code} ({duration:.2f}s)"
        )
        return response
    except Exception as exc:
        duration = time.time() - start_time
        metrics["last_error"] = str(exc)
        print(
            f"[{client_ip}] {request.method} {request.url.path} "
            f"- ERROR ({duration:.2f}s): {exc}"
        )
        raise
    finally:
        metrics["requests_inflight"] = max(0, metrics["requests_inflight"] - 1)


def require_bearer_token(request: Request) -> None:
    if not EXPECTED_BEARER_TOKEN:
        return

    authorization = request.headers.get("Authorization", "").strip()
    scheme, _, token = authorization.partition(" ")
    if scheme.lower() != "bearer" or not hmac.compare_digest(
        token.strip(), EXPECTED_BEARER_TOKEN
    ):
        raise HTTPException(status_code=401, detail="Missing or invalid bearer token")


def create_batch(input_image: Image.Image) -> dict[str, Any]:
    """Create model input batch from a processed RGBA image."""
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
    return {key: value.unsqueeze(0) for key, value in batch_elem.items()}


def process_image(image: Image.Image, foreground_ratio: float) -> Image.Image:
    """Remove the background and resize the foreground for SF3D."""
    image_rgba = sf3d_utils.remove_background(image.convert("RGBA"), rembg_session)
    return sf3d_utils.resize_foreground(
        image_rgba, foreground_ratio, out_size=(COND_WIDTH, COND_HEIGHT)
    )


def generate_mesh_bytes(
    input_image: Image.Image,
    texture_size: int,
    remesh_option: str,
    vertex_count: int,
) -> bytes:
    """Generate a 3D mesh and return the GLB bytes."""
    start = time.time()
    device_name = str(device)

    if torch.cuda.is_available():
        torch.cuda.reset_peak_memory_stats()

    with torch.no_grad():
        with (
            torch.autocast(device_type=device_name, dtype=torch.bfloat16)
            if "cuda" in device_name
            else nullcontext()
        ):
            model_batch = create_batch(input_image)
            model_batch = {key: value.to(device) for key, value in model_batch.items()}
            trimesh_mesh, _glob_dict = model.generate_mesh(
                model_batch, texture_size, remesh_option, vertex_count
            )
            trimesh_mesh = trimesh_mesh[0]

    with tempfile.NamedTemporaryFile(delete=False, suffix=".glb") as tmp_file:
        tmp_path = Path(tmp_file.name)

    try:
        trimesh_mesh.export(tmp_path, file_type="glb", include_normals=True)
        glb_bytes = tmp_path.read_bytes()
    finally:
        if tmp_path.exists():
            tmp_path.unlink()

    if torch.cuda.is_available():
        peak_mb = torch.cuda.max_memory_allocated() / 1024 / 1024
        print(f"Peak Memory: {peak_mb:.2f} MB")

    duration = time.time() - start
    print(f"Generation took: {duration:.2f}s")
    return glb_bytes


def read_uploaded_image(contents: bytes) -> Image.Image:
    try:
        image = Image.open(io.BytesIO(contents))
        image.load()
        return image
    except Exception as exc:
        raise HTTPException(status_code=400, detail=f"Invalid image payload: {exc}") from exc


def normalize_generation_settings(
    texture_size: int | None,
    vertex_count: int | None,
) -> tuple[int, int]:
    normalized_texture_size = texture_size or DEFAULT_TEXTURE_SIZE
    normalized_vertex_count = DEFAULT_VERTEX_COUNT if vertex_count is None else vertex_count
    return normalized_texture_size, normalized_vertex_count


def compute_request_hash(
    image_bytes: bytes,
    foreground_ratio: float,
    texture_size: int,
    remesh_option: str,
    vertex_count: int,
) -> str:
    payload = json.dumps(
        {
            "foreground_ratio": foreground_ratio,
            "texture_size": texture_size,
            "remesh_option": remesh_option,
            "vertex_count": vertex_count,
            "profile": RUNTIME_PROFILE,
        },
        sort_keys=True,
    ).encode("utf-8")

    digest = sha256()
    digest.update(payload)
    digest.update(image_bytes)
    return digest.hexdigest()


def get_cache_path(request_hash: str) -> Path:
    return CACHE_DIR / f"{request_hash}.glb"


def load_cached_glb(request_hash: str) -> bytes | None:
    if not CACHE_ENABLED:
        return None

    cache_path = get_cache_path(request_hash)
    if not cache_path.exists():
        return None

    metrics["cache_hits"] += 1
    return cache_path.read_bytes()


def save_cached_glb(request_hash: str, glb_bytes: bytes) -> None:
    if not CACHE_ENABLED:
        return

    cache_path = get_cache_path(request_hash)
    if cache_path.exists():
        return

    tmp_path = cache_path.with_suffix(".tmp")
    tmp_path.write_bytes(glb_bytes)
    tmp_path.replace(cache_path)


def build_response_headers(
    cached: bool,
    request_hash: str,
    duration_seconds: float,
) -> dict[str, str]:
    return {
        "Content-Disposition": "attachment; filename=model.glb",
        "Access-Control-Expose-Headers": "Content-Disposition",
        "X-ScanSpace-Cache": "hit" if cached else "miss",
        "X-ScanSpace-Request-Hash": request_hash,
        "X-ScanSpace-Profile": RUNTIME_PROFILE,
        "X-ScanSpace-Duration": f"{duration_seconds:.3f}",
    }


async def get_glb_bytes(
    image_bytes: bytes,
    foreground_ratio: float,
    texture_size: int,
    remesh_option: str,
    vertex_count: int,
) -> tuple[bytes, str, bool, float]:
    request_hash = compute_request_hash(
        image_bytes, foreground_ratio, texture_size, remesh_option, vertex_count
    )
    metrics["last_request_hash"] = request_hash

    request_start = time.time()
    cached_glb = load_cached_glb(request_hash)
    if cached_glb is not None:
        duration_seconds = time.time() - request_start
        metrics["last_generation_seconds"] = round(duration_seconds, 3)
        return cached_glb, request_hash, True, duration_seconds

    metrics["cache_misses"] += 1

    async with generation_lock:
        cached_glb = load_cached_glb(request_hash)
        if cached_glb is not None:
            duration_seconds = time.time() - request_start
            metrics["last_generation_seconds"] = round(duration_seconds, 3)
            return cached_glb, request_hash, True, duration_seconds

        def run_pipeline() -> bytes:
            input_image = read_uploaded_image(image_bytes)
            processed_image = process_image(input_image, foreground_ratio)
            return generate_mesh_bytes(
                processed_image,
                texture_size=texture_size,
                remesh_option=remesh_option,
                vertex_count=vertex_count,
            )

        glb_bytes = await asyncio.to_thread(run_pipeline)
        save_cached_glb(request_hash, glb_bytes)

    duration_seconds = time.time() - request_start
    metrics["last_generation_seconds"] = round(duration_seconds, 3)
    return glb_bytes, request_hash, False, duration_seconds


async def validate_upload(image: UploadFile) -> bytes:
    if not image.content_type or not image.content_type.startswith("image/"):
        raise HTTPException(
            status_code=400,
            detail="Invalid file type. Upload an image such as PNG or JPEG.",
        )

    contents = await image.read()
    if not contents:
        raise HTTPException(status_code=400, detail="Uploaded image is empty.")

    if len(contents) > MAX_UPLOAD_SIZE_BYTES:
        raise HTTPException(
            status_code=413,
            detail=f"File too large. Max size is {MAX_UPLOAD_SIZE_MB} MB.",
        )

    return contents


@app.get("/")
async def root():
    return {
        "status": "ok",
        "service": "Scan Space Stable Fast 3D API",
        "profile": RUNTIME_PROFILE,
        "device": device,
    }


@app.get("/health")
async def health():
    return {
        "status": "ok",
        "service": "Scan Space Stable Fast 3D API",
        "profile": RUNTIME_PROFILE,
        "defaults": {
            "texture_size": DEFAULT_TEXTURE_SIZE,
            "vertex_count": DEFAULT_VERTEX_COUNT,
        },
        "auth_required": bool(EXPECTED_BEARER_TOKEN),
        "cache": {
            "enabled": CACHE_ENABLED,
            "directory": str(CACHE_DIR),
            "entries": len(list(CACHE_DIR.glob("*.glb"))) if CACHE_ENABLED else 0,
        },
        "gpu": get_gpu_info(),
        "metrics": metrics,
    }


@app.post("/generate")
async def generate_3d_model(
    request: Request,
    image: UploadFile = File(..., description="Input image file (PNG, JPG, JPEG)"),
    foreground_ratio: float = Query(
        0.85, ge=0.5, le=1.0, description="Foreground ratio (0.5-1.0)"
    ),
    texture_size: int = Query(
        None,
        ge=256,
        le=2048,
        description="Texture resolution, default chosen from runtime profile",
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
    require_bearer_token(request)
    metrics["generate_requests_total"] += 1

    normalized_texture_size, normalized_vertex_count = normalize_generation_settings(
        texture_size, vertex_count
    )

    try:
        image_bytes = await validate_upload(image)
        glb_bytes, request_hash, cached, duration_seconds = await get_glb_bytes(
            image_bytes=image_bytes,
            foreground_ratio=foreground_ratio,
            texture_size=normalized_texture_size,
            remesh_option=remesh_option,
            vertex_count=normalized_vertex_count,
        )

        metrics["last_error"] = None
        metrics["successful_generations"] += 1
        return Response(
            content=glb_bytes,
            media_type="model/gltf-binary",
            headers=build_response_headers(cached, request_hash, duration_seconds),
        )
    except HTTPException:
        metrics["failed_generations"] += 1
        raise
    except Exception as exc:
        metrics["failed_generations"] += 1
        metrics["last_error"] = str(exc)
        print(f"Error generating model: {exc}")
        raise HTTPException(status_code=500, detail=f"Error generating model: {exc}") from exc


@app.post("/generate/base64")
async def generate_3d_model_base64(
    request: Request,
    image: UploadFile = File(..., description="Input image file (PNG, JPG, JPEG)"),
    foreground_ratio: float = Query(0.85, ge=0.5, le=1.0),
    texture_size: int = Query(None, ge=256, le=2048),
    remesh_option: str = Query("none", pattern="^(none|triangle|quad)$"),
    vertex_count: int = Query(None, ge=-1, le=50000),
):
    require_bearer_token(request)
    metrics["generate_requests_total"] += 1

    normalized_texture_size, normalized_vertex_count = normalize_generation_settings(
        texture_size, vertex_count
    )

    try:
        image_bytes = await validate_upload(image)
        glb_bytes, request_hash, cached, duration_seconds = await get_glb_bytes(
            image_bytes=image_bytes,
            foreground_ratio=foreground_ratio,
            texture_size=normalized_texture_size,
            remesh_option=remesh_option,
            vertex_count=normalized_vertex_count,
        )

        metrics["last_error"] = None
        metrics["successful_generations"] += 1
        return {
            "success": True,
            "cached": cached,
            "request_hash": request_hash,
            "duration_seconds": round(duration_seconds, 3),
            "format": "glb",
            "model_base64": base64.b64encode(glb_bytes).decode("utf-8"),
        }
    except HTTPException:
        metrics["failed_generations"] += 1
        raise
    except Exception as exc:
        metrics["failed_generations"] += 1
        metrics["last_error"] = str(exc)
        print(f"Error generating model: {exc}")
        raise HTTPException(status_code=500, detail=f"Error generating model: {exc}") from exc


if __name__ == "__main__":
    import uvicorn

    print("Starting Scan Space Stable Fast 3D API server...")
    print(f"Binding to {HOST}:{PORT}")
    uvicorn.run(
        app,
        host=HOST,
        port=PORT,
        timeout_keep_alive=120,
    )
