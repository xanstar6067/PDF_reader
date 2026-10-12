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

#ifndef PRC_PARSE_TREE_H
#define PRC_PARSE_TREE_H

#include "prc_data.h"
#include "prc_bit.h"

int prc_parse_parts(prc_context *ctx, prc_bit_state *bit_state, prc_asm_parts_definition *data);
int prc_parse_product_occurrence(prc_context *ctx, prc_bit_state *bit_state, prc_asm_product_occurrence *data);

#endif

