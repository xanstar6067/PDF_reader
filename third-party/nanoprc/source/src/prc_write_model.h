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

#ifndef PRC_WRITE_MODEL_H
#define PRC_WRITE_MODEL_H

#include "prc_write_common.h"
#include "prc_data.h"
#include "prc_bit.h"
#include "prc_write_global.h"
#include "prc_write_tree.h"
#include "prc_write_file_structure.h"

/* Number of entries prc_write_prc_file puts in a file structure's
   section_offset[] table: [0] file-struct-header (implicit, not counted by
   the parser's own section_count semantics the same way -- see
   prc_write_prc_file's comment) + schema_globals + tree + tessellation +
   geometry + extra_geometry. Shared here (rather than as a local literal
   inside prc_write_prc_file) so anything that needs to independently
   recompute the main header's byte layout -- e.g. a test reading
   start_offset/end_offset back out of a written file -- uses the exact
   same value the writer does instead of hardcoding it a second time. */
#define PRC_WRITE_PRC_FILE_SECTION_COUNT 6u

/* Byte positions of the main prc_header's dynamic fields (Table 5/35,
   single-file-structure layout), computed once so prc_write_main_header_
   bytes (the writer) and any independent reader (tests, diagnostics) agree
   by construction instead of by two people counting bytes the same way. */
typedef struct prc_write_main_header_layout_s
{
    size_t total_size;               /* total header size in bytes */
    size_t section_offset_table_pos; /* byte offset of file_info[0].section_offset[0] */
    size_t start_offset_pos;         /* byte offset of the model section's start_offset field */
    size_t end_offset_pos;           /* byte offset of the model section's end_offset field */
} prc_write_main_header_layout;

/* Fills `out` with the byte layout of a single-file-structure main header
   whose one file structure has `section_count` entries in its
   section_offset[] table. */
void prc_write_main_header_compute_layout(uint32_t section_count, prc_write_main_header_layout *out);

/* PRC_TYPE_ASM_ModelFile (Table 36) section content: the "model" section,
   addressed via the main header's start_offset/end_offset pair rather than
   the per-file-structure section_offset[] table. Its
   ProductOccurrenceReference.unique_id is a FAR REFERENCE to the file
   structure that owns the actual root product occurrence -- NOT the root
   product's own ContentPRCRefBase.unique_id (confirmed the hard way: an
   external reader failed to resolve a reference written as the latter).
   Always written {PRC_WRITE_FILE_STRUCT_UID0,0,0,0} here, matching the
   single file structure this write facility ever produces, whose own
   identity (main header's file_info[0].unique_id and the file-structure-
   header's unique_id_file) is likewise always that same value -- all
   non-zero, per prc_write_common.h's PRC_WRITE_FILE_STRUCT_UID0 doc
   comment (zero was confirmed to be rejected as a null/invalid sentinel
   by at least one external reader). This codebase's own reader
   (prc_api_prep_model_tree) does not actually consult this reference --
   it just takes the last product of the last file structure's products[]
   array -- but other PRC consumers do, so it is still written correctly.
   root_biased_index is ProductOccurrenceReference.root_index: a BIASED
   (1-based, 0 = none) index into the referenced file structure's
   products[] array, like every other cross-reference in this format --
   pass prc_write_tree_to_stream's *root_biased_index_out. model_name is
   the model file's own base.name (NULL for unnamed, in which case
   nanoPRC's own reader falls back to the literal name "model" -- see
   prc_parse_model_file). file_struct_count is always 1 in this write
   facility (single-file-structure PRC, no cross-file references). */
int prc_write_model_file_to_stream(prc_context *ctx, prc_bit_write_state *s,
    const char *model_name, uint32_t root_biased_index, uint32_t file_struct_count);

/* Same encoding as prc_write_model_file_to_stream, but the
   ProductOccurrenceReference.unique_id far-reference is the caller-supplied
   4-tuple `file_struct_uid` instead of the hardcoded
   {PRC_WRITE_FILE_STRUCT_UID0,0,0,0} -- for diagnostics that need to point
   this write facility's model-file encoding at a file structure this
   library did not itself write (e.g. splicing this section into an
   otherwise-real, independently-produced PRC stream to isolate whether the
   model-file section's own bit encoding, not its referenced content, is
   what a given reader objects to). */
int prc_write_model_file_to_stream_ex(prc_context *ctx, prc_bit_write_state *s,
    const char *model_name, uint32_t root_biased_index, uint32_t file_struct_count,
    const uint32_t file_struct_uid[4]);

/* Top-level orchestration: assembles a complete, single-file-structure PRC
   byte stream into one heap buffer (caller frees with prc_free). Encodes
   globals/tree/tessellation independently, deflates each (plus the
   ASM_ModelFile section), then -- since every section's compressed size is
   known before anything is written -- computes the whole file's layout up
   front and writes it in a single forward pass: main header -> file-
   structure header -> schema+globals section -> tree section ->
   tessellation section -> geometry section -> model section (last). The
   model section is deliberately placed LAST, immediately after every
   regular section: confirmed against a real PRC stream (extracted from
   examples/cube.pdf) that this is the actual convention, and at least one
   third-party reader relies on the regular sections being contiguous up to
   the model section's start_offset rather than merely trusting each
   section's own stored offset. This is the one real encoder; both
   prc_write_prc_file (below) and the PDF-embedding path build on it rather
   than re-deriving the section layout independently. `tables` is mutated:
   prc_write_add_default_style adds one default gray material/style to it
   before the globals section is written, so every part/product/rep-item
   in the tree can reference a real style instead of leaving the file
   with a completely empty style table (see prc_write_tree_to_stream's
   default_biased_style_index doc comment for why). */
int prc_write_prc_buffer(prc_context *ctx,
    const char *model_name,
    prc_write_global_tables *tables,
    const prc_write_tree_node *root,
    const prc_write_tess_entry *tess_entries, uint32_t num_tess_entries,
    uint8_t **out_buf, size_t *out_size);

/* Thin wrapper: prc_write_prc_buffer, then a single fwrite to `filename`. */
int prc_write_prc_file(prc_context *ctx, const char *filename,
    const char *model_name,
    prc_write_global_tables *tables,
    const prc_write_tree_node *root,
    const prc_write_tess_entry *tess_entries, uint32_t num_tess_entries);

#endif
