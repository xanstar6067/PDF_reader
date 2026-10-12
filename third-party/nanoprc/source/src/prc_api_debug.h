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

#ifndef PRC_API_DEBUG_H
#define PRC_API_DEBUG_H

#include "../include/prc_api.h"

/* prc_api_print_tree and prc_api_print_error_stack are part of the public
   API surface and remain declared (with full doxygen) in prc_api.h; the
   declarations below just let prc_api_debug.c itself, and any other
   translation unit that only wants the debug/print surface, see them
   without pulling in the rest of prc_api.h's PRC_EXPORT-annotated set here.

   prc_api_print_tree_prc, prc_api_print_dots, and prc_api_print_tree_ri_api
   remain static to prc_api_debug.c, exactly as they were static to
   prc_api.c before the move, so they are intentionally not declared here. */
PRC_EXPORT void prc_api_print_tree(prc_context *ctx, prc_api_product *product, int level);
PRC_EXPORT void prc_api_print_error_stack(prc_context *ctx);

#endif
