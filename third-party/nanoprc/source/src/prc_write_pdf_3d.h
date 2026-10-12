/* Copyright (C) 2023-2026 CascadiaVoxel LLC

    nanoPRC is free software: you can redistribute it and/or modify it under
    the terms of the GNU Affero General Public License as published by the
    Free Software Foundation, either version 3 of the License, or (at your
    option) any later version.

    nanoPRC is distributed in the hope that it will be useful, but WITHOUT
    ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
    FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public
    License for more details.

    You should have received a copy of the GNU Affero General Public License
    along with nanoPRC. If not, see <https://www.gnu.org/licenses/>.
*/

#ifndef PRC_WRITE_PDF_3D_H
#define PRC_WRITE_PDF_3D_H

#include <stdint.h>
#include <stddef.h>
#include "prc_context.h"
#include "../include/prc_api.h"

/* Writes a complete, minimal, single-page PDF 1.7 file to `pdf_path`
   embedding `prc_data`/`prc_size` as a standard 3D annotation. Object
   graph (Catalog/Pages/Page/3D-stream/3DView(s)/Annot/appearance
   XObject) and every dictionary key used is confirmed against a real,
   working 3D PDF (examples/cube.pdf, byte-extracted and inspected
   directly rather than assumed from the spec from memory) -- see
   prc_write_pdf_3d.c's top comment for the exact object graph. */
int prc_write_pdf_3d_annotation(prc_context *ctx, const char *pdf_path,
    const uint8_t *prc_data, size_t prc_size,
    const prc_pdf_write_options *options);

#endif
