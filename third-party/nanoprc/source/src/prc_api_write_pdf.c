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

/* Implementation of the public entry point declared in include/prc_api.h.
   The only embedding path is the standard (ISO 32000) 3D annotation --
   see prc_write_pdf_3d.c for the object graph. */

#include "../include/prc_api.h"
#include "prc_write_pdf_3d.h"

int
prc_api_pdf_embed_prc(prc_context *ctx, const char *pdf_path,
    const uint8_t *prc_data, size_t prc_size, const prc_pdf_write_options *options)
{
    if (ctx == NULL || pdf_path == NULL || prc_data == NULL || prc_size == 0)
    {
        prc_error(ctx, PRC_ERROR_INTERNAL, "prc_api_pdf_embed_prc: invalid arguments\n");
        return PRC_ERROR_INTERNAL;
    }

    return prc_write_pdf_3d_annotation(ctx, pdf_path, prc_data, prc_size, options);
}
