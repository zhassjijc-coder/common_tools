# Third-party components

LinkLab uses Microsoft .NET 10, Windows Presentation Foundation, and System.IO.Ports 10.0.9. These components are distributed under their respective MIT licenses and third-party notices.

- .NET runtime: https://github.com/dotnet/runtime
- WPF: https://github.com/dotnet/wpf
- System.IO.Ports: https://www.nuget.org/packages/System.IO.Ports/10.0.9

The self-contained distribution includes upstream LICENSE.txt and THIRD-PARTY-NOTICES.txt files supplied by the runtime packages. Additional copies of component license and notice files are included under `licenses/` when packaged.

The application uses Windows system fonts; font files are not redistributed. Interface graphics are code-native vector drawings created for this project.

Since 1.3, the system tray uses the Windows Forms NotifyIcon component from Microsoft's .NET Windows Desktop runtime (MIT): https://github.com/dotnet/winforms . The main application UI remains WPF.

## Added in 1.1

| Component | Version / source | License / attribution |
|---|---|---|
| IoTClient | NuGet 1.0.48; https://github.com/zhaopeiym/IoTClient | MIT, Copyright (c) 2019 农码一生 |
| MQTTnet | NuGet 5.2.0.1603; https://github.com/dotnet/MQTTnet | MIT, .NET Foundation and Contributors |
| MQTTnet.Server | NuGet 5.2.0.1603 (tests only, not an app dependency) | MIT, .NET Foundation and Contributors |
| Tesseract .NET wrapper | NuGet 5.2.0; https://github.com/charlesw/tesseract | Apache-2.0, Copyright 2012–2022 Charles Weld |
| Tesseract OCR native engine | 5.2.0 supplied by wrapper package | Apache-2.0, https://github.com/tesseract-ocr/tesseract |
| Leptonica native imaging library | 1.82.0 supplied by wrapper package | BSD-2-Clause, Dan Bloomberg and contributors; https://github.com/DanBloomberg/leptonica |
| tessdata_fast eng + chi_sim | Official models fetched 2026-10-17 from https://github.com/tesseract-ocr/tessdata_fast | Apache-2.0; model file SHA256 values in `licenses/MODELS-SHA256.txt` |
| System.Reflection.Emit | 4.7.0, transitive via Tesseract | MIT, Microsoft .NET |
| libjpeg-turbo | 2.1.4 embedded in the supplied Leptonica DLL | IJG / BSD-3-Clause / zlib; https://github.com/libjpeg-turbo/libjpeg-turbo |
| libpng | 1.6.37 embedded in the supplied Leptonica DLL | PNG Reference Library License; https://github.com/pnggroup/libpng |
| libtiff | 4.4.0 embedded in the supplied Leptonica DLL | TIFF license; https://github.com/libsdl-org/libtiff |
| zlib | 1.2.13 embedded in the supplied Leptonica DLL | zlib license; https://github.com/madler/zlib |

Full license texts are included in `licenses/`, and the OCR model license is also included in `tessdata/LICENSE`. The wrapper includes InteropDotNet (MIT, Copyright 2014 Andrey Akinshin). Its license is included under `licenses/InteropDotNet-MIT.txt`.

This software is based in part on the work of the Independent JPEG Group.

LinkLab calls IoTClient's protected PLC command-builder APIs through small adapters, patches transaction/routing fields and MC packed-bit write counts, and supplies its own asynchronous transport, validators and memory servers. These are community implementations of vendor protocols, not official Siemens/Mitsubishi/Omron SDKs or endorsements. No HslCommunication binary or source is included.

MQTTX and Snipaste are functional references only; their code, graphics and trademarks are not redistributed as part of LinkLab. No network OCR service is used. OCR native libraries may require the separately installed Microsoft Visual C++ x64 runtime; it is not redistributed in this package.

## Optional virtual serial driver (currently hidden)

The virtual serial module is temporarily hidden from the current release. No virtual serial driver is required for the available modules. Its source is retained pending validation of a compatible, permanently free driver that permits commercial use.

The virtual serial module invokes the separately installed com0com `setupc.exe` command-line tool. com0com is an external GPL-2.0-or-later project by Vyacheslav Frolov: https://com0com.sourceforge.net/ . LinkLab does not bundle, copy, link or modify com0com driver binaries or source. Users obtain a compatible driver separately from its upstream releases and retain its license notices. Driver compatibility and Windows signature acceptance depend on the installed release and operating-system configuration.
