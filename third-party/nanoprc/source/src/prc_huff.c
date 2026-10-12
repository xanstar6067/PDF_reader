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
#include <stdlib.h>
#include <memory.h>
#include "prc_data.h"
#include "prc_bit.h"
#include "prc_huff.h"
#include "prc_parse_common.h"

/* Test code for many of the reading and writing methods */
static uint8_t*
add_bit_in_array(prc_context *ctx, uint8_t *pbBitArray, uint8_t bValue, unsigned *uMaxSize, unsigned *uSize)
{
    if (*uSize == *uMaxSize)
    {
        *uMaxSize += *uMaxSize / 10;
        uint8_t *pbNew = (uint8_t * ) prc_malloc(ctx, *uMaxSize * sizeof(uint8_t));
        if (pbNew == NULL)
            return NULL;
        memcpy(pbNew, pbBitArray, *uSize * sizeof(uint8_t));
        prc_free(ctx, pbBitArray);
        pbBitArray = pbNew;
    }
    pbBitArray[*uSize] = bValue;
    *uSize += 1;
    return pbBitArray;
}

#define true 1
#define false 0

int
get_huffman_code(unsigned char val, unsigned *uCodeValue, unsigned int *uCodeLength, unsigned int uNumberOfLeaves,
               unsigned int *puLeafValues, unsigned int *puLeafCodeLength, unsigned int *puLeafCodeValues)
{
    unsigned int k = 0;
    int code_found = -1;

    for (k = 0; k < uNumberOfLeaves; k++)
    {
        if (val == puLeafValues[k])
        {
            *uCodeValue = puLeafCodeValues[k];
            *uCodeLength = puLeafCodeLength[k];
            code_found = 0;
            break;
        }
    }
    return code_found;
}

/* Used for sorting the frequency list in ascending order*/
static int
compare_freq(const void *a, const void *b)
{
    prc_huff_freq_data arg1 = *(const prc_huff_freq_data *)a;
    prc_huff_freq_data arg2 = *(const prc_huff_freq_data *)b;

    if (arg1.freq < arg2.freq) return -1;
    if (arg1.freq > arg2.freq) return 1;
    return 0;
}

/* Compute a Huffman tree. This is to test the encode/decode process */
int
huffman_tree_calculation(prc_context *ctx, unsigned char* pcArray, unsigned int uCharArraySize, unsigned int uNumberOfBitsForValues,
    unsigned int *uNumberOfLeaves, unsigned int *uMaxCodeLength, unsigned int **puLeafValues,
    unsigned int **puLeafCodeLength, unsigned int **puLeafCodeValues)
{
    int i, j;
    unsigned int ui;
    int num_leaves;
    char freq_temp[256];
    prc_huff_freq_data *huff_freq;
    unsigned int *leaf_values;
    unsigned int *code_length;
    unsigned int *code_values;
    prc_huff_node *priority_queue;
    prc_huff_node *node_bank;
    int bank_index;
    int queue_size;
    prc_huff_node *curr_node, *new_node, *parent;
    prc_huff_node **leaf_nodes;
    int factor, length, code, max_code_length;

    /* First build up our frequency for the unique values */
    memset(freq_temp, 0, 256);
    for (ui = 0; ui < uCharArraySize; ui++)
    {
        freq_temp[pcArray[ui]]++;
    }
    num_leaves = 0;
    for (i = 0; i < 256; i++)
    {
        if (freq_temp[i] > 0)
            num_leaves++;
    }
    huff_freq = (prc_huff_freq_data *)prc_malloc(ctx, num_leaves * sizeof(prc_huff_freq_data));
    if (huff_freq == NULL)
    {
        prc_error(ctx, PRC_ERROR_MEMORY, "Allocation error in huffman_tree_calculation\n");
        return PRC_ERROR_MEMORY;
    }

    /* Get from the array of 256 to an array of num_leaves (freq table) */
    j = 0;
    for (i = 0; i < 256; i++)
    {
        if (freq_temp[i] > 0)
        {
            huff_freq[j].freq = freq_temp[i];
            huff_freq[j].values = i;
            j++;
        }
    }
    /* Now sort the freq table in ascending order of their frequencies */
    /* That is starting at the least frequent.  It will be at the bottom of the table */
    qsort(huff_freq, num_leaves, sizeof(prc_huff_freq_data), compare_freq);

    /* This is the stuff that needs to be encoded in the PRC spec */
    leaf_values = (unsigned int*)prc_malloc(ctx, sizeof(unsigned int) * num_leaves);
    if (leaf_values == NULL)
    {
        prc_error(ctx, PRC_ERROR_MEMORY, "Allocation error in huffman_tree_calculation\n");
        return PRC_ERROR_MEMORY;
    }
    code_length = (unsigned int*)prc_malloc(ctx, sizeof(unsigned) * num_leaves);
    if (code_length == NULL)
    {
        prc_error(ctx, PRC_ERROR_MEMORY, "Allocation error in huffman_tree_calculation\n");
        return PRC_ERROR_MEMORY;
    }
    code_values = (unsigned int*)prc_malloc(ctx, sizeof(unsigned) * num_leaves);
    if (code_values == NULL)
    {
        prc_error(ctx, PRC_ERROR_MEMORY, "Allocation error in huffman_tree_calculation\n");
        return PRC_ERROR_MEMORY;
    }
    *uNumberOfLeaves = num_leaves;

    /* We need to find the code length and code values for each leaf value,
       so we need to construct the Huffman tree.  That is done with a priority queue. */
    /* Tree in worst case could have num_leaves + (num_leaves - 1) nodes */
    /* This is our bank from which to draw new nodes */
    node_bank = (prc_huff_node *)prc_malloc(ctx, sizeof(prc_huff_node) * num_leaves * (num_leaves - 1));
    if (node_bank == NULL)
    {
        prc_error(ctx, PRC_ERROR_MEMORY, "Allocation error in huffman_tree_calculation\n");
        return PRC_ERROR_MEMORY;
    }
    bank_index = 0;

    /* A list of the leave nodes. We use this to travese backwards for each leaf */
    leaf_nodes = (prc_huff_node **)prc_malloc(ctx, sizeof(prc_huff_node*) * num_leaves);
    if (leaf_nodes == NULL)
    {
        prc_error(ctx, PRC_ERROR_MEMORY, "Allocation error in huffman_tree_calculation\n");
        return PRC_ERROR_MEMORY;
    }

    /* Now make a priority queue that consists of prc_huff_node objects.  We will combine
       the two least frequent into a new node, assign its freq. as the sum of the two
       and place it back in the queue and its appropriate location.  In the PRC spec,
       only the leave values are stored along with their code and code length. A decoder
       tree, must be constructed from this information and used to decode the incoming data stream */
    /* Initialize */
    curr_node = &node_bank[0];
    curr_node->is_leaf = true;
    curr_node->left = NULL;
    curr_node->right = NULL;
    curr_node->value = huff_freq[0].values;
    curr_node->freq = huff_freq[0].freq;
    curr_node->prev = NULL;
    curr_node->next = NULL;
    curr_node->parent = NULL;
    priority_queue = curr_node;
    leaf_nodes[0] = curr_node;
    for (i = 1; i < num_leaves; i++)
    {
        new_node = &node_bank[i];
        new_node->is_leaf = true;
        new_node->left = NULL;
        new_node->right = NULL;
        new_node->value = huff_freq[i].values;
        new_node->freq = huff_freq[i].freq;
        new_node->next = NULL;
        new_node->prev = curr_node;
        new_node->parent = NULL;
        curr_node->next = new_node;
        curr_node = new_node;
        leaf_nodes[i] = new_node;
    }

    bank_index = num_leaves; /* This is where we must grab new nodes */
    queue_size = num_leaves; /* We continue until queue_size is 1 */

    /* Now we do the merging of the top two nodes and continue until size is 1*/
    while (queue_size > 1)
    {
        /* Take the two least frequent values and merge to a new node */
        new_node = &node_bank[bank_index];
        bank_index++;
        new_node->is_leaf = false;
        new_node->freq = priority_queue->freq + priority_queue->next->freq;
        new_node->left = priority_queue;
        new_node->right = priority_queue->next;
        new_node->left->parent = new_node;
        new_node->right->parent = new_node;
        new_node->prev = NULL;
        new_node->next = NULL;
        new_node->parent = NULL;

        /* Remove left and right from priority_queue */
        priority_queue = priority_queue->next->next;

        /* Add new node but only if there is at least one left */
        if (queue_size == 2)
        {
            /* Only two left, this is the last one */
            priority_queue = new_node;
            queue_size = queue_size - 1;
        }
        else
        {
            /* Find where to insert the new node based upon its frequency */
            int done = false;
            curr_node = priority_queue;
            while (!done)
            {
                if (curr_node->freq > new_node->freq)
                {
                    /* Insert in front of current node */
                    /* Be careful if we are at the start of the priority queue */
                    if (curr_node == priority_queue)
                    {
                        new_node->next = curr_node;
                        curr_node->prev = new_node;
                        priority_queue = new_node;
                        done = true;
                        queue_size = queue_size - 1;
                    }
                    else
                    {
                        /* Insert in middle of queue */
                        new_node->next = curr_node;
                        new_node->prev = curr_node->prev;
                        curr_node->prev->next = new_node;
                        curr_node->prev = new_node;
                        done = true;
                        queue_size = queue_size - 1;
                    }
                }
                else
                {
                    /* Make sure we are not at the end */
                    if (curr_node->next == NULL)
                    {
                        /* In this case put the new node at the end */
                        curr_node->next = new_node;
                        new_node->prev = curr_node;
                        done = true;
                        queue_size = queue_size - 1;
                    }
                    else
                    {
                        /* In this case we need to move to the next curr_node */
                        curr_node = curr_node->next;
                    }
                }
            }
        }
    }
    /* We made the tree. Its starting node is priority_queue. Get the leaf values, code values,
       and code lengths which we will return and encode in the PRC format. */

    /* How should the code values be stored?  eg. 011 or 110 bit order?  From Table 345 it appears that
       the data is the first decision is the bit on the left, last decision is bit on right. That is the way
       that this bit of code encodes the codes */

    /* Trace out the path to each leaf. We do this by traversing backwards from the leaf back to the top
       of the tree using the parent value in the nodes.   */
    max_code_length = 0;
    for (i = 0; i < num_leaves; i++)
    {
        curr_node = leaf_nodes[i];
        factor = 1;
        length = 0;
        code = 0;
        while (curr_node->parent != NULL)
        {
            parent = curr_node->parent;
            if (parent->left == curr_node)
            {
                /* adds a zero. so add nothing just move factor and length */
                length++;
                factor = factor * 2;
            }
            else
            {
                /* add a one in the proper bit position */
                code = code + factor;
                length++;
                factor = factor * 2;
            }
            curr_node = parent;
        }
        /* code created. We may need to reverse the bits. Might do this at write time. */
        leaf_nodes[i]->code = code;
        leaf_nodes[i]->code_length = length;
        if (length > max_code_length)
            max_code_length = length;
        leaf_values[i] = leaf_nodes[i]->value;
        code_length[i] = leaf_nodes[i]->code_length;
        code_values[i] = leaf_nodes[i]->code;
    }
    *uMaxCodeLength = max_code_length;
    *puLeafValues = leaf_values;
    *puLeafCodeLength = code_length;
    *puLeafCodeValues = code_values;

    prc_free(ctx, huff_freq);
    prc_free(ctx, node_bank);
    prc_free(ctx, leaf_nodes);

    return 0;
}

// uNumberOfBitsForValues is an input of the Huffman algorithm.
// For instance, when writing Edge_status_array in XXX4.7.8.7,
// this number is equal to 2.
int
huffman(prc_context *ctx, unsigned char* pcArray, unsigned int uCharArraySize, unsigned int uNumberOfBitsForValues,
    unsigned int **puHuffmanArray, unsigned int *uHuffmanArraySize)
{
    unsigned int uNumberOfLeaves, uMaxCodeLength, *puLeafValues, *puLeafCodeLength, *puLeafCodeValues;

    /* Note puLeafValues, puLeafCodeLength, and puLeafCodeValues must be freed */
    huffman_tree_calculation(ctx, pcArray, uCharArraySize, uNumberOfBitsForValues,
        &uNumberOfLeaves, &uMaxCodeLength, &puLeafValues, &puLeafCodeLength, &puLeafCodeValues);

    // here we have to allocate a dynamic array growing accordingly
    unsigned int uSize = 0, uMaxSize = 24 * uCharArraySize;
    uint8_t* pbBitArray = (uint8_t*) prc_malloc(ctx, uMaxSize);
    memset((void*) pbBitArray, 0, uMaxSize);

    // writing bit-by-bit the number of leaves
    unsigned int u, v;

    for (u = 0; u < uNumberOfBitsForValues + 1; u++)
        pbBitArray = add_bit_in_array(ctx, pbBitArray, uNumberOfLeaves & (1 << u) ? true : false, &uMaxSize, &uSize);

    // writing bit-by-bit the max code length on 8 bits
    for (u = 0; u < 8; u++)
        pbBitArray = add_bit_in_array(ctx, pbBitArray, uMaxCodeLength & (1 << u) ? true : false, &uMaxSize, &uSize);

    for (v = 0; v < uNumberOfLeaves; v++)
    {
        // writing leaf value (same values as in pcArray, in different order)
        for (u = 0; u < uNumberOfBitsForValues; u++)
            pbBitArray = add_bit_in_array(ctx, pbBitArray, puLeafValues[v] & (1 << u) ? true : false, &uMaxSize, &uSize);

        // writing leaf code length
        for (u = 0; u < uMaxCodeLength; u++)
            pbBitArray = add_bit_in_array(ctx, pbBitArray, puLeafCodeLength[v] & (1 << u) ? true : false, &uMaxSize, &uSize);

        // writing leaf code value
        for (u = 0; u < puLeafCodeLength[v]; u++)
            pbBitArray = add_bit_in_array(ctx, pbBitArray, puLeafCodeValues[v] & (1 << u) ? true : false, &uMaxSize, &uSize);
    }

    //block to insert
    for (u = 0; u < 32; u++)
        pbBitArray = add_bit_in_array(ctx, pbBitArray, uCharArraySize & (1 << u) ? true : false, &uMaxSize, &uSize);
    for (u = 0; u < uCharArraySize; u++)
    {
        unsigned uCodeValue, uCodeLength;

        if (get_huffman_code(pcArray[u], &uCodeValue, &uCodeLength, uNumberOfLeaves, puLeafValues, puLeafCodeLength, puLeafCodeValues) < 0)
            return PRC_ERROR_HUFFMAN;
        for (v = 0; v < uCodeLength; v++)
            pbBitArray = add_bit_in_array(ctx, pbBitArray, uCodeValue & (1 << v) ? true : false, &uMaxSize, &uSize);
    }
    //end block to insert

    unsigned int uBitsInUnsigned = sizeof(unsigned int) * 8;
    *uHuffmanArraySize = uSize % uBitsInUnsigned ? (uSize / uBitsInUnsigned) + 1 : uSize / uBitsInUnsigned;
    *puHuffmanArray = prc_malloc(ctx, sizeof(unsigned int) * *uHuffmanArraySize);

    unsigned int *puCurrent = *puHuffmanArray;
    unsigned int uTot = 0;

    for (u = 0; u < *uHuffmanArraySize - 1; u++, puCurrent++)
    {
        *puCurrent = 0;
        for (v = 0; v < uBitsInUnsigned; v++, uTot++)
            *puCurrent |= (pbBitArray[uTot] ? 1 : 0) << v;
    }

    v = 0;
    *puCurrent = 0;

    /* This is not shown in the spec. We have to write out the number of bits
       used for the last integer.   */
    unsigned int num_bits_so_far = uTot;

    for (u = uBitsInUnsigned * (*uHuffmanArraySize - 1); u < uSize; u++, uTot++, v++)
        *puCurrent |= (pbBitArray[uTot] ? 1 : 0) << v;
    unsigned int number_bits_used_in_last_integer = uTot - num_bits_so_far;

    prc_free(ctx, pbBitArray);

    prc_free(ctx, puLeafValues);
    prc_free(ctx, puLeafCodeLength);
    prc_free(ctx, puLeafCodeValues);

    return number_bits_used_in_last_integer;
}

void
write_bits(unsigned int uValue, int iBitsCount, prc_bit_state *state)
{
    size_t count = iBitsCount;
    size_t bit;

    while (count > 0)
    {
        bit = uValue & ((size_t) 1 << (count - 1));

        if (bit > 0)
        {
            *(state->ptr) = *(state->ptr) | state->bitmask;
        }
        else
        {
            *(state->ptr) = *(state->ptr) & ~state->bitmask;
        }

        state->bitmask >>= 1;
        if (state->bitmask == 0)
        {
            state->ptr++;
            state->bitmask = 0x80;
        }
        count -= 1;
    }
}

void
write_boolean(uint8_t val, prc_bit_state *state)
{
    write_bits(val, 1, state);

}

void
write_unsigned_integer_with_variable_bit_number(
    unsigned int uValue, unsigned int uBitNumber, prc_bit_state *state)
{
    unsigned int u;

    for (u = 0; u < uBitNumber; u++)
    {
        if (uValue >= 1 << (uBitNumber - 1 - u))  // Possible signed/unsigned mismatch
        {
            write_boolean(1, state);
            uValue -= 1 << (uBitNumber - 1 - u);
        }
        else
        {
            write_boolean(0, state);
        }
    }
}

void
write_integer_with_variable_bit_number(int iValue, unsigned int uBitNumber, prc_bit_state *state)
{
    write_boolean(iValue < 0, state);
    write_unsigned_integer_with_variable_bit_number(abs(iValue), uBitNumber - 1, state);
}

/* This one is NOT defined in the spec. I assume its implementation is clear from the name */
void
write_uncompressed_unsigned_integer(unsigned int uValue, prc_bit_state *state)
{
    int k;

    for (k = 0; k < 4; k++)
    {
        write_bits(uValue & 0xFF, 8, state);
        uValue >>= 8;
    }
}

void
write_unsigned_integer(unsigned int uValue, prc_bit_state *state)
{
    for (;;)
    {
        if (uValue == 0)
        {
            write_bits(0, 1, state);
            return;
        }
        write_bits(1, 1, state);
        write_bits(uValue & 0xFF, 8, state);
        uValue >>= 8;
    }
}

void
write_integer(int iValue, prc_bit_state *state)
{
    if (iValue == 0)
    {
        write_bits(0, 1, state);
        return;
    }
    for (;;)
    {
        int loc = iValue & 0xFF;
        write_bits(1, 1, state);
        write_bits(loc, 8, state);
        iValue >>= 8;
        if (((iValue == 0) && ((loc & 0x80) == 0)) ||
            ((iValue == -1) && ((loc & 0x80) != 0)))
        {
            write_bits(0, 1, state);
            return;
        }
    }
}

unsigned int
get_number_bits_to_store_unsigned_integer(unsigned int uValue)
{
    unsigned uNbBit = 1;
    unsigned uTemp = 1;

    while (uValue > uTemp)
    {
        uTemp = uTemp << 1 | 1;
        uNbBit++;
    }
    return uNbBit;
}

unsigned int
get_number_bits_to_store_unsigned_integer2(unsigned int  uValue)
{
    unsigned uNbBit = 1;
    unsigned uTemp = 1;

    while (uValue > uTemp)
    {
        uTemp *= 2;
        uNbBit++;
    }
    return uNbBit;
}

void
write_character(uint8_t data, prc_bit_state *state)
{
    uint32_t k;
    uint32_t val;

    for (k = 0; k < 8; k++)
    {
        val = data & (1 << (7 - k));

        if (val == 0)
            write_bits(0, 1, state);
        else
            write_bits(1, 1, state);
    }
}

/* 10.6 Writecharacterarray.  This is poorly defined in the spec.
 * one of the parameters uBitNumber, is not used in the spec
 * implementation.  The value Numberofbits_used_in_last_integer
 * is not defined, nor set.  And the method WriteUncompressedUnsignedInteger
 * is not defined.  Nor is HuffmanCompression(); */
int
write_character_array(prc_context *ctx,
    unsigned char* pcArray, unsigned int uCharArraySize,
    unsigned int uBitNumber, uint8_t bWriteCompressStrategy,
    prc_bit_state *state, int WriteCompress_Strategy)
{
    unsigned int u;
    int bIsCompressed = 1; // Just always compress for now.

    if (WriteCompress_Strategy)
        write_boolean(bIsCompressed, state);

    if (bIsCompressed)
    {
        // calling Huffman to create
        // puHuffmanArray and uHuffmanArraySize
        unsigned int *puHuffmanArray;
        unsigned int uHuffmanArraySize;
        unsigned int Numberofbits_used_in_last_integer;

        if (huffman(ctx, pcArray, uCharArraySize, uBitNumber, &puHuffmanArray, &uHuffmanArraySize) < 0)
            return PRC_ERROR_HUFFMAN;

        write_unsigned_integer(uHuffmanArraySize, state);

        for (u = 0; u < uHuffmanArraySize; u++)
            write_uncompressed_unsigned_integer(puHuffmanArray[u], state);
        Numberofbits_used_in_last_integer = 32;  /* Don't know about this */
        write_unsigned_integer(Numberofbits_used_in_last_integer, state);
    }
    else
    {
        write_unsigned_integer(uCharArraySize, state);
        for (u = 0; u < uCharArraySize; u++)
            write_character(pcArray[u], state);
    }
    return 0;
}

/* 10.9 Writecompressedindicearray */
int
write_compressed_indice_array(prc_context *ctx, int *piArray, unsigned int uIntArraySize, uint8_t bWriteCompressStrategy, prc_bit_state *state)
{
    unsigned int u;
    int iTemp;
    char cTemp;
    char cLastBitCount;
    int code;
    char *pcArray = (char *)prc_malloc(ctx, uIntArraySize);

    if (pcArray == NULL)
    {
        prc_error(ctx, PRC_ERROR_MEMORY, "Allocation error in write_compressed_indice_array\n");
        return PRC_ERROR_MEMORY;
    }

    pcArray[0] = (char)get_number_bits_to_store_unsigned_integer(abs(piArray[0])) + 1;
    cLastBitCount = pcArray[0];

    for (u = 1; u < uIntArraySize; u++)
    {
        iTemp = piArray[u] - piArray[u - 1];
        cTemp = (char)get_number_bits_to_store_unsigned_integer(abs(iTemp)) + 1;
        pcArray[u] = cTemp - cLastBitCount;
        cLastBitCount = cTemp;
    }

    code = write_character_array(ctx, (unsigned char *)pcArray, uIntArraySize, 6, bWriteCompressStrategy, state, true); // Note the false setting for indice_array
    if (code < 0)
    {
        prc_error(ctx, code, "Failed in write_character_array\n");
        return code;
    }

    char cBitCount = pcArray[0];
    write_integer_with_variable_bit_number(piArray[0], cBitCount, state);

    for (u = 1; u < uIntArraySize; u++)
    {
        cBitCount += (int)pcArray[u];
        write_integer_with_variable_bit_number(piArray[u] - piArray[u - 1], cBitCount, state);
    }

    prc_free(ctx, pcArray);
    return 0;
}

/* 10.8 WriteCompressedIntegerArray */
/*
void
write_compressed_integer_array(int *piArray, unsigned int uIntArraySize)
{
    unsigned int u;
    int bWriteCompressStrategy = 1;
    char *pcArray = (char *)prc_malloc(ctx, uIntArraySize);

    for (u = 1; u < uIntArraySize; u++)
        pcArray[u] = (char) GetNumberOfBitsUsedToStoreInteger(piArray[u]);

    WriteCharacterArray(pcArray,uIntArraySize, 6, bWriteCompressStrategy);

    for( u=0;u < uIntArraySize; u++)
        WriteIntegerWithVariableBitNumber(piArray[u], pcArray[u]);

    prc_free(ctx, pcArray);

} */

int
test_write_compressed_indice_array(prc_context *ctx)
{
    int data_in[] = { 1, 3, 2, 7, 1, 4, 3, 7, 2, 0 };
    uint32_t data_size;
    prc_bit_state state;
    uint8_t* bit_buffer = (uint8_t*) prc_malloc(ctx, 4096);
    uint32_t* output;
    uint32_t output_size;
    int code;

    if (bit_buffer == NULL)
    {
        prc_error(ctx, PRC_ERROR_MEMORY, "Allocation error in test_write_compressed_indice_array\n");
        return PRC_ERROR_MEMORY;
    }
    data_size = 10;

    prc_init_bit_state(ctx, &state, bit_buffer, data_size);
    code = write_compressed_indice_array(ctx, data_in, data_size, 1, &state);
    if (code < 0)
    {
        prc_error(ctx, code, "Failed in write_compressed_indice_array\n");
        return code;
    }

    prc_init_bit_state(ctx, &state, bit_buffer, data_size);
    output = (uint32_t *)prc_bitread_compressed_indice_array(ctx, &state, &output_size, false, 0);
    return 0;
}

#ifdef TESTHUFFMAN

/* Writing code taken from spec to test our reading codes */
int
test_huffman(prc_context *ctx)
{
    //char data[] = { 1, 3, 5, 7, 7, 9, 5, 3, 3 };
    /* 255 freq 4, 2 freq 3, 0 freq 2, 1 has freq 1 */
   // unsigned char data[] = { 2, 1, 255, 2, 0, 255, 255, 2, 0, 255 };
    char data[] = { 1, -1, 1, -2, 0, 1, 2, 0, -1 };

    unsigned int array_size;
    unsigned int *huffman_array;
    prc_bit_state huff_state;
    uint32_t num_leaves;
    uint32_t max_code_length;
    uint32_t *leaf_values;
    uint32_t *code_length;
    uint32_t *code_values;
    uint8_t num_bits = 6;
    uint32_t k;
    int number_bits_used_in_last_integer;
    uint32_t data_size;
    prc_bit_state state;
    int32_t *data_read;
    uint32_t data_size_read;
    uint32_t number_bits_used_in_last_integer_read;

    uint8_t *bit_buffer = (uint8_t *)prc_malloc(ctx, 4096);
    if (bit_buffer == NULL)
    {
        prc_error(ctx, PRC_ERROR_MEMORY, "Allocation error in test_write_compressed_indice_array\n");
        return PRC_ERROR_MEMORY;
    }

    number_bits_used_in_last_integer = huffman(ctx, &data[0], 9, num_bits, &huffman_array, &array_size);
    if (number_bits_used_in_last_integer < 0)
        return PRC_ERROR_HUFFMAN;

    prc_init_bit_state(ctx, &state, bit_buffer, 4096);


    /* Write out the huffman array and the number of bits used in the last integer. */
    write_unsigned_integer(array_size, &state);
    for (k = 0; k < array_size; k++)
    {
        write_uncompressed_unsigned_integer(huffman_array[k], &state);
    }
    write_unsigned_integer(number_bits_used_in_last_integer, &state);

    /* Reset bit stream for reading */
    prc_init_bit_state(ctx, &state, bit_buffer, 4096);
    data_read = prc_bitread_compressed_indice_array(ctx, &state, &data_size_read, false); /* False as I did not write the compression bit */
    number_bits_used_in_last_integer_read = prc_bitread_uint32(ctx, &state);


#if 0
    /* Now take the huffman array apart into the values, codes and code lengths */
    /* Everything is bit compressed */

    prc_init_huff_bit_state(ctx, &huff_state, (uint8_t *)huffman_array);

    /* First read in huffman array */
    num_leaves = prc_bitread_huff_data(ctx, &huff_state, num_bits + 1);
    max_code_length = prc_bitread_huff_data(ctx, &huff_state, 8);

    leaf_values = (uint32_t *)prc_malloc(ctx, sizeof(uint32_t) * num_leaves);
    if (leaf_values == NULL)
    {
        prc_error(ctx, PRC_ERROR_MEMORY, "Allocation error in test_huffman\n");
        return PRC_ERROR_MEMORY;
    }

    code_length = (uint32_t *)prc_malloc(ctx, sizeof(uint32_t) * num_leaves);
    if (code_length == NULL)
    {
        prc_error(ctx, PRC_ERROR_MEMORY, "Allocation error in test_huffman\n");
        return PRC_ERROR_MEMORY;
    }

    code_values = (uint32_t *)prc_malloc(ctx, sizeof(uint32_t) * num_leaves);
    if (code_values == NULL)
    {
        prc_error(ctx, PRC_ERROR_MEMORY, "Allocation error in test_huffman\n");
        return PRC_ERROR_MEMORY;
    }

    for (k = 0; k < num_leaves; k++)
    {
        leaf_values[k] = prc_bitread_huff_data(ctx, &huff_state, num_bits);
        code_length[k] = prc_bitread_huff_data(ctx, &huff_state, max_code_length);
        code_values[k] = prc_bitread_huff_data(ctx, &huff_state, code_length[k]);
    }
    return 0;
#endif
}

#endif
