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

#include <string.h>
#include <time.h>
#include "prc_write_pdf.h"

#define PRC_PDF_OBJ_CAPACITY_INIT 16u

int
prc_pdf_writer_init(prc_context *ctx, prc_pdf_writer *w, FILE *fid)
{
    if (ctx == NULL || w == NULL || fid == NULL)
    {
        prc_error(ctx, PRC_ERROR_INTERNAL, "prc_pdf_writer_init: invalid arguments\n");
        return PRC_ERROR_INTERNAL;
    }

    memset(w, 0, sizeof(*w));
    w->fid = fid;
    w->next_obj_num = 1;

    w->obj_offsets = (uint32_t *)prc_malloc(ctx, PRC_PDF_OBJ_CAPACITY_INIT * sizeof(uint32_t));
    if (w->obj_offsets == NULL)
    {
        prc_error(ctx, PRC_ERROR_MEMORY, "Allocation error in prc_pdf_writer_init\n");
        return PRC_ERROR_MEMORY;
    }
    w->obj_capacity = PRC_PDF_OBJ_CAPACITY_INIT;

    /* "%PDF-1.7" header, followed by the conventional comment line of four
       bytes >= 0x80 so naive tools that sniff the first few lines treat
       the file as binary rather than text. */
    if (fprintf(fid, "%%PDF-1.7\n%%\xE2\xE3\xCF\xD3\n") < 0)
    {
        w->error = 1;
        prc_error(ctx, PRC_ERROR_IO, "prc_pdf_writer_init: failed to write PDF header\n");
        return PRC_ERROR_IO;
    }

    return 0;
}

void
prc_pdf_writer_release(prc_context *ctx, prc_pdf_writer *w)
{
    if (w == NULL)
        return;
    if (w->obj_offsets != NULL)
        prc_free(ctx, w->obj_offsets);
    w->obj_offsets = NULL;
    w->obj_capacity = 0;
    w->obj_count = 0;
}

uint32_t
prc_pdf_writer_alloc_obj(prc_context *ctx, prc_pdf_writer *w)
{
    uint32_t obj_num;

    if (w == NULL || w->error)
        return 0;

    if (w->obj_count == w->obj_capacity)
    {
        uint32_t new_capacity = w->obj_capacity * 2;
        uint32_t *new_offsets = (uint32_t *)prc_realloc(ctx, w->obj_offsets, new_capacity * sizeof(uint32_t));
        if (new_offsets == NULL)
        {
            prc_error(ctx, PRC_ERROR_MEMORY, "Allocation error in prc_pdf_writer_alloc_obj\n");
            w->error = 1;
            return 0;
        }
        w->obj_offsets = new_offsets;
        w->obj_capacity = new_capacity;
    }

    obj_num = w->next_obj_num++;
    w->obj_offsets[w->obj_count++] = 0; /* filled in by prc_pdf_begin_obj */
    return obj_num;
}

int
prc_pdf_begin_obj(prc_context *ctx, prc_pdf_writer *w, uint32_t obj_num)
{
    long pos;

    if (w == NULL || w->error || obj_num == 0 || obj_num > w->obj_count)
    {
        prc_error(ctx, PRC_ERROR_INTERNAL, "prc_pdf_begin_obj: invalid object number\n");
        return PRC_ERROR_INTERNAL;
    }

    pos = ftell(w->fid);
    if (pos < 0)
    {
        w->error = 1;
        prc_error(ctx, PRC_ERROR_IO, "prc_pdf_begin_obj: ftell failed\n");
        return PRC_ERROR_IO;
    }
    w->obj_offsets[obj_num - 1] = (uint32_t)pos;

    if (fprintf(w->fid, "%u 0 obj", obj_num) < 0)
    {
        w->error = 1;
        prc_error(ctx, PRC_ERROR_IO, "prc_pdf_begin_obj: write failed\n");
        return PRC_ERROR_IO;
    }
    return 0;
}

int
prc_pdf_end_obj(prc_context *ctx, prc_pdf_writer *w)
{
    if (w == NULL || w->error)
        return PRC_ERROR_INTERNAL;

    if (fprintf(w->fid, "endobj\n") < 0)
    {
        w->error = 1;
        prc_error(ctx, PRC_ERROR_IO, "prc_pdf_end_obj: write failed\n");
        return PRC_ERROR_IO;
    }
    return 0;
}

int
prc_pdf_write_stream_body(prc_context *ctx, prc_pdf_writer *w,
    const uint8_t *data, size_t data_len)
{
    if (w == NULL || w->error)
        return PRC_ERROR_INTERNAL;

    if (fprintf(w->fid, "stream\n") < 0)
        goto io_fail;
    if (data_len > 0 && fwrite(data, 1, data_len, w->fid) != data_len)
        goto io_fail;
    if (fprintf(w->fid, "\nendstream\n") < 0)
        goto io_fail;

    return 0;

io_fail:
    w->error = 1;
    prc_error(ctx, PRC_ERROR_IO, "prc_pdf_write_stream_body: write failed\n");
    return PRC_ERROR_IO;
}

void
prc_pdf_write_escaped_string(prc_pdf_writer *w, const char *s)
{
    const char *p;

    if (w == NULL || w->error)
        return;

    fputc('(', w->fid);
    if (s != NULL)
    {
        for (p = s; *p != '\0'; p++)
        {
            if (*p == '\\' || *p == '(' || *p == ')')
                fputc('\\', w->fid);
            fputc((unsigned char)*p, w->fid);
        }
    }
    fputc(')', w->fid);
}

/* Fills `id16` with 16 bytes that are unique enough for a trailer /ID
   (not a security token -- just needs to look like a real document
   identifier). Mixes the current time with writer state that varies by
   file content (object count, xref position) so two files written in the
   same process/second still don't collide. */
static void
prc_pdf_generate_id(const prc_pdf_writer *w, uint32_t root_obj_num, long xref_pos, uint8_t id16[16])
{
    uint32_t state = (uint32_t)time(NULL) ^ (w->obj_count * 2654435761u)
        ^ (root_obj_num * 40503u) ^ ((uint32_t)xref_pos * 2246822519u);
    int i;

    for (i = 0; i < 16; i++)
    {
        /* xorshift32 */
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        id16[i] = (uint8_t)(state >> ((i % 4) * 8));
    }
}

static int
prc_pdf_write_id_hex_string(prc_pdf_writer *w, const uint8_t id16[16])
{
    static const char hex[] = "0123456789ABCDEF";
    char buf[34];
    int i;

    buf[0] = '<';
    for (i = 0; i < 16; i++)
    {
        buf[1 + i * 2] = hex[id16[i] >> 4];
        buf[2 + i * 2] = hex[id16[i] & 0x0F];
    }
    buf[33] = '>';
    return (fwrite(buf, 1, sizeof(buf), w->fid) == sizeof(buf)) ? 0 : -1;
}

int
prc_pdf_write_xref_and_trailer(prc_context *ctx, prc_pdf_writer *w,
    uint32_t root_obj_num, uint32_t info_obj_num)
{
    long xref_pos;
    uint32_t i;
    uint8_t id16[16];

    if (w == NULL || w->error)
    {
        prc_error(ctx, PRC_ERROR_IO, "prc_pdf_write_xref_and_trailer: writer already in error state\n");
        return PRC_ERROR_IO;
    }

    xref_pos = ftell(w->fid);
    if (xref_pos < 0)
        goto io_fail;

    if (fprintf(w->fid, "xref\n0 %u\n", w->obj_count + 1) < 0)
        goto io_fail;
    /* Object 0 is always the (sole) free-list head; this writer never
       frees an object once allocated, so every other entry is "in use". */
    if (fprintf(w->fid, "0000000000 65535 f \n") < 0)
        goto io_fail;
    for (i = 0; i < w->obj_count; i++)
    {
        /* Fixed 20-byte entry: 10-digit offset, space, 5-digit generation
           (always 0), space, 'n', space, CRLF. */
        if (fprintf(w->fid, "%010u 00000 n \n", w->obj_offsets[i]) < 0)
            goto io_fail;
    }

    prc_pdf_generate_id(w, root_obj_num, xref_pos, id16);
    if (fprintf(w->fid, "trailer\n<< /Size %u /Root %u 0 R", w->obj_count + 1, root_obj_num) < 0)
        goto io_fail;
    if (info_obj_num != 0)
        if (fprintf(w->fid, " /Info %u 0 R", info_obj_num) < 0)
            goto io_fail;
    if (fprintf(w->fid, " /ID[") < 0)
        goto io_fail;
    /* Both halves identical: this writer only ever produces a fresh,
       single-revision file, never an incremental update (where the first
       half would stay fixed across revisions and the second would change
       per revision). */
    if (prc_pdf_write_id_hex_string(w, id16) != 0) goto io_fail;
    if (prc_pdf_write_id_hex_string(w, id16) != 0) goto io_fail;
    if (fprintf(w->fid, "] >>\nstartxref\n%ld\n%%%%EOF\n", xref_pos) < 0)
        goto io_fail;

    if (ferror(w->fid))
        goto io_fail;

    return 0;

io_fail:
    w->error = 1;
    prc_error(ctx, PRC_ERROR_IO, "prc_pdf_write_xref_and_trailer: write failed\n");
    return PRC_ERROR_IO;
}
