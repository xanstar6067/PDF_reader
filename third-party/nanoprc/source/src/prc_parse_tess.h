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

#ifndef PRC_PARSE_TESS_H
#define PRC_PARSE_TESS_H

#include "prc_data.h"
#include "prc_bit.h"

int prc_parse_tess(prc_context *ctx, prc_bit_state *bit_state, prc_tess *data, uint8_t debug_tess);
int prc_parse_tess_3d_compressed(prc_context *ctx, prc_bit_state *bit_state, prc_tess_3d_compressed **data_in, uint8_t debug_tess);
int prc_parse_tess_3d(prc_context *ctx, prc_bit_state *bit_state, prc_tess_3d **data_in);
int prc_parse_tess_3d_wire(prc_context *ctx, prc_bit_state *bit_state, prc_tess_3d_wire **data_in);

#endif
