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

#include "prc_api_debug.h"
#include "prc_internal_api.h"
#include "prc_data.h"
#include <stdio.h>

/* Debug method to dump the tree in the PRC data */
static void
prc_api_print_tree_prc(prc_context *ctx, prc_api_data data)
{
    prc_data *data_in = (prc_data *)data;
    prc_asm_file_structure_tree *tree_in = data_in->file_struct->tree;
    uint32_t num_products = tree_in->product_count;
    uint32_t num_parts = tree_in->parts_count;
    int k, j;
    prc_asm_product_occurrence *product;
    uint32_t offset = num_products - 1;
    prc_references_of_product_occurrence product_refs;
    uint32_t biased_part_index;
    uint32_t number_child_product_occurrences;
    prc_asm_parts_definition *part;
    uint32_t num_rep_items;
    uint32_t tess_index_ri;

    /* Now we have to go through the products. Products can reference
       single products or multiple parts.  Parts can only reference tessellations */
    for (k = 0; k < num_products; k++)
    {
        product = &tree_in->products[offset];

        product_refs = product->references_product_occurrence;
        biased_part_index = product_refs.biased_index_part - 1;
        number_child_product_occurrences = product_refs.number_of_child_product_occurrences;

        /* Print out all these details */
        if (product->base.base.name.name.string != NULL)
            printf("\nProduct %d: %s\n", offset, product->base.base.name.name.string);
        else
            printf("Product %d: No name\n", offset);
        printf("Biased index part: %d\n", biased_part_index);
        printf("Number of child product occurrences: %d\n", number_child_product_occurrences);

        /* Now we have to go through the child product occurrences */
        for (j = 0; j < number_child_product_occurrences; j++)
        {
            printf("Child product occurrence %d: %d\n", j, product_refs.index_child_occurrence[j]);
        }
        offset--;
    }

    printf("\nParts:\n");

    /* Now we have to go through the parts. Parts can reference tessellations */
    for (k = 0; k < num_parts; k++)
    {
        part = &tree_in->parts[k];
        num_rep_items = part->num_rep_items;
        if (num_rep_items > 0)
        {
            if (part->base.base.name.name.string != NULL)
                printf("\nPart %d: %s\n", k, part->base.base.name.name.string);
            else
                printf("\nPart %d: No name\n", k);
            printf("\nPart %d: %s\n", k, part->base.base.name.name.string);
            for (j = 0; j < num_rep_items; j++)
            {
                tess_index_ri = part->rep_items[j].item_content.biased_index_tessellation - 1;
                if (tess_index_ri >= 0)
                {
                    printf("Tessellation %d\n", tess_index_ri);
                }
                else
                {
                    printf("No tessellation\n");
                }
            }
        }
        else
        {
            printf("\nPart %d: No representation items\n", k);
        }
    }
}

static void
prc_api_print_dots(prc_context *ctx, int level)
{
    uint32_t k;

    for (k = 0; k < level; k++)
        printf(".");
}

/* This is the recursive function that deals with the representation items */
static void
prc_api_print_tree_ri_api(prc_context *ctx, prc_api_part *part, int level)
{
    uint32_t num_children = part->num_rep_items;
    prc_api_part *children = part->rep_items;
    uint32_t k;

    printf("\n");

    for (k = 0; k < num_children; k++)
    {
        prc_api_print_dots(ctx, level);
        if (part->rep_items[k].name != NULL)
            printf("RI name: %s\n", part->rep_items[k].name);
        else
            printf("RI name: No name\n");

        if (part->rep_items[k].biased_tess_index > 0)
        {
            prc_api_print_dots(ctx, level);
            printf("Tessellation biased index %d\n",
                part->rep_items[k].biased_tess_index);
        }
        if (part->rep_items[k].biased_style_index > 0)
        {
            prc_api_print_dots(ctx, level);
            printf("Tessellation biased style %d\n",
                part->rep_items[k].biased_style_index);
        }

        prc_api_print_dots(ctx, level);
        if (part->rep_items[k].tess != NULL)
        {
            switch (part->rep_items[k].tess->type)
            {
            case PRC_API_TESS_3D:
                printf("Tessellation type: 3D\n");
                break;
            case PRC_API_TESS_3D_Compressed:
                printf("Tessellation type: 3D Compressed\n");
                break;
            case PRC_API_TESS_3D_Wire:
                printf("Tessellation type: 3D Wire\n");
                break;
            case PRC_API_TESS_MarkUp:
                printf("Tessellation type: MarkUp\n");
                break;
            default:
                printf("Tessellation type: Unknown\n");
                break;
            }
        }
        if (part->rep_items[k].num_rep_items > 0)
        {
            prc_api_print_dots(ctx, level);
            printf("RI item %u has %zu RI items\n", k, part->rep_items[k].num_rep_items);
            prc_api_print_tree_ri_api(ctx, part->rep_items + k, level + 1);
        }
    }
}

/* Debug method to dump the tree we create */
PRC_EXPORT void
prc_api_print_tree(prc_context *ctx, prc_api_product *product, int level)
{
    uint32_t k, j;
    uint32_t num_children = product->num_children;
    prc_api_product *children = product->children;

    printf("\n");
    prc_api_print_dots(ctx, level);
    if (product->name != NULL)
        printf("Product name: %s\n", product->name);
    else
        printf("Product name: No name\n");

    /* Display type */
    prc_api_print_dots(ctx, level);
    if (product->type == PRC_API_NODE_PRODUCT)
    {
        printf("Type: Product\n");
    }
    else if (product->type == PRC_API_NODE_PART)
    {
        printf("Type: Part\n");
    }
    else if (product->type == PRC_API_NODE_MARKUP)
    {
        printf("Type: Markup\n");
    }
    else
    {
        printf("Unknown type\n");
    }

    /* Display if it has a tessellation */
    if (product->part != NULL && product->part->tess != NULL)
    {
        prc_api_print_dots(ctx, level);
        switch (product->part->tess->type)
        {
        case PRC_API_TESS_3D:
            printf("Tessellation type: 3D\n");
            break;
        case PRC_API_TESS_3D_Compressed:
            printf("Tessellation type: 3D Compressed\n");
            break;
        case PRC_API_TESS_3D_Wire:
            printf("Tessellation type: 3D Wire\n");
            break;
        case PRC_API_TESS_MarkUp:
            printf("Tessellation type: MarkUp\n");
            break;
        default:
            printf("Tessellation type: Unknown\n");
            break;
        }
        prc_api_print_dots(ctx, level);
        printf("Tessellation biased index %d\n",
            product->part->biased_tess_index);
        prc_api_print_dots(ctx, level);
        printf("Tessellation biased style %d\n",
            product->part->biased_style_index);
    }
    else if (product->part != NULL)
    {
        prc_api_print_dots(ctx, level);
        if (product->part->name != NULL)
            printf("Part name: %s\n", product->part->name);
        else
            printf("Part name: No name\n");

        if (product->part->num_rep_items > 0)
        {
            prc_api_print_dots(ctx, level);
            printf("Part has %d representation items\n", (uint32_t) product->part->num_rep_items);
            prc_api_print_tree_ri_api(ctx, product->part, level + 1);
        }
        else
        {
            prc_api_print_dots(ctx, level);
            printf("Part has no representation items\n");
        }
        if (product->part->biased_tess_index > 0)
        {
            prc_api_print_dots(ctx, level);
            printf("Tessellation biased index %d\n",
                product->part->biased_tess_index);
        }
        if (product->part->biased_style_index > 0)
        {
            prc_api_print_dots(ctx, level);
            printf("Tessellation biased style %d\n",
                product->part->biased_style_index);
        }
    }
    else
    {
        prc_api_print_dots(ctx, level);
        printf("No tessellation\n");
    }

    /* Display if transformation is identity */
    prc_api_print_dots(ctx, level);
    if (product->location.is_identity)
    {
        printf("Transformation is identity\n");
    }
    else
    {
        printf("Transformation is not identity\n");

        prc_api_print_dots(ctx, level);
        printf("Matrix:\n");
        for (k = 0; k < 4; k++)
        {
            for (uint32_t j = 0; j < 4; j++)
            {
                printf("%f ", product->location.matrix[k * 4 + j]);
            }
            printf("\n");
        }
    }

    /* Display if this has any markups */
    prc_api_print_dots(ctx, level);

    if (product->num_markups > 0)
    {
        printf("Markups:\n");
        for (k = 0; k < product->num_markups; k++)
        {
            prc_api_print_dots(ctx, level);
            printf("Markup %d: %s\n", k, product->markup[k].name);
        }
    }
    else
    {
        printf("No markups\n");
    }

    /* Do recursion on the children */
    for (k = 0; k < num_children; k++)
    {
        prc_api_print_tree(ctx, children + k, level + 1);
    }
}

PRC_EXPORT void
prc_api_print_error_stack(prc_context *ctx)
{
    prc_print_error_stack(ctx);
}

/* write-path diagnostics: prc_api_write_print_session() — TBD */
