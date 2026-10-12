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

/* Implementation of the public write-facility entry point declared in
   include/prc_api.h. This is a thin wrapper: prc_api_write_node,
   prc_api_write_rep_item, and prc_api_write_tessellation are the same
   types the internal encoder (prc_write_tree.c / prc_write_file_structure.c
   / prc_write_model.c) already works with -- prc_write_tree_node etc. are
   typedef aliases of these public types, not separate copies -- so no
   conversion step is needed here, only supplying the (currently always
   empty) global color/material/style table those internals require. */

#include "../include/prc_api.h"
#include "prc_write_global.h"
#include "prc_write_tree.h"
#include "prc_write_file_structure.h"
#include "prc_write_model.h"

int
prc_api_write_prc_file(prc_context *ctx, const char *filename,
    const char *model_name, const prc_api_write_node *root,
    const prc_api_write_tessellation *tess_entries, uint32_t num_tess_entries)
{
    prc_write_global_tables tables;
    int code;

    if (ctx == NULL || filename == NULL || root == NULL)
    {
        prc_error(ctx, PRC_ERROR_INTERNAL, "prc_api_write_prc_file: invalid arguments\n");
        return PRC_ERROR_INTERNAL;
    }

    if (prc_write_global_tables_init(ctx, &tables) != 0)
        return PRC_ERROR_MEMORY;

    code = prc_write_prc_file(ctx, filename, model_name, &tables, root, tess_entries, num_tess_entries);

    prc_write_global_tables_free(ctx, &tables);
    return code;
}

int
prc_api_write_prc_buffer(prc_context *ctx,
    const char *model_name, const prc_api_write_node *root,
    const prc_api_write_tessellation *tess_entries, uint32_t num_tess_entries,
    uint8_t **out_buf, size_t *out_size)
{
    prc_write_global_tables tables;
    int code;

    if (ctx == NULL || root == NULL || out_buf == NULL || out_size == NULL)
    {
        prc_error(ctx, PRC_ERROR_INTERNAL, "prc_api_write_prc_buffer: invalid arguments\n");
        return PRC_ERROR_INTERNAL;
    }

    if (prc_write_global_tables_init(ctx, &tables) != 0)
        return PRC_ERROR_MEMORY;

    code = prc_write_prc_buffer(ctx, model_name, &tables, root, tess_entries, num_tess_entries, out_buf, out_size);

    prc_write_global_tables_free(ctx, &tables);
    return code;
}

void
prc_api_write_prc_buffer_free(prc_context *ctx, uint8_t *buf)
{
    prc_free(ctx, buf);
}
