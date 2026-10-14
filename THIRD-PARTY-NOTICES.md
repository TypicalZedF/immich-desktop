# Third-party notices

Immich Desktop includes or uses the following components.

## LibVLC and LibVLCSharp

Videos are played with [LibVLC](https://www.videolan.org/vlc/libvlc.html) through
[LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp), both by VideoLAN and contributors,
licensed under the GNU Lesser General Public License 2.1 or later. The Windows build ships an
unmodified copy of LibVLC (from the `VideoLAN.LibVLC.Windows` package) as separate libraries in the
`libvlc` folder; on Linux the system's libvlc is used. Source code: https://code.videolan.org/videolan/vlc

## On-device privacy check

The models in `Models/` run only on this PC through [ONNX Runtime](https://onnxruntime.ai)
(Microsoft, MIT license; on Windows with DirectML, Microsoft's redistributable GPU library).

### NudeNet 320n detector (`nudenet-320n.onnx`)

The `320n.onnx` file from [NudeNet](https://github.com/notAI-tech/NudeNet)'s v3.4-weights release, unchanged.
Licensed under the GNU Affero General Public License v3.0, the same license as this app (see [LICENSE](LICENSE)).
Copyright (c) notAI-tech. It is based on Ultralytics YOLOv8 (AGPL-3.0).

### LAION CLIP-based NSFW detector (`nsfw-clip-b32-head.bin`)

The ViT-B/32 detector from [LAION-AI/CLIP-based-NSFW-Detector](https://github.com/LAION-AI/CLIP-based-NSFW-Detector),
exported to plain weights with `tools/models/laion_nsfw_head.py` (the weights are unchanged).

MIT License

Copyright (c) 2022 Christoph Schuhmann

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES
OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

### CLIP ViT-B/32 image model (`clip-vit-b32-visual.onnx`, downloaded on request)

Not bundled. Only when the thorough check is turned on, the app downloads Immich's published copy
([immich-app/ViT-B-32__openai](https://huggingface.co/immich-app/ViT-B-32__openai), `visual/model.onnx`) and checks its
size and SHA-256 before using it. The model is [OpenAI CLIP](https://github.com/openai/CLIP).

MIT License

Copyright (c) 2021 OpenAI

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES
OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

### PaddleOCR PP-OCRv4 text models (`text-detect.onnx`, `text-read.onnx`)

From [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) (PaddlePaddle Authors), as packaged in
ONNX form by [RapidOCR](https://github.com/RapidAI/RapidOCR) (`rapidocr_onnxruntime` 1.4.4).
Licensed under the Apache License, Version 2.0: https://www.apache.org/licenses/LICENSE-2.0.
Unmodified.
