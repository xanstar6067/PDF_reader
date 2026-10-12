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

#ifndef PRC_SCHEMA_H
#define PRC_SCHEMA_H

#include <stdint.h>
#include "prc_data.h"
#include "prc_bit.h"

#define SCHEMA_DICT_ENTRY_NOTFOUND NULL
#define SCHEMA_RECURSION_MAX 10

/* The schema can declare and reference variables through
   the use of an index. Store these in a dictionary */
typedef struct schema_dict_s schema_dict;
struct schema_dict_s
{
    uint32_t key;
    double value;
    schema_dict *next;
};

int prc_execute_schema(prc_context *ctx, prc_bit_state *bit_state, prc_entity_schema *schema, int *recursion);
prc_entity_schema* prc_get_schema(prc_context *ctx, uint32_t number);

#endif
