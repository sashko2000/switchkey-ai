# Third-party components

| Component | License | Used for |
|---|---|---|
| OpenVINO, OpenVINO GenAI, OpenVINO Tokenizers (Intel) | Apache License 2.0 | running the model on Intel NPU/GPU/CPU (`openvino*.dll`) |
| oneTBB (Intel) | Apache License 2.0 | `tbb*.dll` |
| llama.cpp (ggml-org) | MIT | the model on the GPU (Vulkan: AMD Radeon, Intel Arc; CUDA: NVIDIA) and CPU (`llama-server.exe`): AMD processors, GGUF-only models, NVIDIA cards |
| NVIDIA CUDA runtime libraries (`cudart`, `cublas`) | NVIDIA CUDA Toolkit EULA (redistributable) | NVIDIA cards: shipped by ggml-org together with llama.cpp for CUDA |
| ryzenai-server (lemonade-sdk); Ryzen AI libraries (AMD) | MIT; AMD Software End User License Agreement | AMD processors: the model on the XDNA 2 NPU |
| .NET Runtime (Microsoft) | MIT | the app runtime |
| Microsoft Visual C++ Runtime | Microsoft Visual C++ Redistributable license | installed by Microsoft's official installer when needed |
| FrequencyWords, Hermit Dave (https://github.com/hermitdave/FrequencyWords) | CC BY-SA 4.0 | frequency dictionaries `Resources/en.txt`, `ru.txt`, `uk.txt`, `hi.txt` (filtered: letters only, ё replaced with е; obviously Russian words removed from the Ukrainian list) |
| Qwen3, Qwen3.5, Qwen3.8, Qwen2.5 (Alibaba Cloud) | Apache 2.0 | models downloaded at the user's choice (OpenVINO, GGUF and AMD ONNX versions) |
| Gemma 4 (Google) | Apache 2.0 | models downloaded at the user's choice (GGUF, quantized by Unsloth) |

OpenVINO, llama.cpp, the CUDA libraries, ryzenai-server, the models and (for conversion) Python with Intel's tools are not part of the app:
they are downloaded from their publishers when the user turns on the language model in settings.
