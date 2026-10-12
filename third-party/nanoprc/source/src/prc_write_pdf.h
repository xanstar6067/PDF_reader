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

#ifndef PRC_WRITE_PDF_H
#define PRC_WRITE_PDF_H

#include <stdio.h>
#include <stdint.h>
#include <stddef.h>
#include "prc_context.h"

/* Minimal, narrow PDF 1.7 object/xref producer -- not a general-purpose PDF
   library. It supports exactly what a fixed, small set of page layouts
   needs: sequential indirect objects (allocated up front so any object can
   reference any other regardless of write order), a classic (non-stream)
   cross-reference table, and a single-revision trailer. Every object's
   dictionary text is written directly by its own caller (fprintf-style) --
   there is no generic PDF value/dictionary tree here, deliberately, since
   there are only ever two or three fixed dictionary shapes to emit. */
typedef struct prc_pdf_writer_s
{
    FILE *fid;
    uint32_t next_obj_num;   /* 1-based; object 0 is the free-list head */
    uint32_t *obj_offsets;   /* obj_offsets[i] = byte offset of object i+1's "N 0 obj" */
    uint32_t obj_count;      /* number of object numbers allocated so far */
    uint32_t obj_capacity;
    int error;               /* sticky I/O error flag, checked by the caller at the end */
} prc_pdf_writer;

/* Binds `w` to an already-open, writable `fid` and writes the PDF header
   ("%PDF-1.7" plus the conventional binary-marker comment line). */
int prc_pdf_writer_init(prc_context *ctx, prc_pdf_writer *w, FILE *fid);

/* Frees obj_offsets. Does not close fid (caller owns it). */
void prc_pdf_writer_release(prc_context *ctx, prc_pdf_writer *w);

/* Reserves the next sequential object number, growing obj_offsets if
   needed. Call this for every object up front, before writing any object
   bodies, so forward references (e.g. Pages -> Page, Page -> Annot) can be
   written by number before the referenced object itself exists on disk. */
uint32_t prc_pdf_writer_alloc_obj(prc_context *ctx, prc_pdf_writer *w);

/* Records the current file position as obj_num's offset (for the xref
   table) and writes "<obj_num> 0 obj". Caller writes the dictionary text
   (and, for a stream object, calls prc_pdf_write_stream_body) next, then
   calls prc_pdf_end_obj. */
int prc_pdf_begin_obj(prc_context *ctx, prc_pdf_writer *w, uint32_t obj_num);

/* Writes "endobj\n". */
int prc_pdf_end_obj(prc_context *ctx, prc_pdf_writer *w);

/* Writes "stream\n<data>\nendstream\n". Caller is responsible for having
   already written a dictionary containing "/Length <data_len>" before
   calling this. No PDF-level /Filter is ever applied here -- PRC content
   is already zlib-deflated per section internally; wrapping it again would
   just cost time for no benefit. */
int prc_pdf_write_stream_body(prc_context *ctx, prc_pdf_writer *w,
    const uint8_t *data, size_t data_len);

/* Writes a PDF literal string, including the enclosing parentheses, with
   '\\', '(' and ')' backslash-escaped. Plain ASCII only -- adequate for the
   short producer-supplied names (view/model names) this writer handles;
   not a general PDFDocEncoding/UTF-16 text-string encoder. */
void prc_pdf_write_escaped_string(prc_pdf_writer *w, const char *s);

/* Writes the classic cross-reference table (one 20-byte entry per
   allocated object, all "in use" since this writer never frees/reuses an
   object number) and the trailer, using `root_obj_num` as /Root, and a
   /ID entry (both halves identical, since this writer only ever produces
   a single-revision file, never an incremental update) so the trailer has
   a document identifier -- confirmed present in every real, working 3D
   PDF checked against (examples/cube.pdf and a reference file produced by
   re-converting our own utah_teapot.prc through an independent PRC-to-PDF
   tool), and absent from every one of our own attempts that real readers
   rejected. /ID is technically optional per spec, but apparently not
   practically optional for at least two real 3D-capable readers.
   `info_obj_num` is written as /Info in the trailer if non-zero (an
   indirect reference to a Document Information dictionary object the
   caller has already written), omitted entirely if zero. Must be called
   last, after every object has been written via prc_pdf_begin_obj/
   end_obj. Checks w->error and the underlying FILE's error indicator;
   returns PRC_ERROR_IO on any write failure. */
int prc_pdf_write_xref_and_trailer(prc_context *ctx, prc_pdf_writer *w,
    uint32_t root_obj_num, uint32_t info_obj_num);

#endif
