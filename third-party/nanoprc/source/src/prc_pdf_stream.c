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

#include <stdlib.h>
#include <memory.h>
#include <string.h>
#include <stdio.h>
#include <stdint.h>
#include "prc_context.h"
#include "prc_pdf.h"
#include "prc_parse_file_structure.h"

static void
pdf_stream_release_uncompressed_list(prc_context *ctx,
    prc_pdf_uncompressed_object_stream_list *list)
{
    uint32_t i;

    if (list->ustream != NULL)
    {
        for (i = 0; i < list->number_streams; i++)
        {
            if (list->ustream[i].object_offsets != NULL)
            {
                prc_free(ctx, list->ustream[i].object_offsets);
                list->ustream[i].object_offsets = NULL;
            }
            if (list->ustream[i].stream != NULL)
            {
                prc_free(ctx, list->ustream[i].stream);
                list->ustream[i].stream = NULL;
            }
        }
        prc_free(ctx, list->ustream);
        list->ustream = NULL;
    }
    list->number_streams = 0;
}

static void
pdf_stream_release_object_stream(prc_context *ctx,
    prc_pdf_uncompressed_object_stream *ustream)
{
    if (ustream->object_offsets != NULL)
    {
        prc_free(ctx, ustream->object_offsets);
        ustream->object_offsets = NULL;
    }
    if (ustream->stream != NULL)
    {
        prc_free(ctx, ustream->stream);
        ustream->stream = NULL;
    }
}

static int
pdf_stream_parse_u32_bounded(uint8_t **ptr, uint8_t *end, uint32_t *value)
{
    uint32_t v = 0;
    uint8_t has_digit = 0;

    while (*ptr < end && (**ptr == ' ' || **ptr == '\n' || **ptr == '\r' || **ptr == '\t'))
    {
        (*ptr)++;
    }

    while (*ptr < end && **ptr >= '0' && **ptr <= '9')
    {
        uint32_t digit = (uint32_t)(**ptr - '0');
        has_digit = 1;

        if (v > (UINT32_MAX - digit) / 10u)
        {
            return PRC_ERROR_PARSE;
        }
        v = v * 10u + digit;
        (*ptr)++;
    }

    if (!has_digit)
    {
        return PRC_ERROR_PARSE;
    }

    *value = v;
    return 0;
}

/* Below is adapated from pdfium C++ code which has a BSD license */
static
void duplicate_span(prc_context *ctx, prc_pdf_span des, prc_pdf_span src)
{
    des.data = src.data;
    des.size = src.size;
}

static int
new_span(prc_context *ctx, prc_pdf_span *span, size_t size)
{
    if (size == 0)
    {
        span->data = NULL;
        span->size = 0;
        return 0;
    }
    span->data = (uint8_t *)prc_calloc(ctx, size, sizeof(uint8_t));
    if (span->data == NULL)
    {
        return PRC_ERROR_MEMORY;
    }
    span->size = size;
    return 0;
}

static void
init_span(prc_context *ctx, prc_pdf_span *span, uint8_t *data, size_t size)
{
    span->data = data;
    span->size = size;
}

static uint8_t
front_span(prc_context *ctx, prc_pdf_span span)
{
    if (span.size == 0)
    {
        return 0;
    }
    return span.data[0];
}

static prc_pdf_span
sub_span(prc_context *ctx, prc_pdf_span span, size_t offset, size_t size)
{
    prc_pdf_span sub;
    if (offset + size > span.size)
    {
        sub.data = NULL;
        sub.size = 0;
    }
    else
    {
        sub.data = span.data + offset;
        sub.size = size;
    }
    return sub;
}

static prc_pdf_span
sub_span_shift(prc_context *ctx, prc_pdf_span span, size_t offset)
{
    prc_pdf_span sub;
    if (offset >= span.size)
    {
        sub.data = NULL;
        sub.size = 0;
    }
    else
    {
        sub.data = span.data + offset;
        sub.size = span.size - offset;
    }

    return sub;
}


static uint8_t
get_left_value(prc_context *ctx, prc_pdf_span span, size_t i,
               uint32_t bytes_per_pixel)
{
    return i >= bytes_per_pixel ? span.data[i - bytes_per_pixel] : 0;
}

static uint8_t
get_up_value(prc_context *ctx, prc_pdf_span span, size_t i)
{
    return span.size ? span.data[i] : 0;
}

static uint8_t
get_upper_left_value(prc_context *ctx, prc_pdf_span span, size_t i,
                    uint32_t bytes_per_pixel)
{
    if (i >= bytes_per_pixel && span.size != 0)
    {
        return span.data[i - bytes_per_pixel];
    }
    return 0;
}

static uint8_t
path_predictor(prc_context *ctx, uint8_t a, uint8_t b, uint8_t c)
{
    int p = (int) (a) + b - c;
    int pa = abs(p - a);
    int pb = abs(p - b);
    int pc = abs(p - c);

    if (pa <= pb && pa <= pc)
    {
        return a;
    }

    return pb <= pc ? b : c;
}

static uint32_t
calculate_pitch8(prc_context *ctx, uint32_t bpc, uint32_t components, int width)
{
    uint32_t pitch = bpc;

    pitch *= components;
    pitch *= width;
    pitch += 7;
    pitch /= 8;

    return pitch;
}

static size_t
compute_size(prc_context *ctx, uint32_t w, size_t h)
{
    size_t safe_size = w;
    safe_size *= h;
    return safe_size;
}

static void
png_predict_line(prc_context *ctx, prc_pdf_span dest_span, prc_pdf_span src_span,
    prc_pdf_span last_span, size_t row_size, uint32_t bytes_per_pixel)
{
    uint8_t tag = front_span(ctx, src_span);
    prc_pdf_span remaining_src_span = sub_span(ctx, src_span, 1u, row_size);
    switch (tag)
    {
        case 1: {
            for (size_t i = 0; i < remaining_src_span.size; ++i)
            {
                uint8_t left = get_left_value(ctx, dest_span, i, bytes_per_pixel);
                dest_span.data[i] = remaining_src_span.data[i] + left;
            }
            break;
        }
        case 2: {
            for (size_t i = 0; i < remaining_src_span.size; ++i)
            {
                uint8_t up = get_up_value(ctx, last_span, i);
                dest_span.data[i] = remaining_src_span.data[i] + up;
            }
            break;
        }
        case 3: {
            for (size_t i = 0; i < remaining_src_span.size; ++i)
            {
                uint8_t left = get_left_value(ctx, dest_span, i, bytes_per_pixel);
                uint8_t up = get_up_value(ctx, last_span, i);
                dest_span.data[i] = remaining_src_span.data[i] + (up + left) / 2;
            }
            break;
        }
        case 4: {
            for (size_t i = 0; i < remaining_src_span.size; ++i)
            {
                uint8_t left = get_left_value(ctx, dest_span, i, bytes_per_pixel);
                uint8_t up = get_up_value(ctx, last_span, i);
                uint8_t upper_left = get_upper_left_value(ctx, last_span, i, bytes_per_pixel);
                dest_span.data[i] =
                    remaining_src_span.data[i] + path_predictor(ctx, left, up, upper_left);
            }
            break;
        }
        default: {
            memccpy(dest_span.data, remaining_src_span.data, 0, remaining_src_span.size);
            break;
        }
    }
}

int
pdf_png_predictor(prc_context *ctx, prc_pdf_decode_params decode_params,
    uint8_t *buff_in, uint32_t size_in, uint8_t **buff_out_decode,
    uint32_t *size_out_decode)
{
    uint32_t bytes_per_pixel =
        (decode_params.colors * decode_params.bits_per_component + 7) / 8;
    uint8_t *dest_buff = NULL;
    uint32_t row_size = calculate_pitch8(ctx, decode_params.bits_per_component,
        decode_params.colors, decode_params.columns);
    if (row_size == 0)
    {
        prc_error(ctx, PRC_ERROR_PARSE, "PNG Predictor row size is 0 in PDF file\n");
        return PRC_ERROR_PARSE;
    }

    uint32_t src_row_size = row_size + 1;
    size_t row_count = (size_in + row_size) / src_row_size;
    if (row_count == 0)
    {
        prc_error(ctx, PRC_ERROR_PARSE, "PNG Predictor row count is 0 in PDF file\n");
        return PRC_ERROR_PARSE;
    }

    uint32_t last_row_size = size_in % src_row_size;
    size_t dest_size = compute_size(ctx, row_size, row_count);
    if (last_row_size)
    {
        dest_size -= src_row_size - last_row_size;
    }

    if (dest_size == 0)
    {
        prc_error(ctx, PRC_ERROR_PARSE, "PNG Predictor destination size is 0 in PDF file\n");
        return PRC_ERROR_PARSE;
    }
    *size_out_decode = dest_size;
    *buff_out_decode = (uint8_t *)prc_calloc(ctx, 1, dest_size);
    if (*buff_out_decode == NULL)
    {
        prc_error(ctx, PRC_ERROR_MEMORY, "Failed to allocate output buffer for PNG predictor\n");
        return PRC_ERROR_MEMORY;
    }

    /* Move over to spans like are used in PDFium for decode */
    prc_pdf_span remaining_src_span;
    init_span(ctx, &remaining_src_span, buff_in, size_in);
    prc_pdf_span remaining_dest_span;
    init_span(ctx, &remaining_dest_span, *buff_out_decode, dest_size);
    prc_pdf_span prev_dest_span;

    int code = new_span(ctx, &prev_dest_span, 0);
    if (code != 0)
    {
        prc_error(ctx, code, "Failed to allocate previous destination span for PNG predictor\n");
        prc_free(ctx, *buff_out_decode);
        *buff_out_decode = NULL;
        return code;
    }

    for (size_t row = 0; row < row_count; row++)
    {
        size_t remaining_row_size = row_size < remaining_src_span.size - 1 ? row_size : remaining_src_span.size - 1;

        png_predict_line(ctx, remaining_dest_span, remaining_src_span, prev_dest_span,
            remaining_row_size, bytes_per_pixel);
        remaining_src_span = sub_span_shift(ctx, remaining_src_span, remaining_row_size + 1);
        prev_dest_span = remaining_dest_span;
        remaining_dest_span = sub_span_shift(ctx, remaining_dest_span, remaining_row_size);
    }

    return 0;
}

int
pdf_tiff_predictor(prc_context *ctx, prc_pdf_decode_params decode_params,
    uint8_t *buff_in_xref, uint32_t size_in_xref, uint8_t **buff_out_xref_decode,
    uint32_t *size_out_xref_decode)
{
    return 0;
}

int
prc_pdf_object_stream_decompress(prc_context *ctx, uint8_t *pdf_data,
    uint32_t pdf_data_size, prc_pdf_head_xref *compressed_xref,
    uint32_t num_content_streams,
    prc_pdf_uncompressed_object_stream_list *uncompressed_list,
    uint32_t xref_head_offset, uint8_t has_encryption,
    prc_pdf_decrypt_params *decryption_params)
{
    uint32_t k, j;
    uint32_t num_objects = compressed_xref->num_objects;
    uint32_t num_found = 0;
    prc_pdf_xref *xref_object;
    prc_pdf_uncompressed_object_stream *ustream;
    uint8_t *ptr;
    int32_t code;
    uint8_t *file_end = pdf_data + pdf_data_size;
    uint8_t *ptr_stream;
    uint32_t stream_length;
    uint32_t byte_offset;
    uint8_t found;
    uint32_t stream_obj_num;
    uint32_t stream_gen_num;
    uint32_t prev_stream_index = 0;
    uint8_t *stream_end;

    if (num_content_streams == 0)
    {
        return 0;
    }

    if (uncompressed_list->number_streams == 0)
    {
        uncompressed_list->number_streams = num_content_streams;
        uncompressed_list->ustream = (prc_pdf_uncompressed_object_stream *)prc_calloc(ctx,
            num_content_streams, sizeof(prc_pdf_uncompressed_object_stream));
    }
    else
    {
        prc_pdf_uncompressed_object_stream *ustream_temp;
        num_found = uncompressed_list->number_streams;
        prev_stream_index = uncompressed_list->number_streams;
        uncompressed_list->number_streams += num_content_streams;
        ustream_temp = (prc_pdf_uncompressed_object_stream *)prc_realloc(ctx,
            uncompressed_list->ustream, uncompressed_list->number_streams * sizeof(prc_pdf_uncompressed_object_stream));
        if (ustream_temp == NULL)
        {
            prc_error(ctx, PRC_ERROR_MEMORY, "Failed to allocate uncompressed stream list\n");
            return PRC_ERROR_MEMORY;
        }
        uncompressed_list->ustream = ustream_temp;

        /* Initialize the new entries to zero */
        for (k = num_found; k < uncompressed_list->number_streams; k++)
        {
            memset(&uncompressed_list->ustream[k], 0, sizeof(prc_pdf_uncompressed_object_stream));
        }
    }
    if (uncompressed_list->ustream == NULL)
    {
        prc_error(ctx, PRC_ERROR_MEMORY, "Failed to allocate uncompressed stream list\n");
        return PRC_ERROR_MEMORY;
    }

    if (xref_head_offset >= num_objects)
    {
        return 0;
    }

    for (k = xref_head_offset; k < num_objects; k++)
    {
        xref_object = &compressed_xref->xref_objects[k];

        if (xref_object->type == PRC_PDF_XREF_COMPRESSED_TYPE)
        {
            /* Check if we already have this one first */
            found = 0;
            for (j = 0; j < num_found; j++)
            {
                if (uncompressed_list->ustream[j].stream_object_number ==
                                                    xref_object->byte_offset)
                {
                    /* Already found this one, go to the next object */
                    found = 1;
                    break;
                }
            }
            if (found)
            {
                continue;
            }

            byte_offset = 0;
            /* We have a new object, so we need to decompress it */
            for (j = 0; j < num_objects; j++)
            {
                if (compressed_xref->xref_objects[j].type == PRC_PDF_XREF_USED_TYPE &&
                    compressed_xref->xref_objects[j].object_number ==
                    xref_object->byte_offset)
                {
                    /* This is the object we are looking for, so we can get the byte offset */
                    byte_offset = compressed_xref->xref_objects[j].byte_offset;
                    break;
                }
            }
            if (byte_offset == 0)
            {
                prc_error(ctx, PRC_ERROR_PARSE, "Failed to find byte offset for object %u in PDF file\n",
                    xref_object->byte_offset);
                pdf_stream_release_uncompressed_list(ctx, uncompressed_list);
                return PRC_ERROR_PARSE;
            }

            /* First we need to find the type 1 item in the xref table that
               has this object number so that we can get the proper byte offset */
            if (num_found == uncompressed_list->number_streams)
            {
                return 0;
            }
            ustream = &uncompressed_list->ustream[num_found];
            ustream->stream_object_number = xref_object->byte_offset;
            ptr = pdf_data + byte_offset;

            /* Look for /N */
            code = pdf_search_for_tag(ctx, ptr, file_end, (uint8_t *) PDF_N_NAME,
                PDF_N_NAME_LEN, (uint8_t *) PDF_STREAM_NAME, PDF_STREAM_NAME_LEN, &found);
            if (!found)
            {
                /* Not found throw error */
                prc_error(ctx, PRC_ERROR_PARSE, "Failed to find /N in PDF object stream\n");
                pdf_stream_release_uncompressed_list(ctx, uncompressed_list);
                return PRC_ERROR_PARSE;
            }
            else
            {
                ptr += code;
                ptr += PDF_N_NAME_LEN; /* Move past /N name */

                /* Scan for the number */
                code = sscanf((const char *)ptr, "%u", &ustream->n);
                if (code != 1)
                {
                    prc_error(ctx, PRC_ERROR_PARSE, "Failed to parse /N in PDF object stream\n");
                    pdf_stream_release_uncompressed_list(ctx, uncompressed_list);
                    return PRC_ERROR_PARSE;
                }
            }

            /* Now get /First entry */
            ptr = pdf_data + byte_offset;
            code = pdf_search_for_tag(ctx, ptr, file_end, (uint8_t *) PDF_FIRST_NAME,
                PDF_FIRST_NAME_LEN, (uint8_t *) PDF_STREAM_NAME, PDF_STREAM_NAME_LEN, &found);
            if (!found)
            {
                /* Not found throw error */
                prc_error(ctx, PRC_ERROR_PARSE, "Failed to find /First in PDF object stream\n");
                pdf_stream_release_uncompressed_list(ctx, uncompressed_list);
                return PRC_ERROR_PARSE;
            }
            else
            {
                ptr += code;
                ptr += PDF_FIRST_NAME_LEN; /* Move past /First name */
                /* Scan for the first object number */
                code = sscanf((const char *)ptr, "%u", &ustream->first);
                if (code != 1)
                {
                    prc_error(ctx, PRC_ERROR_PARSE, "Failed to parse /First in PDF object stream\n");
                    pdf_stream_release_uncompressed_list(ctx, uncompressed_list);
                    return PRC_ERROR_PARSE;
                }
            }

            /* Now get /Filter entry */
            ptr = pdf_data + byte_offset;
            code = pdf_search_for_tag(ctx, ptr, file_end, (uint8_t *) PDF_FILTER_NAME,
                PDF_FILTER_NAME_LEN, (uint8_t *) PDF_STREAM_NAME, PDF_STREAM_NAME_LEN, &found);
            if (!found)
            {   /* Not found throw error */
                prc_error(ctx, PRC_ERROR_PARSE, "Failed to find /Filter in PDF object stream\n");
                pdf_stream_release_uncompressed_list(ctx, uncompressed_list);
                return PRC_ERROR_PARSE;
            }
            else
            {
                ptr += code;
                ptr += PDF_FILTER_NAME_LEN; /* Move past /Filter name */
                /* Scan for the filter name */
                code = pdf_search_for_tag(ctx, ptr, file_end, (uint8_t *) PDF_FLATEDECODE_NAME,
                    PDF_FLATEDECODE_NAME_LEN, (uint8_t *) PDF_STREAM_NAME, PDF_STREAM_NAME_LEN, &found);
                if (!found)
                {
                    prc_error(ctx, PRC_ERROR_PARSE, "Failed to find FlateDecode in PDF object stream\n");
                    pdf_stream_release_uncompressed_list(ctx, uncompressed_list);
                    return PRC_ERROR_PARSE;
                }
            }

            /* Get the actual stream data */
            ptr = pdf_data + byte_offset;
            code = pdf_get_stream_info(ctx, ptr, file_end, &ptr_stream, &stream_length,
                &stream_obj_num, &stream_gen_num);
            if (code < 0)
            {
                prc_error(ctx, PRC_ERROR_PARSE, "Failed to get stream info in PDF file\n");
                return code;
            }

            if (has_encryption)
            {
                size_t decypted_size;
                uint32_t actual_decrypted_size;
                uint8_t *decrypted_data;

                decypted_size = pdf_decrypt_get_size(ctx, decryption_params, stream_length);
                decrypted_data = (uint8_t *)prc_calloc(ctx, decypted_size, sizeof(uint8_t));
                if (decrypted_data == NULL)
                {
                    prc_error(ctx, PRC_ERROR_MEMORY, "Did not allocate decrypted data\n");
                    return PRC_ERROR_MEMORY;
                }

                code = pdf_get_decrypted_stream_data(ctx, ptr_stream, stream_length,
                                        decryption_params, decrypted_data, decypted_size,
                                        stream_obj_num, stream_gen_num,
                                        &actual_decrypted_size);
                if (code < 0)
                {
                    prc_error(ctx, PRC_ERROR_PARSE, "Did not get decrypted stream data in PDF file\n");
                    prc_free(ctx, decrypted_data);
                    return code;
                }

                /* Now we need to deflate the decrypted stream */
                code = pdf_get_stream_data(ctx, ptr, file_end, decrypted_data, actual_decrypted_size,
                                           &ustream->stream, &ustream->stream_length);
                if (code < 0)
                {
                    prc_error(ctx, PRC_ERROR_PARSE, "Did not get PRC stream data (encrypted) in PDF file\n");
                    prc_free(ctx, decrypted_data);
                    return code;
                }
                prc_free(ctx, decrypted_data);
            }
            else
            {
                code = pdf_get_stream_data(ctx, ptr, file_end, ptr_stream, stream_length,
                    &ustream->stream, &ustream->stream_length);
                if (code < 0)
                {
                    prc_error(ctx, PRC_ERROR_PARSE, "Failed to get stream data in PDF file\n");
                    return code;
                }
            }

            /* Now get the N pairs of integers */
            ustream->object_offsets = (prc_pdf_object_stream_offsets *)prc_calloc(ctx,
                ustream->n, sizeof(prc_pdf_object_stream_offsets));
            if (ustream->object_offsets == NULL)
            {
                prc_error(ctx, PRC_ERROR_MEMORY, "Failed to allocate object offsets in PDF object stream\n");
                pdf_stream_release_object_stream(ctx, ustream);
                return PRC_ERROR_MEMORY;
            }

            ptr = ustream->stream;
            stream_end = ustream->stream + ustream->stream_length;
            for (j = 0; j < ustream->n; j++)
            {
                if (ptr >= stream_end)
                {
                    prc_error(ctx, PRC_ERROR_PARSE, "Object stream index table exceeds decoded stream bounds\n");
                    pdf_stream_release_object_stream(ctx, ustream);
                    return PRC_ERROR_PARSE;
                }

                code = pdf_stream_parse_u32_bounded(&ptr, stream_end,
                    &ustream->object_offsets[j].object_number);
                if (code < 0)
                {
                    prc_error(ctx, PRC_ERROR_PARSE, "Failed to parse object offsets in PDF object stream\n");
                    pdf_stream_release_object_stream(ctx, ustream);
                    return PRC_ERROR_PARSE;
                }

                code = pdf_stream_parse_u32_bounded(&ptr, stream_end,
                    &ustream->object_offsets[j].offset);
                if (code < 0)
                {
                    prc_error(ctx, PRC_ERROR_PARSE, "Failed to parse object offsets in PDF object stream\n");
                    pdf_stream_release_object_stream(ctx, ustream);
                    return PRC_ERROR_PARSE;
                }

                /* This skips us over the pairs of indices */
                ustream->object_offsets[j].offset += ustream->first;
                if (ustream->object_offsets[j].offset >= ustream->stream_length)
                {
                    prc_error(ctx, PRC_ERROR_PARSE, "Parsed object offset exceeds decoded stream size\n");
                    pdf_stream_release_object_stream(ctx, ustream);
                    return PRC_ERROR_PARSE;
                }

                while (ptr < stream_end && (*ptr == ' ' || *ptr == '\n' ||
                    *ptr == '\r' || *ptr == '\t'))
                {
                    ptr++;
                }
            }
            num_found++;
        }
    }

    return 0;
}

int
pdf_get_decode_params(prc_context *ctx, uint8_t *pdf_buff_in, uint8_t *file_end,
    prc_pdf_decode_params *decode_params)
{
    uint8_t *ptr = pdf_buff_in;
    int code;
    int ret_count;

    /* For now we only support the default values */
    decode_params->predictor = 1;
    decode_params->colors = 1;
    decode_params->bits_per_component = 8;
    decode_params->columns = 1;
    decode_params->early_change = 1;

    /* Eat any white space first */
    code = pdf_eat_white_space(ctx, &ptr, file_end);
    if (code < 0)
    {
        return code;
    }

    /* Between << and >> look for the keywords */

    /* Now we should be at the start of the dictionary */
    if (*ptr != '<' || *(ptr + 1) != '<')
    {
        prc_error(ctx, PRC_ERROR_PARSE, "Failed to find << in PDF file\n");
        return PRC_ERROR_PARSE;
    }
    ptr += 2; /* Move past << */
    /* Eat white space */
    code = pdf_eat_white_space(ctx, &ptr, file_end);
    if (code < 0)
    {
        return code;
    }

    /* Now read until we hit >> */
    while (ptr < file_end && strncmp((char *)ptr, ">>", 2) != 0)
    {
        code = pdf_check_for_dict_int_entry(ctx, ptr, file_end,
            (uint8_t*)PDF_PREDICTOR_NAME, PDF_PREDICTOR_NAME_LEN, &decode_params->predictor);
        if (code > 0)
        {
            ptr += code;
        }
        else if (code < 0)
        {
            return code;
        }

        code = pdf_check_for_dict_int_entry(ctx, ptr, file_end,
            (uint8_t *)PDF_COLORS_NAME, PDF_COLORS_NAME_LEN, &decode_params->colors);
        if (code > 0)
        {
            ptr += code;
        }
        else if (code < 0)
        {
            return code;
        }

        code = pdf_check_for_dict_int_entry(ctx, ptr, file_end,
            (uint8_t *)PDF_BITS_PER_COMPONENT_NAME, PDF_BITS_PER_COMPONENT_NAME_LEN,
            &decode_params->bits_per_component);
        if (code > 0)
        {
            ptr += code;
        }
        else if (code < 0)
        {
            return code;
        }

        code = pdf_check_for_dict_int_entry(ctx, ptr, file_end,
            (uint8_t *)PDF_COLUMNS_NAME, PDF_COLUMNS_NAME_LEN, &decode_params->columns);
        if (code > 0)
        {
            ptr += code;
        }
        else if (code < 0)
        {
            return code;
        }

        code = pdf_check_for_dict_int_entry(ctx, ptr, file_end,
            (uint8_t *)PDF_EARLY_CHANGE_NAME, PDF_EARLY_CHANGE_NAME_LEN,
            &decode_params->early_change);
        if (code > 0)
        {
            ptr += code;
        }
        else if (code < 0)
        {
            return code;
        }

        if (ptr >= file_end)
        {
            prc_error(ctx, PRC_ERROR_PARSE, "Failed to find >> in PDF file\n");
            return PRC_ERROR_PARSE;
        }
    }
    return 0;
}

int
pdf_apply_predictor(prc_context *ctx, uint8_t *buff_in, uint32_t size_in,
    uint8_t **buff_out_decode, uint32_t *size_out_decode,
    prc_pdf_decode_params decode_params)
{
    int code;

    switch (decode_params.predictor)
    {
    case PRC_PDF_PREDICTOR_NONE: /* No predictor */
        *buff_out_decode = buff_in;
        *size_out_decode = size_in;
        return 0;
    case PRC_PDF_PREDICTOR_PNG_NONE:
    case PRC_PDF_PREDICTOR_PNG_SUB:
    case PRC_PDF_PREDICTOR_PNG_AVG:
    case PRC_PDF_PREDICTOR_PNG_PAETH:
    case PRC_PDF_PREDICTOR_PNG_UP:
    case PRC_PDF_PREDICTOR_PNG_OPTIM:
        code = pdf_png_predictor(ctx, decode_params, buff_in, size_in,
            buff_out_decode, size_out_decode);
        code = 0;
        if (code < 0)
        {
            prc_error(ctx, PRC_ERROR_PARSE, "Failed to apply PNG predictor\n");
            return PRC_ERROR_PARSE;
        }
        return 0;
    case PRC_PDF_PREDICTOR_TIFF: /* TIFF predictor */
#if 0
        code = pdf_tiff_predictor(ctx, decode_params, buff_in, size_in,
            buff_out_decode, size_out_decode);
        if (code < 0)
        {
            prc_error(ctx, PRC_ERROR_PARSE, "Failed to apply PNG predictor\n");
            return PRC_ERROR_PARSE;
        }
        return 0;
#endif
    default:
        prc_error(ctx, PRC_ERROR_PARSE, "Unsupported predictor type %d\n", decode_params.predictor);
        return PRC_ERROR_PARSE;
    }
    return 0;
}
