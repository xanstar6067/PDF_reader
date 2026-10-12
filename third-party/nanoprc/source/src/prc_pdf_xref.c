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

/* This file deals with the cross-reference stream parsing */

#include <stdlib.h>
#include <memory.h>
#include <string.h>
#include <stdio.h>
#include <stdint.h>
#include "prc_context.h"
#include "prc_pdf.h"
#include "prc_parse_file_structure.h"

#define PRC_PDF_MAX_NUM_CONTENT_STREAMS 1024

static void
pdf_xref_free_stream_buffers(prc_context *ctx, uint8_t **buff_out_xref,
    uint8_t **buff_out_xref_decode)
{
    if (*buff_out_xref_decode != NULL && *buff_out_xref_decode != *buff_out_xref)
    {
        prc_free(ctx, *buff_out_xref_decode);
    }
    if (*buff_out_xref != NULL)
    {
        prc_free(ctx, *buff_out_xref);
    }
    *buff_out_xref = NULL;
    *buff_out_xref_decode = NULL;
}

/* Non compressed xref parsing */
static int
pdf_parse_xref_non_compressed(prc_context *ctx, uint8_t *pdf_buff_in,
    uint32_t xref_offset, uint32_t size_in, prc_pdf_head_xref *xref_head)
{
    uint8_t found_trailer = 0;
    uint8_t *ptr;
    uint8_t *xref_end;
    uint8_t *file_end = pdf_buff_in + size_in;
    int code;
    uint32_t first_entry_subsection;
    uint32_t xref_num_objects;
    uint32_t k;
    uint32_t object_byte_offset;
    uint32_t object_generation_num;
    uint32_t x_ref_capacity = PDF_NUM_XREF_OBJECTS_INIT;
    uint32_t ref_num;
    uint32_t gen_num;
    uint8_t *temp_ptr;
    uint32_t total_xref_objects = 0;
    uint32_t prev_num_xref_objects = xref_head->num_objects;
    prc_pdf_xref *xref_objects;
    prc_pdf_xref *merged_xref_objects;
    uint8_t *ptr_start;
    uint32_t object_count = 0;

    ptr = pdf_buff_in + xref_offset;
    while (ptr < pdf_buff_in + size_in)
    {
        if (strncmp((char *)ptr, PDF_TRAILER_NAME, PDF_TRAILER_NAME_LEN) == 0)
        {
            found_trailer = 1;
            break;
        }
        ptr++;
    }
    if (!found_trailer)
    {
        prc_error(ctx, PRC_ERROR_PARSE, "Failed to find trailer in PDF file\n");
        return PRC_ERROR_PARSE;
    }
    xref_end = ptr;

    ptr = pdf_buff_in + xref_offset;
    if (strncmp((char *)ptr, PDF_XREF_NAME, PDF_XREF_NAME_LEN) != 0)
    {
        prc_error(ctx, PRC_ERROR_PARSE, "Failed to find xref in PDF file\n");
        return PRC_ERROR_PARSE;
    }

    /* The next line contains the number of objects in the PDF file */
    ptr += (PDF_XREF_NAME_LEN);
    code = pdf_eat_white_space(ctx, &ptr, file_end);
    if (code < 0)
    {
        return code;
    }

    /* Parse the xref data into a structure we can use for finding all the
       various parts */
    /* First figure out how many objects are in the xref table */
    ptr_start = ptr;
    while (ptr < xref_end)
    {
        code = sscanf((const char*) ptr, "%u %u", &first_entry_subsection, &xref_num_objects);
        if (code != 2)
        {
            prc_error(ctx, PRC_ERROR_PARSE, "Failed to find number of objects in PDF file\n");
            return PRC_ERROR_PARSE;
        }
        if (xref_num_objects <= 0)
        {
            prc_error(ctx, PRC_ERROR_PARSE, "Failed to find number of objects in PDF file\n");
            return PRC_ERROR_PARSE;
        }
        total_xref_objects += xref_num_objects;

        /* Skip over xref_num_objects lines */
        for (k = 0; k < xref_num_objects + 1; k++)
        {
            /* Advance ptr to next line */
            while (ptr < xref_end)
            {
                if (*ptr == '\n')
                {
                    ptr++;
                    break;
                }
                ptr++;
            }
        }
        /* Eat any remaining white space */
        code = pdf_eat_white_space(ctx, &ptr, file_end);
        if (code < 0)
        {
            return code;
        }
    }

    xref_objects = (prc_pdf_xref *)prc_calloc(ctx, total_xref_objects, sizeof(prc_pdf_xref));
    if (xref_objects == NULL)
    {
        prc_error(ctx, PRC_ERROR_MEMORY, "Failed to allocate xref_objects\n");
        return PRC_ERROR_MEMORY;
    }

    ptr = ptr_start;
    while (ptr < xref_end)
    {
        code = sscanf((const char*) ptr, "%u %u", &first_entry_subsection, &xref_num_objects);
        if (code != 2)
        {
            prc_error(ctx, PRC_ERROR_PARSE, "Failed to find number of objects in PDF file\n");
            goto fail;
        }
        if (xref_num_objects <= 0)
        {
            prc_error(ctx, PRC_ERROR_PARSE, "Failed to find number of objects in PDF file\n");
            goto fail;
        }

        /* Advance ptr to next line */
        while (ptr < xref_end)
        {
            if (*ptr == '\n')
            {
                ptr++;
                break;
            }
            ptr++;
        }

        for (k = 0; k < xref_num_objects; k++)
        {
            if (strncmp((char *)ptr, PDF_TRAILER_NAME, PDF_TRAILER_NAME_LEN) == 0)
            {
                break;
            }
            code = sscanf((const char*) ptr, "%u %u", &object_byte_offset, &object_generation_num);
            if (code != 2)
            {
                prc_error(ctx, PRC_ERROR_PARSE, "Failed to read object byte offset in PDF file\n");
                goto fail;
            }

            xref_objects[object_count].byte_offset = object_byte_offset;
            xref_objects[object_count].object_number = first_entry_subsection + k;
            xref_objects[object_count].generation_number = object_generation_num;

            /* Check if this is a free object or not */
            if (strncmp((char *)ptr + PDF_XREF_ENTRY_LENGTH - 3, PDF_FREE_OBJECT_NAME, PDF_FREE_OBJECT_NAME_LEN) == 0)
            {
                xref_objects[object_count].is_free = 1;
                xref_objects[object_count].is_compressed = 0;
                xref_objects[object_count].type = PRC_PDF_XREF_FREE_TYPE;
            }
            else if (strncmp((char *)ptr + PDF_XREF_ENTRY_LENGTH - 3, PDF_IN_USE_OBJECT_NAME, PDF_IN_USE_OBJECT_NAME_LEN) == 0)
            {
                xref_objects[object_count].is_free = 0;
                xref_objects[object_count].is_compressed = 0;
                xref_objects[object_count].type = PRC_PDF_XREF_USED_TYPE;
            }
            else
            {
                prc_error(ctx, PRC_ERROR_PARSE, "Unknown object type in PDF file\n");
                goto fail;
            }

#if 0
            /* If not a free object, verify it is correct */
            if (!xref_objects[k].is_free)
            {
                temp_ptr = pdf_buff_in + object_byte_offset;
                code = sscanf((const char*) temp_ptr, "%u %u", &ref_num, &gen_num);
                if (code != 2)
                {
                    prc_error(ctx, PRC_ERROR_PARSE, "Failed to read ref num gen num in PDF file\n");
                    return PRC_ERROR_PARSE;
                }
#if 0
                if (ref_num != xref_objects[object_count].object_number ||
                    gen_num != xref_objects[object_count].generation_number)
                {
                    prc_error(ctx, PRC_ERROR_PARSE, "Object number or generation number mismatch in PDF file\n");
                    return PRC_ERROR_PARSE;
                }
#endif
            }
#endif
            object_count++;
            ptr += PDF_XREF_ENTRY_LENGTH;
        }
        /* Eat any remaining white space */
        code = pdf_eat_white_space(ctx, &ptr, file_end);
        if (code < 0)
        {
            goto fail;
        }
    }

    if (prev_num_xref_objects == 0)
    {
        xref_head->num_objects = total_xref_objects;
        xref_head->xref_objects = xref_objects;
    }
    else
    {
        merged_xref_objects = (prc_pdf_xref *)prc_realloc(ctx, xref_head->xref_objects,
            (prev_num_xref_objects + total_xref_objects) * sizeof(prc_pdf_xref));
        if (merged_xref_objects == NULL)
        {
            prc_free(ctx, xref_objects);
            prc_error(ctx, PRC_ERROR_MEMORY, "Failed to allocate xref_objects\n");
            return PRC_ERROR_MEMORY;
        }
        memcpy(merged_xref_objects + prev_num_xref_objects, xref_objects,
            total_xref_objects * sizeof(prc_pdf_xref));
        prc_free(ctx, xref_objects);
        xref_head->xref_objects = merged_xref_objects;
        xref_head->num_objects = prev_num_xref_objects + total_xref_objects;
    }

    return 0;

fail:
    prc_free(ctx, xref_objects);
    return (code < 0) ? code : PRC_ERROR_PARSE;
}

uint8_t*
prc_pdf_get_ptr_to_obj(prc_context *context, uint8_t *pdf_buff_in, uint32_t size_in,
                       prc_pdf_head_xref *head_xref,
                       prc_pdf_uncompressed_object_stream_list *stream_list,
                       int32_t object_number, uint32_t *size_out,
                       uint8_t *is_object_stream_item)
{
    uint8_t *ptr_temp = NULL;
    int j, k;
    int found_object = 0;
    uint32_t num_streams = stream_list->number_streams;

    /* Search for that object using the xref table */
    if (head_xref == NULL)
    {
        return NULL;
    }
    found_object = 0;
    *is_object_stream_item = 0;
    *size_out = 0;

    for (j = 0; j < head_xref->num_objects; j++)
    {
        /* First check the non-compressed streams */
        if (head_xref->xref_objects[j].object_number == object_number &&
            head_xref->xref_objects[j].type == PRC_PDF_XREF_USED_TYPE)
        {
            /* Found the object in the xref table */
            ptr_temp = pdf_buff_in + head_xref->xref_objects[j].byte_offset;
            found_object = 1;
            *is_object_stream_item = 0;
            *size_out = size_in - head_xref->xref_objects[j].byte_offset;
            break;
        }
    }
    /* Did not find the object in the inuse objects. Check any
       compressed streams */
    if (!found_object && num_streams > 0)
    {
        for (j = 0; j < num_streams; j++)
        {
            for (k = 0; k < stream_list->ustream[j].n; k++)
            {
                if (stream_list->ustream[j].object_offsets[k].object_number == object_number)
                {
                    /* Found the object in the compressed stream list */
                    ptr_temp = stream_list->ustream[j].stream +
                        stream_list->ustream[j].object_offsets[k].offset;

                    /* The size in this case is the actual size for this object
                       only, not the whole stream. */
                    /* Check if it is the last object */
                    if (k == stream_list->ustream[j].n - 1)
                    {
                        *size_out = stream_list->ustream[j].stream_length -
                                    stream_list->ustream[j].object_offsets[k].offset;
                    }
                    else
                    {
                        *size_out = stream_list->ustream[j].object_offsets[k + 1].offset -
                                    stream_list->ustream[j].object_offsets[k].offset;
                    }
                    found_object = 1;
                    *is_object_stream_item = 1;
                    break;
                }
            }
            if (found_object)
            {
                break;
            }
        }

        if (!found_object)
        {
            for (j = 0; j < head_xref->num_objects; j++)
            {
                if (head_xref->xref_objects[j].type == PRC_PDF_XREF_COMPRESSED_TYPE &&
                    head_xref->xref_objects[j].object_number == (uint32_t)object_number)
                {
                    uint32_t stream_object_number = head_xref->xref_objects[j].byte_offset;
                    uint32_t stream_index = head_xref->xref_objects[j].object_stream_index;

                    for (k = 0; k < num_streams; k++)
                    {
                        if (stream_list->ustream[k].stream_object_number == stream_object_number)
                        {
                            if (stream_index >= stream_list->ustream[k].n)
                            {
                                continue;
                            }

                            ptr_temp = stream_list->ustream[k].stream +
                                stream_list->ustream[k].object_offsets[stream_index].offset;

                            if (stream_index == stream_list->ustream[k].n - 1)
                            {
                                *size_out = stream_list->ustream[k].stream_length -
                                    stream_list->ustream[k].object_offsets[stream_index].offset;
                            }
                            else
                            {
                                *size_out = stream_list->ustream[k].object_offsets[stream_index + 1].offset -
                                    stream_list->ustream[k].object_offsets[stream_index].offset;
                            }
                            found_object = 1;
                            *is_object_stream_item = 1;
                            break;
                        }
                    }
                }

                if (found_object)
                {
                    break;
                }
            }
        }
    }
    return ptr_temp;
}

static int
prc_pdf_xref_stream_parse(prc_context *context, prc_pdf_head_xref *xref,
    int32_t *entry_subsections, uint32_t number_subsections,
    int32_t byte_widths[], const uint8_t *data, size_t data_size,
    uint32_t *num_content_streams, uint32_t *xref_head_offset)
{
    int k;
    int j;
    int m;
    uint8_t type;
    uint32_t object_number_byte_offset;
    uint32_t generation_number_index;
    uint32_t content_stream_obj[PRC_PDF_MAX_NUM_CONTENT_STREAMS];
    uint8_t already_added = 0;
    uint32_t xref_num_objects = 0;
    uint32_t first_entry_subsection;
    uint32_t xref_object_index;
    uint32_t prev_num_xref_objects = xref->num_objects;

    *num_content_streams = 0;

    if (context == NULL || xref == NULL || data == NULL || byte_widths == NULL ||
        byte_widths[0] < 1 || byte_widths[1] < 1)
    {
        return PRC_ERROR_PDF;
    }

    if ((number_subsections * 2u) > 256u)
    {
        return PRC_ERROR_PDF;
    }

    /* Validate subsection metadata and add up number of objects safely.
       The /Index array is signed in our parser; reject negative or overflowing values. */
    for (k = 0; k < number_subsections; k++)
    {
        int32_t subsection_start = entry_subsections[2 * k];
        int32_t subsection_count = entry_subsections[2 * k + 1];

        if (subsection_start < 0 || subsection_count < 0)
        {
            return PRC_ERROR_PDF;
        }

        if ((uint32_t)subsection_count > (UINT32_MAX - xref_num_objects))
        {
            return PRC_ERROR_PDF;
        }

        xref_num_objects += (uint32_t)subsection_count;
    }

    if (xref_num_objects > (UINT32_MAX - xref->num_objects))
    {
        return PRC_ERROR_PDF;
    }

    xref->num_objects += xref_num_objects;
    if (prev_num_xref_objects > 0)
    {
        prc_pdf_xref *new_xref_objects = (prc_pdf_xref *)prc_realloc(context,
            xref->xref_objects, xref->num_objects * sizeof(prc_pdf_xref));
        if (new_xref_objects == NULL)
        {
            return PRC_ERROR_MEMORY;
        }
        xref->xref_objects = new_xref_objects;
    }
    else
    {
        xref->xref_objects = (prc_pdf_xref *)prc_calloc(context, xref->num_objects,
            sizeof(prc_pdf_xref));
    }
    if (xref->xref_objects == NULL)
    {
        return PRC_ERROR_MEMORY;
    }

    /* Now go through each subsection */
    xref_object_index = prev_num_xref_objects;
    *xref_head_offset = xref_object_index;  /* Used later for knowing where to start when we deal with the streams */
    for (m = 0; m < number_subsections; m++)
    {
        int32_t subsection_start = entry_subsections[2 * m];
        int32_t subsection_count = entry_subsections[2 * m + 1];

        if (subsection_start < 0 || subsection_count < 0)
        {
            return PRC_ERROR_PDF;
        }

        first_entry_subsection = (uint32_t)subsection_start;
        xref_num_objects = (uint32_t)subsection_count;

        if (first_entry_subsection > (UINT32_MAX - xref_num_objects))
        {
            return PRC_ERROR_PDF;
        }

        for (k = 0; k < xref_num_objects; k++)
        {
            if (xref_object_index >= xref->num_objects)
            {
                return PRC_ERROR_PDF;
            }

            prc_pdf_xref *xref_object = &xref->xref_objects[xref_object_index];
            xref_object_index++;
            type = 0;
            object_number_byte_offset = 0;
            generation_number_index = 0;

            /* Read type high-order byte first */
            for (j = 0; j < byte_widths[0]; j++)
            {
                if (data_size <= 0)
                {
                    return PRC_ERROR_PDF;
                }
                type = (type << 8) | *data++;
                data_size--;
            }

            /* If type is 0, this is the object number of next free object.
               If type is 1, this is the byte offset of the object in the file.
               If type is 2, this is the object number of the object stream in
                             in which this object is stored. */
            for (j = 0; j < byte_widths[1]; j++)
            {
                if (data_size <= 0)
                {
                    return PRC_ERROR_PDF;
                }
                object_number_byte_offset = (object_number_byte_offset << 8) | *data++;
                data_size--;
            }

            /* If type is 0 or 1, this is generation number.
               If type is 2 this is the index of object in object stream */
            if (byte_widths[2] == 0)
            {
                generation_number_index = 0; /* Default to 0 if not present */
            }
            else
            {
                for (j = 0; j < byte_widths[2]; j++)
                {
                    if (data_size <= 0)
                    {
                        return PRC_ERROR_PDF;
                    }
                    generation_number_index = (generation_number_index << 8) | *data++;
                    data_size--;
                }
            }

            xref_object->type = type;

            if (type == 0x00)  /* free object */
            {
                xref_object->is_free = 1;
                xref_object->object_number = object_number_byte_offset;
                xref_object->generation_number = generation_number_index;
                xref_object->byte_offset = 0; /* free objects have no byte offset */
                xref_object->is_compressed = 0;
            }
            else if (type == 0x01)  /* in-use object */
            {
                xref_object->is_free = 0;
                xref_object->byte_offset = object_number_byte_offset;
                xref_object->object_number = k + first_entry_subsection;
                xref_object->generation_number = generation_number_index;
                xref_object->is_compressed = 0;
            }
            else if (type == 0x02)  /* compressed object */
            {
                xref_object->is_free = 0;
                xref_object->is_compressed = 1;
                xref_object->generation_number = 0;
                xref_object->object_number = k + first_entry_subsection; /* actual object number */
                xref_object->byte_offset = object_number_byte_offset; /* containing object stream number */
                xref_object->object_stream_index = generation_number_index; /* index of the object in the stream */

                if (*num_content_streams >= PRC_PDF_MAX_NUM_CONTENT_STREAMS)
                {
                    return PRC_ERROR_PDF; /* too many content streams */
                }

                already_added = 0;
                if (*num_content_streams > 0)
                {
                    for (j = 0; j < *num_content_streams; j++)
                    {
                        if (content_stream_obj[j] == object_number_byte_offset)
                        {
                            already_added = 1;
                        }
                    }
                }

                if (!already_added)
                {
                    content_stream_obj[*num_content_streams] = object_number_byte_offset;
                    (*num_content_streams)++;
                }
            }
            else
            {
                return PRC_ERROR_PDF; /* unknown type */
            }
        }
    }
    return 0;
}

/* No legitimate PDF has anywhere near this many incremental-update xref
   sections; this exists purely to bound the /Prev chain walk below against a
   crafted file. */
#define PRC_PDF_MAX_XREF_CHAIN 4096

/* There can be multiple xrefs each referenced by the \Prev entry of the
   current xref. Walk them iteratively (not recursively): /Prev is an
   attacker-controlled offset with no relation to prior ones, so a crafted
   file could otherwise build a cycle (A -> B -> A) causing infinite
   recursion, or simply a very long chain driving stack depth proportional to
   file size. The heap-allocated offsets array also gives us cycle detection
   and a chain-length cap for free, which the recursive form did not have. */
int
pdf_count_xref_sections(prc_context *ctx, uint8_t *pdf_buff_in,
    uint32_t xref_offset, uint32_t size_in, uint32_t *num_xrefs,
    uint32_t **xref_offsets)
{
    uint8_t *file_end = pdf_buff_in + size_in;
    uint32_t capacity = (*num_xrefs > 0) ? *num_xrefs : 1;
    uint32_t count = 0;
    uint32_t current_offset = xref_offset;
    uint32_t *offsets;

    offsets = (uint32_t *)prc_calloc(ctx, capacity, sizeof(uint32_t));
    if (offsets == NULL)
    {
        prc_error(ctx, PRC_ERROR_MEMORY, "Failed to allocate xref_offsets\n");
        return PRC_ERROR_MEMORY;
    }

    for (;;)
    {
        uint32_t prev_offset;
        uint32_t k;
        int code;
        uint8_t *ptr;

        /* Cycle guard: reject a /Prev chain that revisits an offset. */
        for (k = 0; k < count; k++)
        {
            if (offsets[k] == current_offset)
            {
                prc_error(ctx, PRC_ERROR_PDF, "Cyclic /Prev chain in PDF xref\n");
                prc_free(ctx, offsets);
                return PRC_ERROR_PDF;
            }
        }

        if (count >= PRC_PDF_MAX_XREF_CHAIN)
        {
            prc_error(ctx, PRC_ERROR_PDF, "/Prev xref chain too long in PDF\n");
            prc_free(ctx, offsets);
            return PRC_ERROR_PDF;
        }

        if (count >= capacity)
        {
            uint32_t *new_offsets;
            capacity *= 2;
            new_offsets = (uint32_t *)prc_realloc(ctx, offsets, capacity * sizeof(uint32_t));
            if (new_offsets == NULL)
            {
                prc_error(ctx, PRC_ERROR_MEMORY, "Failed to grow xref_offsets\n");
                prc_free(ctx, offsets);
                return PRC_ERROR_MEMORY;
            }
            offsets = new_offsets;
        }

        offsets[count] = current_offset;
        count++;

        /* /Prev is file-supplied with no other validation; bound it against
           the buffer before dereferencing at that offset. */
        if (current_offset >= size_in)
        {
            prc_error(ctx, PRC_ERROR_PDF, "Invalid xref offset in PDF\n");
            prc_free(ctx, offsets);
            return PRC_ERROR_PDF;
        }

        ptr = pdf_buff_in + current_offset;
        code = pdf_get_integer_prc(ctx, ptr, file_end, (uint8_t *)PDF_PREV_NAME,
            PDF_PREV_NAME_LEN, (uint8_t *)PDF_STREAM_NAME, PDF_STREAM_NAME_LEN, &prev_offset);
        if (code < 0)
        {
            /* No /Prev -- this was the last (oldest) xref section. */
            break;
        }

        current_offset = prev_offset;
    }

    *xref_offsets = offsets;
    *num_xrefs = count;
    return 0;
}

/* We can have multiple xrefs. We try to pack all the data into one here... */
int
pdf_parse_xref(prc_context *ctx, uint8_t *pdf_buff_in,uint32_t size_in,
    prc_pdf_head_xref *xref_head,
    prc_pdf_uncompressed_object_stream_list *stream_list,
    uint32_t *xref_offsets, uint8_t *streams_are_encrypted,
    prc_pdf_decrypt_params *encryption_params)
{
    uint8_t *ptr;
    uint8_t *file_end = pdf_buff_in + size_in;
    int code;
    uint8_t xref_encoded = 0;
    uint8_t xref_predictor_present = 0;
    uint32_t xref_predictor_offset = 0;
    prc_pdf_decode_params decode_params;
    uint32_t byte_widths[3] = { 0 };
    uint32_t index[256] = { 0 };
    uint8_t *ptr_stream = NULL;
    uint32_t stream_length = 0;
    uint8_t *buff_out_xref = NULL;
    uint32_t size_out_xref = 0;
    uint8_t *buff_out_xref_decode = NULL;
    uint32_t size_out_xref_decode = 0;
    uint32_t num_object_streams = 0;
    uint32_t size;
    uint32_t stream_obj_num;
    uint32_t stream_gen_num;
    uint32_t num_in_array;
    uint32_t xref_offset;
    uint32_t xref_head_offset = 0;
    uint32_t encryption_obj_num = 0;

    *streams_are_encrypted = 0;

    buff_out_xref = NULL;
    buff_out_xref_decode = NULL;
    size_out_xref = 0;
    size_out_xref_decode = 0;
    xref_offset = *xref_offsets;

    /* The xref table could be encoded. Test this by first seeing if xref is present */
    ptr = pdf_buff_in + xref_offset;
    if (strncmp((char *)ptr, PDF_XREF_NAME, PDF_XREF_NAME_LEN) != 0)
    {
        /* We have to decode the xref table */
        xref_encoded = 1;

        /* Check if there are DecodeParams */
        ptr = pdf_buff_in + xref_offset;
        xref_predictor_offset = pdf_search_for_tag(ctx, ptr, file_end,
            (uint8_t *) PDF_DECODE_PARMS_NAME, PDF_DECODE_PARMS_NAME_LEN,
            (uint8_t *) PDF_STREAM_NAME, PDF_STREAM_NAME_LEN, &xref_predictor_present);

        if (xref_predictor_present)
        {
            /* We have to do the crazy prediction decoding. This bit is lifted
                from pdfium, which has a BSD license */
                /* Move us past /DecodeParams */
            ptr += xref_predictor_offset;
            ptr += PDF_DECODE_PARMS_NAME_LEN;
            code = pdf_get_decode_params(ctx, ptr, file_end, &decode_params);
            if (code < 0)
            {
                prc_error(ctx, PRC_ERROR_PARSE, "Failed to get decode params in PDF file\n");
                return code;
            }
        }

        /* Look for /W */
        ptr = pdf_buff_in + xref_offset;
        num_in_array = 3;
        code = pdf_get_integer_array_prc(ctx, ptr, file_end, (uint8_t *)PDF_W_NAME,
            PDF_W_NAME_LEN, (uint8_t *)PDF_STREAM_NAME, PDF_STREAM_NAME_LEN,
            (int32_t*) byte_widths, &num_in_array, 3);
        if (code < 0)
        {
            prc_error(ctx, PRC_ERROR_PARSE, "Failed to read byte widths in PDF file\n");
            return code;
        }

        /* Look for /Size */
        ptr = pdf_buff_in + xref_offset;
        code = pdf_get_integer_prc(ctx, ptr, file_end, (uint8_t *)PDF_SIZE_NAME,
            PDF_SIZE_NAME_LEN, (uint8_t *)PDF_STREAM_NAME, PDF_STREAM_NAME_LEN, &size);
        if (code < 0)
        {
            prc_error(ctx, PRC_ERROR_PARSE, "Failed to read size in PDF file\n");
            return code;
        }

        /* Look for /Index */
        ptr = pdf_buff_in + xref_offset;
        num_in_array = 0;
        code = pdf_get_integer_array_prc(ctx, ptr, file_end, (uint8_t *)PDF_INDEX_NAME,
            PDF_INDEX_NAME_LEN, (uint8_t *)PDF_STREAM_NAME, PDF_STREAM_NAME_LEN,
            (int32_t *) index, &num_in_array, 256);
        if (code < 0)
        {
            /* This is an optional element. Set it based upon the PDF_SIZE_NAME */
            index[0] = 0;
            index[1] = size;
            num_in_array = 2;
        }

        /* Get the actual stream data. Note this stream is not encrypted */
        ptr = pdf_buff_in + xref_offset;
        code = pdf_get_stream_info(ctx, ptr, file_end, &ptr_stream, &stream_length,
            &stream_obj_num, &stream_gen_num);
        if (code < 0)
        {
            prc_error(ctx, PRC_ERROR_PARSE, "Failed to get stream info in PDF file\n");
            return code;
        }

        ptr = pdf_buff_in + xref_offset;
        code = pdf_get_stream_data(ctx, ptr, file_end, ptr_stream, stream_length,
            &buff_out_xref, &size_out_xref);
        if (code < 0)
        {
            prc_error(ctx, PRC_ERROR_PARSE, "Failed to get stream data in PDF file\n");
            return code;
        }

        /* Now apply the predictor.  There is always one (it could be NONE) */
        if (xref_predictor_present)
        {
            code = pdf_apply_predictor(ctx, buff_out_xref, size_out_xref,
                &buff_out_xref_decode, &size_out_xref_decode, decode_params);
            if (code < 0)
            {
                prc_error(ctx, PRC_ERROR_PARSE, "Failed to apply predictor in PDF file\n");
                pdf_xref_free_stream_buffers(ctx, &buff_out_xref,
                    &buff_out_xref_decode);
                return code;
            }
        }
        else
        {
            buff_out_xref_decode = buff_out_xref;
            size_out_xref_decode = size_out_xref;
        }

        /* Now we deal with the decoding of the cross-reference stream */
        code = prc_pdf_xref_stream_parse(ctx, xref_head, (int32_t*) index,
            num_in_array / 2, (int32_t *)byte_widths, buff_out_xref_decode, size_out_xref_decode,
            &num_object_streams, &xref_head_offset);
        if (code < 0)
        {
            prc_error(ctx, PRC_ERROR_PARSE, "Failed to parse xref stream in PDF file\n");
            pdf_xref_free_stream_buffers(ctx, &buff_out_xref,
                &buff_out_xref_decode);
            return code;
        }

        if (buff_out_xref != NULL)
        {
            pdf_xref_free_stream_buffers(ctx, &buff_out_xref,
                &buff_out_xref_decode);
        }

        /* Get a pointer to this offset and then check for the presence of
            a /Encrypt object */
        if (*streams_are_encrypted == 0)
        {
            ptr = pdf_buff_in + xref_offset;
            code = pdf_get_integer_prc(ctx, ptr, file_end, (uint8_t *)PDF_ENCRYPT_NAME,
                PDF_ENCRYPT_NAME_LEN, (uint8_t *)PDF_STREAM_NAME, PDF_STREAM_NAME_LEN,
                &encryption_obj_num);
            if (code >= 0)
            {
                *streams_are_encrypted = 1;
            }

            /* Also get the fileID as it may be here if we don't yet have it */
            if (encryption_params->file_id_length == 0)
            {
                code = pdf_get_file_id_from_object(ctx, ptr, file_end, encryption_params->file_id,
                    &encryption_params->file_id_length);
                if (code < 0)
                {
                    prc_error(ctx, PRC_ERROR_PARSE, "Did not get file ID in PDF file\n");
                    pdf_xref_free_stream_buffers(ctx, &buff_out_xref,
                        &buff_out_xref_decode);
                    return code;
                }
            }

            if (*streams_are_encrypted)
            {
                /* Parse the encryption object */
                code = pdf_parse_decryption(ctx, xref_head, pdf_buff_in, file_end,
                    encryption_obj_num, encryption_params);
                if (code < 0)
                {
                    prc_error(ctx, PRC_ERROR_PARSE, "Did not get encryption information\n");
                    pdf_xref_free_stream_buffers(ctx, &buff_out_xref,
                        &buff_out_xref_decode);
                    return code;
                }
            }
        }

        /* Lets decompress all the object streams.  These can be encrypted */
        if (num_object_streams > 0)
        {
            code = prc_pdf_object_stream_decompress(ctx, pdf_buff_in, size_in,
                xref_head, num_object_streams, stream_list, xref_head_offset,
                *streams_are_encrypted, encryption_params);
            if (code < 0)
            {
                prc_error(ctx, PRC_ERROR_PARSE, "Failed to decompress content streams in PDF file\n");
                pdf_xref_free_stream_buffers(ctx, &buff_out_xref,
                    &buff_out_xref_decode);
                return code;
            }
        }
    }

    if (!xref_encoded)
    {
        code = pdf_parse_xref_non_compressed(ctx, pdf_buff_in, xref_offset,
            size_in, xref_head);
        if (code < 0)
        {
            prc_error(ctx, code, "Failed to parse xref in PDF file\n");
            return code;
        }

        /* Check if streams are encrypted here too */
        if (*streams_are_encrypted == 0)
        {
            ptr = pdf_buff_in + xref_offset;
            code = pdf_get_integer_prc(ctx, ptr, file_end, (uint8_t *)PDF_ENCRYPT_NAME,
                PDF_ENCRYPT_NAME_LEN, (uint8_t *)PDF_STREAM_NAME, PDF_STREAM_NAME_LEN,
                &encryption_obj_num);
            if (code >= 0)
            {
                *streams_are_encrypted = 1;
            }
            if (*streams_are_encrypted)
            {
                /* Parse the encryption object */
                code = pdf_parse_decryption(ctx, xref_head, pdf_buff_in, file_end,
                    encryption_obj_num, encryption_params);
                if (code < 0)
                {
                    prc_error(ctx, PRC_ERROR_PARSE, "Did not get encryption information\n");
                    return code;
                }
            }
        }
    }

    return 0;
}
