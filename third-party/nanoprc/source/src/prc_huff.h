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

#ifndef PRC_HUFF_H
#define PRC_HUFF_H

/* Structure of the values and their frequencies */
typedef struct prc_huff_freq_data_s prc_huff_freq_data;
struct prc_huff_freq_data_s
{
    char freq;
    unsigned char values;
};

typedef struct prc_huff_node_test_s prc_huff_node_test;
struct prc_huff_node_test
{
    unsigned int freq;
    unsigned char value;
    prc_huff_node_test *left;
    prc_huff_node_test *right;
};

int test_huffman(prc_context *ctx);
int test_write_compressed_indice_array(prc_context *ctx);
unsigned int get_number_bits_to_store_unsigned_integer2(unsigned int  uValue);

#endif
