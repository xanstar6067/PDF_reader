# Tesseract OCR resources

The NuGet lock file pins the `Tesseract` .NET wrapper/native Windows binaries to 5.2.0 (Charles Weld). The x64 binaries contain Tesseract 5.2.0 and Leptonica 1.82.0. Microsoft Visual C++ Redistributable x64 2019 or newer is a runtime prerequisite. No OCR resources are downloaded during build or recognition.

- Wrapper: https://github.com/charlesw/tesseract, Apache-2.0, `LICENSE-wrapper.txt`.
- Engine: https://github.com/tesseract-ocr/tesseract/tree/5.2.0, Apache-2.0, `LICENSE-tesseract.txt`.
- Leptonica: https://github.com/DanBloomberg/leptonica/tree/1.82.0, BSD-2-Clause, `LICENSE-leptonica.txt`.
- English and Russian LSTM models: https://github.com/tesseract-ocr/tessdata_fast/tree/87416418657359cb625c412a48b6e1d6d41c29bd, Apache-2.0, `LICENSE-tessdata.txt`.
- Glyphless PDF font: https://github.com/tesseract-ocr/tesseract/blob/5.2.0/tessdata/pdf.ttf, distributed with Tesseract under Apache-2.0.

Bundled files and SHA256:

| File | SHA256 |
| --- | --- |
| tessdata/eng.traineddata | 7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2 |
| tessdata/rus.traineddata | E16E5E036CCE1D9EC2B00063CF8B54472625B9E14D893A169E2B0DEDEB4DF225 |
| tessdata/pdf.ttf | C7845420925A23D88ED830A63957B8AF85A66A8DAF8D9FC90E843673B2EF1A59 |

The font is used exclusively by the OCR engine to create invisible Unicode text in a text-only PDF. PaperDesk imports that layer as a Form XObject without replacing or re-encoding the original page image. Additional language packs are explicitly imported by the user, validated in the isolated OCR process, and stored outside the installation in `%LOCALAPPDATA%/PaperDesk/tessdata`. Their licenses remain those of their respective distributors.

Notices for the image codecs embedded in Leptonica are in `licenses/`: libjpeg-turbo 2.1.4 (IJG/BSD/zlib; this software is based in part on the work of the Independent JPEG Group), libtiff 4.4.0, libpng, zlib 1.2.13 and liblzma (public domain, from XZ Utils). Upstream license snapshots come from libjpeg-turbo 2.1.4, libtiff v4.4.0, libpng v1.6.38, zlib v1.2.13 and XZ Utils v5.2.7; snapshot versions do not identify the libpng/liblzma binary versions. The package's binaries are redistributed unmodified.
