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

#ifndef PRC_MARKUP_DECODE_H
#define PRC_MARKUP_DECODE_H

#include "prc_data.h"

typedef enum {
    PRC_MARKUPTYPE_IS_UNKNOWN = 0,
    PRC_MARKUPTYPE_IS_MATRIX = 1,
    PRC_MARKUPTYPE_IS_EXTRA_DATA = 2,
    PRC_MARKUPTYPE_IS_POLYLINE = 3,
    PRC_MARKUPTYPE_IS_TEXT = 4
} prc_markup_type_t;

/* Table 150 Table of Entities */
typedef enum {
    PRC_PATTERN_ENTITY = 0,
    PRC_PICTURE_ENTITY = 1,
    PRC_TRIANGLES_ENTITY = 2,
    PRC_QUADS_ENTITY = 3,
    PRC_FACE_VIEW_MODEL = 6,
    PRC_FRAME_DRAW_MODEL = 7,
    PRC_FIXED_SIZE_MODEL = 8,
    PRC_SYMBOL_ENTITY = 9,
    PRC_CYLINDER_ENTITY = 10,
    PRC_COLOR_ENTITY = 11,
    PRC_LINE_STIPPLE_ENTITY = 12,
    PRC_FONT_ENTITY = 13,
    PRC_TEXT_ENTITY = 14,
    PRC_POINTS_ENTITY = 15,
    PRC_POLYGON_ENTITY = 16,
    PRC_LINEWIDTH_ENTITY = 17,
    PRC_POLYLINE_ENTITY = 18 // Added for ease of coding

} prc_markup_entity_t;

/* We have a stack that defines maintains the mode that we are currently in.
   Note that the matrix is stored in the state and does not interfere with this */
typedef struct prc_markup_mode_stack_s prc_markup_mode_stack;
struct prc_markup_mode_stack_s
{
    prc_markup_block_mode_t mode;
    prc_markup_mode_stack *next;
};

/* Structure that contains the markup state which includes current line width and
   the transformation matrix */
typedef struct prc_markup_state_s prc_markup_state;
struct prc_markup_state_s
{
    double line_width;
    double matrix[16];
    uint8_t is_identity;
    prc_markup_state *next;
};

int prc_decode_compressed_tess(prc_context *ctx, prc_tess_3d_compressed *data, uint8_t debug_tess);
int prc_compressed_tess_apply_crease_angle(prc_context *ctx, prc_tess_3d_compressed *data);
int prc_decode_markup_tess(prc_context *ctx, prc_tess_markup *data);

#endif

