# /// script
# requires-python = ">=3.10"
# ///
"""Describe a Unity asset thumbnail using a local vision-language model.

Supported engines:
  blip  — Salesforce/blip-image-captioning-base (default, ~990 MB on first run)
  clip  — openai/clip-vit-base-patch32 zero-shot classification (~600 MB)

Security: Model revisions are pinned to specific commit SHAs to prevent
supply-chain attacks via compromised HuggingFace uploads.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

# ── Pinned model revisions ──────────────────────────────────────────
# Pin to exact commit SHAs so that a compromised HF account cannot
# silently swap model weights. Update these when upgrading models.
_BLIP_MODEL_ID = "Salesforce/blip-image-captioning-base"
_BLIP_REVISION = "82a37760796d32b1411fe092ab5d4e227313294b"

_CLIP_MODEL_ID = "openai/clip-vit-base-patch32"
_CLIP_REVISION = "3d74acf9a28c67741b2f4f2ea7635f0aaf6f0268"


def _is_model_cached(model_id: str, revision: str) -> bool:
    """Check if a model revision is already in the HuggingFace cache."""
    hf_home = os.environ.get("HF_HOME", Path.home() / ".cache" / "huggingface")
    snapshot_dir = (
        Path(hf_home)
        / "hub"
        / f"models--{model_id.replace('/', '--')}"
        / "snapshots"
        / revision
    )
    return snapshot_dir.is_dir()


def _load_kwargs(model_id: str, revision: str) -> dict:
    """Return from_pretrained kwargs: use local_files_only when cached."""
    kwargs: dict = {"revision": revision}
    if _is_model_cached(model_id, revision):
        kwargs["local_files_only"] = True
    return kwargs


def describe_blip(image_path: str, max_words: int) -> dict:
    from transformers import BlipProcessor, BlipForConditionalGeneration
    from PIL import Image

    kw = _load_kwargs(_BLIP_MODEL_ID, _BLIP_REVISION)
    processor = BlipProcessor.from_pretrained(_BLIP_MODEL_ID, **kw)
    model = BlipForConditionalGeneration.from_pretrained(_BLIP_MODEL_ID, **kw)

    image = Image.open(image_path).convert("RGB")
    inputs = processor(image, return_tensors="pt")
    out = model.generate(**inputs, max_new_tokens=max_words)
    caption = processor.decode(out[0], skip_special_tokens=True).strip()

    return {
        "description": caption,
        "engine": "blip",
        "model": f"{_BLIP_MODEL_ID}@{_BLIP_REVISION[:8]}",
    }


# Descriptive label set covering common Unity asset types.
_CLIP_CANDIDATE_LABELS = [
    "a 2D sprite character",
    "a 2D sprite tileset",
    "a UI button or icon",
    "a UI panel or dialog",
    "a 3D model or mesh",
    "a landscape or environment texture",
    "a material or shader preview",
    "a particle effect",
    "a skybox or HDR environment",
    "a font or text glyph",
    "pixel art",
    "a photo or photographic texture",
    "an audio waveform visualization",
    "an animation or sprite sheet",
    "a normal map or height map",
    "a logo or branding asset",
    "an abstract pattern or noise texture",
]


def describe_clip(image_path: str, max_words: int) -> dict:
    from transformers import CLIPProcessor, CLIPModel
    from PIL import Image
    import torch

    kw = _load_kwargs(_CLIP_MODEL_ID, _CLIP_REVISION)
    processor = CLIPProcessor.from_pretrained(_CLIP_MODEL_ID, **kw)
    model = CLIPModel.from_pretrained(_CLIP_MODEL_ID, **kw)

    image = Image.open(image_path).convert("RGB")
    inputs = processor(
        text=_CLIP_CANDIDATE_LABELS,
        images=image,
        return_tensors="pt",
        padding=True,
    )

    with torch.no_grad():
        outputs = model(**inputs)

    logits = outputs.logits_per_image[0]
    probs = logits.softmax(dim=-1)
    top3 = probs.topk(3)

    labels = []
    for idx, score in zip(top3.indices.tolist(), top3.values.tolist()):
        labels.append({"label": _CLIP_CANDIDATE_LABELS[idx], "score": round(score, 4)})

    description = ", ".join(entry["label"] for entry in labels)
    return {
        "description": description,
        "engine": "clip",
        "model": f"{_CLIP_MODEL_ID}@{_CLIP_REVISION[:8]}",
        "top_labels": labels,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Describe an image asset locally.")
    parser.add_argument("--image", required=True, help="Path to the thumbnail PNG")
    parser.add_argument(
        "--engine",
        choices=["blip", "clip"],
        default="blip",
        help="Captioning engine (default: blip)",
    )
    parser.add_argument(
        "--max-words",
        type=int,
        default=20,
        help="Maximum words/tokens in the description (default: 20)",
    )
    parser.add_argument(
        "--output",
        choices=["json", "text"],
        default="json",
        help="Output format (default: json)",
    )
    args = parser.parse_args()

    if not Path(args.image).is_file():
        error = {"error": f"image not found: {args.image}"}
        json.dump(error, sys.stdout)
        sys.exit(1)

    try:
        if args.engine == "blip":
            result = describe_blip(args.image, args.max_words)
        else:
            result = describe_clip(args.image, args.max_words)
    except Exception as exc:
        error = {"error": str(exc), "engine": args.engine}
        json.dump(error, sys.stdout)
        sys.exit(1)

    if args.output == "text":
        print(result["description"])
    else:
        json.dump(result, sys.stdout)


if __name__ == "__main__":
    main()
