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

/* Needed for parsing the encryption dictionary. This is likely fragile.. */

#include <stdlib.h>
#include <memory.h>
#include <string.h>
#include <stdio.h>
#include "prc_context.h"
#include "prc_pdf.h"

/* Helper method to search for start of dictionary */
static int
prc_pdf_dict_get_start(prc_context *ctx, uint8_t *input_buffer,
    uint8_t **dict_start, uint8_t *buffer_end)
{
    /* First look for initial << but make sure we don't run into an endobj
       before hitting a << */
    uint8_t *ptr = input_buffer;
    while (ptr < buffer_end - 1)
    {
        if (ptr[0] == '<' && ptr[1] == '<')
        {
            *dict_start = ptr + 2;
            return 0;
        }
        if (strncmp((const char*) ptr, (const char *) PDF_ENDOBJ_NAME, PDF_ENDOBJ_NAME_LEN) == 0)
        {
            return PRC_ERROR_PARSE;
        }
        ptr++;
    }
    prc_error(ctx, PRC_ERROR_PARSE, "Failed to find start of PDF dictionary\n");
    return PRC_ERROR_PARSE;
}

/* Helper method to pass over any dictionary embedded in a dictionary */
static int
prc_pdf_dict_skip_dict(prc_context *ctx, uint8_t **ptr,
    uint8_t *buffer_end)
{
    int dict_level = 0;

    /* First check that we are at the start of a dict.. */
    if (!((*ptr)[0] == '<' && (*ptr)[1] == '<'))
        return 0;

    dict_level++;
    *ptr += 2;
    while (*ptr < buffer_end - 1 && dict_level > 0)
    {
        if ((*ptr)[0] == '<' && (*ptr)[1] == '<')
        {
            dict_level++;
            *ptr += 2;
        }
        else if ((*ptr)[0] == '>' && (*ptr)[1] == '>')
        {
            dict_level--;
            *ptr += 2;
        }
        else
            (*ptr)++;
    }
    if (dict_level != 0)
    {
        //prc_error(ctx, PRC_ERROR_PARSE, "issue to parse PDF dictionary\n");
        return PRC_ERROR_PARSE;
    }
    return 0;
}

int
prc_pdf_dict_get_boolean(prc_context *ctx, uint8_t *input_buffer, uint8_t *buffer_end,
                        const char *key, uint8_t *value, uint8_t default_value)
{
    uint8_t *ptr = input_buffer;
    int code;

    /* Get to the start of the dictionary */
    code = prc_pdf_dict_get_start(ctx, input_buffer, &ptr, buffer_end);
    if (code < 0)
        return code;

    while (ptr < buffer_end - 1)
    {
        /* Skip over any embedded dictionaries */
        code = prc_pdf_dict_skip_dict(ctx, &ptr, buffer_end);
        if (code < 0)
            return code;

        /* Look for the key */
        if (strncmp((char *)ptr, key, strlen(key)) == 0)
        {
            ptr += strlen(key);

            /* Skip white space */
            while (ptr < buffer_end && (*ptr == ' ' || *ptr == '\n' || *ptr == '\r' || *ptr == '\t'))
                ptr++;

            /* Now read the boolean */
            if (ptr >= buffer_end)
                break;
            if (strncmp((char *)ptr, "true", 4) == 0)
            {
                *value = 1;
                return 0;
            }
            else if (strncmp((char *)ptr, "false", 5) == 0)
            {
                *value = 0;
                return 0;
            }
            else
            {
                prc_error(ctx, PRC_ERROR_PARSE, "Failed to read boolean in PDF dictionary\n");
                return PRC_ERROR_PARSE;
            }
        }
        ptr++;

        /* Look for the end markers */
        if (ptr < buffer_end - 1 && ptr[0] == '>' && ptr[1] == '>')
            break;
    }

    /* Not found, return default */
    *value = default_value;
    return 0;
}

int
prc_pdf_dict_get_uinteger(prc_context *ctx, uint8_t *input_buffer, uint8_t *buffer_end,
                          const char *key, uint32_t *value,  uint32_t default_value)
{
    uint8_t *ptr = input_buffer;
    int code;

    /* Get to the start of the dictionary */
    code = prc_pdf_dict_get_start(ctx, input_buffer, &ptr, buffer_end);
    if (code < 0)
        return code;

    while (ptr < buffer_end - 1)
    {
        /* Skip over any embedded dictionaries */
        code = prc_pdf_dict_skip_dict(ctx, &ptr, buffer_end);
        if (code < 0)
            return code;

        /* Look for the key */
        if (strncmp((char *)ptr, key, strlen(key)) == 0)
        {
            ptr += strlen(key);

            /* Skip white space */
            while (ptr < buffer_end && (*ptr == ' ' || *ptr == '\n' || *ptr == '\r' || *ptr == '\t'))
                ptr++;
            /* Now read the integer */
            if (ptr >= buffer_end)
                break;
            if (sscanf((char *)ptr, "%d", value) != 1)
            {
                prc_error(ctx, PRC_ERROR_PARSE, "Failed to read integer in PDF dictionary\n");
                return PRC_ERROR_PARSE;
            }
            return 0;
        }
        ptr++;

        /* Look for the end markers */
        if (ptr < buffer_end - 1 && ptr[0] == '>' && ptr[1] == '>')
            break;
    }

    /* Not found, return default */
    *value = default_value;
    return 0;
}

/* Get a signed integer */
int
prc_pdf_dict_get_integer(prc_context *ctx, uint8_t *input_buffer, uint8_t *buffer_end,
                        const char *key, int32_t *value, int32_t default_value)
{
    uint8_t *ptr = input_buffer;
    int code;
    /* Get to the start of the dictionary */
    code = prc_pdf_dict_get_start(ctx, input_buffer, &ptr, buffer_end);
    if (code < 0)
        return code;
    while (ptr < buffer_end - 1)
    {
        /* Skip over any embedded dictionaries */
        code = prc_pdf_dict_skip_dict(ctx, &ptr, buffer_end);
        if (code < 0)
            return code;
        /* Look for the key */
        if (strncmp((char *)ptr, key, strlen(key)) == 0)
        {
            ptr += strlen(key);
            /* Skip white space */
            while (ptr < buffer_end && (*ptr == ' ' || *ptr == '\n' || *ptr == '\r' || *ptr == '\t'))
                ptr++;
            /* Now read the integer */
            if (ptr >= buffer_end)
                break;
            if (sscanf((char *)ptr, "%d", value) != 1)
            {
                prc_error(ctx, PRC_ERROR_PARSE, "Failed to read integer in PDF dictionary\n");
                return PRC_ERROR_PARSE;
            }
            return 0;
        }
        ptr++;
        /* Look for the end markers */
        if (ptr < buffer_end - 1 && ptr[0] == '>' && ptr[1] == '>')
            break;
    }
    /* Not found, return default */
    *value = default_value;
    return 0;
}

/* Here we get enumerated types */
int
prc_pdf_dict_get_bytestring(prc_context *ctx, uint8_t *input_buffer, uint8_t *buffer_end,
                        const char *key, char *string, uint32_t max_str_len)
{
    uint8_t *ptr = input_buffer;
    int code;
    size_t k = 0;
    size_t key_len = strlen(key);
    size_t str_len = 0;
    int in_string = 0;

    string[0] = 0;

    /* Get to the start of the dictionary */
    code = prc_pdf_dict_get_start(ctx, input_buffer, &ptr, buffer_end);
    if (code < 0)
        return code;
    while (ptr < buffer_end - 1)
    {
        /* Skip over any embedded dictionaries */
        code = prc_pdf_dict_skip_dict(ctx, &ptr, buffer_end);
        if (code < 0)
            return code;

        /* Look for the key */
        if (strncmp((char *)ptr, key, key_len) == 0)
        {
            ptr += key_len;

            /* Skip white space */
            while (ptr < buffer_end && (*ptr == ' ' || *ptr == '\n' || *ptr == '\r' || *ptr == '\t'))
                ptr++;

            /* Skip the / */
            ptr++;

            /* Now read the string looking for it to end with a /, a white space or a > */
            if (ptr >= buffer_end)
                break;

            while (ptr < buffer_end && k < max_str_len - 1)
            {
                if (ptr[0] == '/' || ptr[0] == '>' || ptr[0] == '\n' || ptr[0] == '\r' || ptr[0] == '\t' || ptr[0] == ' ')
                    break;
                string[k++] = ptr[0];
                ptr++;
            }
            string[k] = 0;
            return 0;
        }
        ptr++;
    }
    prc_error(ctx, PRC_ERROR_PARSE, "Failed to find string in PDF dictionary\n");
    return PRC_ERROR_PARSE;
}

/* Get a pointer to an embedded dictionary in our dictionary */
int
prc_pdf_dict_get_dict(prc_context *ctx, uint8_t *input_buffer, uint8_t *buffer_end,
                      const char *key, uint8_t **dict_start, uint8_t **dict_end)
{
    uint8_t *ptr = input_buffer;
    int code;
    uint8_t found_end = 0;

    /* Get to the start of the dictionary */
    code = prc_pdf_dict_get_start(ctx, input_buffer, &ptr, buffer_end);
    if (code < 0)
        return code;

    while (ptr < buffer_end - 1)
    {
        /* Skip over any embedded dictionaries */
        code = prc_pdf_dict_skip_dict(ctx, &ptr, buffer_end);
        if (code < 0)
            return code;

        /* Look for the key */
        if (strncmp((char *)ptr, key, strlen(key)) == 0)
        {
            ptr += strlen(key);

            /* Skip white space */
            while (ptr < buffer_end && (*ptr == ' ' || *ptr == '\n' || *ptr == '\r' || *ptr == '\t'))
                ptr++;

            /* This should have us at the start of the dictionary */
            if (ptr >= buffer_end)
                break;
            if (ptr[0] != '<' || ptr[1] != '<')
            {
                prc_error(ctx, PRC_ERROR_PARSE, "Failed to find start of embedded PDF dictionary\n");
                return PRC_ERROR_PARSE;
            }
            *dict_start = ptr;
            ptr += 2;

            /* Now find the end of the dictionary */
            while (ptr < buffer_end - 1)
            {
                /* Skip over any embedded dictionaries */
                code = prc_pdf_dict_skip_dict(ctx, &ptr, buffer_end);
                if (code < 0)
                    return code;

                if (ptr[0] == '>' && ptr[1] == '>')
                {
                    found_end = 1;
                    *dict_end = ptr + 2;
                    break;
                }
                ptr++;
            }
            if (!found_end)
            {
                prc_error(ctx, PRC_ERROR_PARSE, "Failed to find end of embedded PDF dictionary\n");
                return PRC_ERROR_PARSE;
            }
            return 0;
        }
        ptr++;
    }
    prc_error(ctx, PRC_ERROR_PARSE, "Failed to find embedded PDF dictionary\n");
    return PRC_ERROR_PARSE;
}

/* Get a ref associated keyword */
int
prc_pdf_dict_get_ref(prc_context *ctx, uint8_t *input_buffer, uint8_t *buffer_end,
    const char *key, uint32_t *object_num, uint32_t *gen_num)
{
    uint8_t *ptr = input_buffer;
    int code;
    size_t k = 0;
    size_t key_len = strlen(key);
    uint8_t found_key = 0;

    /* Get to the start of the dictionary */
    code = prc_pdf_dict_get_start(ctx, input_buffer, &ptr, buffer_end);
    if (code < 0)
        return code;

    while (ptr < buffer_end - 1)
    {
        /* Skip over any embedded dictionaries */
        code = prc_pdf_dict_skip_dict(ctx, &ptr, buffer_end);
        if (code < 0)
            return code;

        /* Look for the key */
        if (strncmp((char *)ptr, key, key_len) == 0)
        {
            ptr += key_len;
            found_key = 1;

            /* Skip white space */
            while (ptr < buffer_end && (*ptr == ' ' || *ptr == '\n' || *ptr == '\r' || *ptr == '\t'))
                ptr++;

            /* Now read the two integers */
            if (ptr >= buffer_end)
                break;

            if (sscanf((char *)ptr, "%d %d", object_num, gen_num) != 2)
            {
                prc_error(ctx, PRC_ERROR_PARSE, "Failed to read reference in PDF dictionary\n");
                return PRC_ERROR_PARSE;
            }
            return found_key;
        }
        ptr++;
    }
    return found_key;
}

/* Get a literal string that is associated with a keyword */
int
prc_pdf_dict_get_literal_string(prc_context *ctx, uint8_t *input_buffer, uint8_t *buffer_end,
    const char *key, uint8_t *string, uint32_t max_str_len, uint32_t *actual_str_len)
{
    uint8_t *ptr = input_buffer;
    int code;
    size_t k = 0;
    size_t key_len = strlen(key);
    size_t str_len = 0;
    int in_string = 0;
    *actual_str_len = 0;
    /* Get to the start of the dictionary */
    code = prc_pdf_dict_get_start(ctx, input_buffer, &ptr, buffer_end);
    if (code < 0)
        return code;
    while (ptr < buffer_end - 1)
    {
        /* Skip over any embedded dictionaries */
        code = prc_pdf_dict_skip_dict(ctx, &ptr, buffer_end);
        if (code < 0)
            return code;
        /* Look for the key */
        if (strncmp((char *)ptr, key, key_len) == 0)
        {
            ptr += key_len;
            /* Skip white space */
            while (ptr < buffer_end && (*ptr == ' ' || *ptr == '\n' || *ptr == '\r' || *ptr == '\t'))
                ptr++;
            /* Now read the string looking for it to end with a ) */
            if (ptr >= buffer_end)
                break;
            if (ptr[0] != '(')
            {
                prc_error(ctx, PRC_ERROR_PARSE, "Failed to find start of literal string in PDF dictionary\n");
                return PRC_ERROR_PARSE;
            }
            ptr++;
            while (ptr < buffer_end && k < max_str_len)
            {
                /* Check for an end symbol but make sure it is not preceded by
                   an escape symbol \ */
                if (ptr[0] == ')' && ptr[-1] != '\\')
                {
                    break;
                }
                string[k++] = ptr[0];
                ptr++;
            }
            *actual_str_len = k;
            return 0;
        }
        ptr++;
    }
    prc_error(ctx, PRC_ERROR_PARSE, "Failed to find literal string in PDF dictionary\n");
    return PRC_ERROR_PARSE;
}

/* Get a keyword associated with another keyword */
int
prc_pdf_dict_get_type(prc_context *ctx, uint8_t *input_buffer, uint8_t *buffer_end,
    const char *key, uint8_t *keyout, uint32_t max_keyout_len, uint32_t *actual_keyout_len)
{
    uint8_t *ptr = input_buffer;
    int code;
    size_t k = 0;
    size_t key_len = strlen(key);
    *actual_keyout_len = 0;
    uint8_t found_key = 0;

    /* Get to the start of the dictionary */
    code = prc_pdf_dict_get_start(ctx, input_buffer, &ptr, buffer_end);
    if (code < 0)
        return code;

    while (ptr < buffer_end - 1)
    {
        /* Skip over any embedded dictionaries */
        code = prc_pdf_dict_skip_dict(ctx, &ptr, buffer_end);
        if (code < 0)
            return code;

        /* Look for the key */
        if (strncmp((char *)ptr, key, key_len) == 0)
        {
            ptr += key_len;
            found_key = 1;

            /* Skip white space */
            while (ptr < buffer_end && (*ptr == ' ' || *ptr == '\n' || *ptr == '\r' || *ptr == '\t'))
                ptr++;

            /* Skip the / but put it in the output*/
            ptr++;
            keyout[k++] = '/';

            /* Now read the string looking for it to end with a /, a white space or a > */
            if (ptr >= buffer_end)
                break;

            while (ptr < buffer_end && k < max_keyout_len)
            {
                if (ptr[0] == '/' || ptr[0] == '>' || ptr[0] == '\n' || ptr[0] == '\r' || ptr[0] == '\t' || ptr[0] == ' ')
                    break;
                keyout[k++] = ptr[0];
                ptr++;
            }
            *actual_keyout_len = k;
            return found_key;
        }
        ptr++;
    }
    return found_key;
}

/* Get a hex string. These are delimited by < and >. Read in two  hex characters at a time
   and merge them into a single entry */
int
prc_pdf_dict_get_hexstring(prc_context *ctx, uint8_t *input_buffer, uint8_t *buffer_end,
                        const char *key, uint8_t *hexstring, uint32_t max_str_len, uint32_t *actual_str_len)
{
    uint8_t *ptr = input_buffer;
    int code;
    size_t k = 0;
    size_t key_len = strlen(key);
    size_t str_len = 0;
    int in_string = 0;
    *actual_str_len = 0;
    uint8_t evennibble = 1;

    /* Get to the start of the dictionary */
    code = prc_pdf_dict_get_start(ctx, input_buffer, &ptr, buffer_end);
    if (code < 0)
        return code;
    while (ptr < buffer_end - 1)
    {
        /* Skip over any embedded dictionaries */
        code = prc_pdf_dict_skip_dict(ctx, &ptr, buffer_end);
        if (code < 0)
            return code;
        /* Look for the key */
        if (strncmp((char *)ptr, key, strlen(key)) == 0)
        {
            ptr += strlen(key);
            /* Skip white space */
            while (ptr < buffer_end && (*ptr == ' ' || *ptr == '\n' || *ptr == '\r' || *ptr == '\t'))
                ptr++;
            /* Now read the string looking for it to end with a > */
            if (ptr >= buffer_end)
                break;
            if (ptr[0] != '<')
            {
                prc_error(ctx, PRC_ERROR_PARSE, "Failed to find start of hex string in PDF dictionary\n");
                return PRC_ERROR_PARSE;
            }
            ptr++;
            while (ptr < buffer_end && k < max_str_len)
            {
                if (ptr[0] == '>')
                    break;
                if ((ptr[0] >= '0' && ptr[0] <= '9') ||
                    (ptr[0] >= 'A' && ptr[0] <= 'F') ||
                    (ptr[0] >= 'a' && ptr[0] <= 'f'))
                {
                    if (evennibble)
                    {
                        hexstring[k] = 0;
                        if (ptr[0] >= '0' && ptr[0] <= '9')
                            hexstring[k] = (ptr[0] - '0') << 4;
                        else if (ptr[0] >= 'A' && ptr[0] <= 'F')
                            hexstring[k] = (ptr[0] - 'A' + 10) << 4;
                        else
                            hexstring[k] = (ptr[0] - 'a' + 10) << 4;
                        evennibble = 0;
                    }
                    else
                    {
                        if (ptr[0] >= '0' && ptr[0] <= '9')
                            hexstring[k] |= (ptr[0] - '0');
                        else if (ptr[0] >= 'A' && ptr[0] <= 'F')
                            hexstring[k] |= (ptr[0] - 'A' + 10);
                        else
                            hexstring[k] |= (ptr[0] - 'a' + 10);
                        evennibble = 1;
                        k++;
                    }
                }
                ptr++;
            }
            if (ptr >= buffer_end)
                break;
            if (ptr[0] != '>')
            {
                prc_error(ctx, PRC_ERROR_PARSE, "Failed to find end of hex string in PDF dictionary\n");
                    return PRC_ERROR_PARSE;
            }
            *actual_str_len = k;
            return 0;
        }
        ptr++;
        /* Look for the end markers */
        if (ptr < buffer_end - 1 && ptr[0] == '>' && ptr[1] == '>')
            break;
    }
    prc_error(ctx, PRC_ERROR_PARSE, "Failed to find hex string in PDF dictionary\n");
    return PRC_ERROR_PARSE;
}
