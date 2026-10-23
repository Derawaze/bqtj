# Third-Party Notices

This repository references the following direct dependencies. License identifiers were read from the installed NuGet package metadata for the versions listed below. Transitive dependencies remain subject to their own package licenses.

## Runtime dependencies

| Package | Version | License |
| --- | --- | --- |
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| Microsoft.Data.Sqlite | 10.0.12 | MIT |
| Microsoft.Extensions.DependencyInjection | 10.0.12 | MIT |
| Microsoft.Extensions.Logging | 10.0.12 | MIT |
| Serilog.Extensions.Logging | 10.0.0 | Apache-2.0 |
| Serilog.Sinks.File | 7.0.0 | Apache-2.0 |

## Build and test dependencies

| Package | Version | License |
| --- | --- | --- |
| coverlet.collector | 6.0.4 | MIT |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT |
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 |

This project does not redistribute Flash Player, game SWF files, Speedhack/GameSpeed DLLs, or binaries from third-party launchers. Script recognition includes only cropped item icons documented in resources/automation/sky-top/README.md; those game images remain attributed to their original rights holders and are not authored by this project. Windows system libraries and the locally installed Flash ActiveX runtime are not bundled in the release archive.

Before a public release, the maintainer should retain the corresponding package license files in the release compliance record and re-run this inventory whenever dependency versions change.
## Optional script vision component

The separately delivered components/automation package dynamically loads MaaFramework 5.14.2 (LGPL-3.0), OpenCV (Apache-2.0), ONNX Runtime (MIT), and FastDeploy/PaddleOCR (Apache-2.0). It uses the Chinese PP-OCRv4 model from MaaCommonAssets at commit dabcd4681ac990dc4361de26416d986abd80e4aa. License texts accompany that component in licenses/, together with the project's GPL-3.0 LICENSE and the model provenance README.

- MaaFramework source and corresponding version: https://github.com/MaaXYZ/MaaFramework/tree/v5.14.2
- OpenCV: https://github.com/opencv/opencv
- ONNX Runtime: https://github.com/microsoft/onnxruntime
- FastDeploy: https://github.com/PaddlePaddle/FastDeploy
- PaddleOCR/model provenance: https://github.com/MaaXYZ/MaaCommonAssets/tree/dabcd4681ac990dc4361de26416d986abd80e4aa/OCR/ppocr_v4/zh_cn

Microsoft Visual C++ v14 redistributable runtime DLLs are included only in the optional component for application-local deployment and retain Microsoft's terms. Native worker code is in native/ScriptVision and remains dynamically linked to the libraries; an interface-compatible library can be replaced. The base launcher does not bundle these vision libraries. No third-party game script EXE, DLL, Flash Player or game asset is included.
