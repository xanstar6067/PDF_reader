/* PaperDesk PRC regression tests, GNU AGPL version 3 or later.
   Generated fixtures contain no user drawings. */
#include <math.h>
#include <process.h>
#include <stdio.h>
#include <string.h>
#include "prc_api.h"
#include "source/src/prc_data.h"
#include "source/src/prc_internal_api.h"

static unsigned assertions;
static void check(int condition, const char *description)
{
    if (!condition) { fprintf(stderr, "FAIL: %s\n", description); exit(1); }
    ++assertions;
}
static int near(double a, double b) { return fabs(a - b) < 1e-9; }

static void test_file_scoped_geometry(prc_context *ctx)
{
    prc_data data = {0};
    prc_filestructure files[2] = {0};
    prc_asm_file_structure_tessellation tessellations[2] = {0};
    prc_tess meshes[2] = {0};
    prc_tess_3d surfaces[2] = {0};
    prc_api_part parts[3] = {0};
    prc_api_child_reserve reserve = {0};
    prc_api_product model = {0};
    uint32_t nt = 0, nl = 0, ne = 0;
    data.file_structure_count = 2;
    data.file_struct = files;
    for (unsigned i = 0; i < 2; ++i) {
        files[i].tessellation = tessellations + i;
        tessellations[i].tess_count = 1;
        tessellations[i].tess = meshes + i;
        meshes[i].tess_type = PRC_TYPE_TESS_3D;
        meshes[i].tess_3d = surfaces + i;
        parts[i].biased_tess_index = 1; // Same file-local index, different resources.
        parts[i].tess_file_index = i;
    }
    parts[2] = parts[1]; // Another occurrence reuses the second resource.
    reserve.parts = parts;
    reserve.num_parts = 3;
    model.reserved = &reserve;
    check(prc_api_get_number_tessellations(ctx, &data, &model, &nt, &nl, &ne, 0) == 0,
        "enumerate geometry spanning two file structures");
    check(nt >= 2 && nt <= 3 && nl == 0 && ne == 0, "distinct resources despite identical local indices");
    check(parts[0].part_detail_index == 0 && parts[1].part_detail_index == 1,
        "first occurrence points to its own geometry");
    check(parts[2].part_detail_index < nt &&
        data.part_details[parts[2].part_detail_index].tess_file_index == 1 &&
        data.part_details[parts[2].part_detail_index].tessellation_index == 0,
        "shared occurrence preserves its resource mapping");
    prc_free(ctx, data.part_details);
}

static void test_local_coordinates(prc_context *ctx)
{
    prc_data data = {0};
    prc_filestructure file = {0};
    prc_asm_file_structure_globals globals = {0};
    prc_ri_coordinate_system coordinates[2] = {0};
    prc_api_part part = {0};
    prc_api_transform transform;
    double point[3] = {1, 1, 1}, result[3];
    data.file_structure_count = 1;
    data.file_struct = &file;
    file.globals = &globals;
    globals.global_data.ref_coord_count = 2;
    globals.global_data.ref_coords = coordinates;
    part.biased_local_coordinate_index = 1;
    coordinates[0].item_content.biased_index_local_coordinate_system = 2;
    coordinates[0].transform.transformation_type = PRC_TYPE_MISC_CartesianTransformation;
    coordinates[0].transform.cartesian.behavior = PRC_TRANSFORMATION_Translate;
    coordinates[0].transform.cartesian.translation = (prc_vec3){1, 2, 3};
    coordinates[1].transform.transformation_type = PRC_TYPE_MISC_CartesianTransformation;
    coordinates[1].transform.cartesian.behavior = PRC_TRANSFORMATION_Translate |
        PRC_TRANSFORMATION_Rotate | PRC_TRANSFORMATION_NonUniformScale;
    coordinates[1].transform.cartesian.translation = (prc_vec3){5, -2, 1};
    coordinates[1].transform.cartesian.rotation[0] = (prc_vec3){0, 1, 0};
    coordinates[1].transform.cartesian.rotation[1] = (prc_vec3){-1, 0, 0};
    coordinates[1].transform.cartesian.non_uniform_scale = (prc_vec3){2, 3, 4};
    check(prc_api_part_local_transform(ctx, &data, &part, &transform) == 0,
        "resolve nested representation coordinates");
    prc_api_transform_point(&transform, point, result);
    // First translate to (2,3,4), then scale to (4,9,16), rotate to (-9,4,16),
    // then translate to (-4,2,17). Checks order and scaling rotated basis axes.
    check(near(result[0], -4) && near(result[1], 2) && near(result[2], 17),
        "nested translation, rotation and nonuniform scaling retain physical position");
    coordinates[1].item_content.biased_index_local_coordinate_system = 1;
    check(prc_api_part_local_transform(ctx, &data, &part, &transform) < 0,
        "cyclic coordinate references rejected");
    coordinates[1].item_content.biased_index_local_coordinate_system = 3;
    check(prc_api_part_local_transform(ctx, &data, &part, &transform) < 0,
        "out-of-range coordinate references rejected");
    part.biased_local_coordinate_index = 0;
    check(prc_api_part_local_transform(ctx, &data, &part, &transform) == 0 && transform.is_identity,
        "representation with no local coordinates uses identity");
}

static prc_api_product *find_product(prc_api_product *node, const char *name)
{
    if (node->name && strcmp(node->name, name) == 0) return node;
    for (uint32_t i = 0; i < node->num_children; ++i) {
        prc_api_product *found = find_product(node->children + i, name);
        if (found) return found;
    }
    return NULL;
}

static void test_prototype_placements(prc_context *ctx, const char *fixture)
{
    double positions[] = {0,0,0, 1,0,0, 0,1,0};
    uint32_t indices[] = {0,1,2}, face_count = 1;
    prc_api_write_tessellation tess = {0};
    prc_api_write_rep_item ri = {0};
    prc_api_write_node root = {0}, children[4] = {0};
    prc_api_write_node *child_pointers[4];
    const char *names[] = {"Prototype", "Instance", "Identity", "Inherited"};
    tess.kind = PRC_API_WRITE_TESS_KIND_TRIANGLES;
    tess.positions = positions; tess.num_positions = 3;
    tess.tri_indices = indices; tess.num_triangles = 1;
    tess.face_tri_counts = &face_count; tess.num_faces = 1;
    ri.kind = PRC_API_WRITE_RI_SURFACE; ri.biased_tessellation_index = 1;
    root.name = "Assembly"; root.children = child_pointers; root.num_children = 4;
    root.has_transform = 1;
    // Parent rotates 90 degrees around Z and then translates by (50,100,0).
    root.transform[1] = 1; root.transform[4] = -1;
    root.transform[10] = root.transform[15] = 1;
    root.transform[12] = 50; root.transform[13] = 100;
    for (unsigned i = 0; i < 4; ++i) {
        child_pointers[i] = children + i;
        children[i].name = names[i]; children[i].part_name = names[i];
        children[i].rep_items = &ri; children[i].num_rep_items = 1;
        children[i].bbox_max[0] = children[i].bbox_max[1] = 1;
        for (unsigned j = 0; j < 16; ++j) children[i].transform[j] = j % 5 == 0 ? 1 : 0;
    }
    children[0].has_transform = children[1].has_transform = children[2].has_transform = 1;
    children[0].transform[12] = 1000;
    children[1].transform[12] = 10;
    children[2].is_identity = 1;
    check(prc_api_write_prc_file(ctx, fixture, "Regression", &root, &tess, 1) == 0,
        "generate a PRC fixture without external drawings");
    prc_api_data opaque = prc_api_open_contents(ctx, fixture);
    check(opaque != NULL, "parse generated PRC fixture");
    prc_data *data = (prc_data *)opaque;
    prc_asm_file_structure_tree *raw_tree = data->file_struct[0].tree;
    uint32_t prototype = UINT32_MAX;
    for (uint32_t i = 0; i < raw_tree->product_count; ++i)
        if (strcmp((const char *)raw_tree->products[i].base.base.name.name.string, "Prototype") == 0) prototype = i;
    check(prototype != UINT32_MAX, "fixture exposes prototype geometry");
    // The simple public writer emits independent parts. Add prototype links to
    // its parsed data so the same real tree parser exercises CAD instance rules.
    for (uint32_t i = 0; i < raw_tree->product_count; ++i) {
        prc_asm_product_occurrence *product = raw_tree->products + i;
        const char *name = (const char *)product->base.base.name.name.string;
        if (strcmp(name, "Instance") == 0 || strcmp(name, "Identity") == 0 || strcmp(name, "Inherited") == 0) {
            product->references_product_occurrence.biased_index_part = 0;
            product->references_product_occurrence.biased_index_prototype = prototype + 1;
            product->references_product_occurrence.prototype_in_same_file_structure.flag = 1;
            if (strcmp(name, "Identity") == 0) {
                // The writer optimizes identity away. A CAD instance may store
                // it explicitly, which suppresses prototype inheritance.
                product->has_transform = 1;
                product->location.transformation_type = PRC_TYPE_MISC_CartesianTransformation;
                memset(&product->location.cartesian, 0, sizeof(product->location.cartesian));
            }
        }
    }
    uint32_t np = 0, nproducts = 0, nm = 0;
    prc_api_product *tree = NULL;
    check(prc_api_prep_model_tree(ctx, data, &np, &nproducts, &nm) == 0 &&
        prc_api_create_model_tree(ctx, data, &tree, np, nproducts, nm) == 0,
        "resolve prototype-based assembly tree");
    prc_api_product *instance = find_product(tree, "Instance");
    prc_api_product *identity = find_product(tree, "Identity");
    prc_api_product *inherited = find_product(tree, "Inherited");
    check(instance && identity && inherited, "all assembly instances preserved");
    check(instance->location_set && near(instance->location.matrix[12], 10),
        "instance position overrides prototype position instead of adding it");
    check(identity->location.is_identity, "explicit identity overrides prototype position");
    check(inherited->location_set && near(inherited->location.matrix[12], 1000),
        "missing instance position inherits prototype position");
    prc_api_release_data(ctx, opaque, NULL, 0, NULL, 0, NULL, 0, tree);
}

static void test_worker(const char *worker, const char *fixture)
{
    char *output = malloc(strlen(fixture) + 6);
    char *input_argument = malloc(strlen(fixture) + 3);
    char *output_argument = malloc(strlen(fixture) + 8);
    check(output && input_argument && output_argument, "allocate worker paths");
    sprintf(output, "%s.pd3d", fixture);
    // Windows CRT spawn joins argv into a command line; quote paths with spaces.
    sprintf(input_argument, "\"%s\"", fixture);
    sprintf(output_argument, "\"%s\"", output);
    check(_spawnl(_P_WAIT, worker, "PaperDeskModelWorker", input_argument, output_argument, NULL) == 0,
        "standalone worker decodes generated PRC assembly");
    free(input_argument); free(output_argument);
    FILE *file = fopen(output, "rb");
    check(file != NULL, "open worker output");
    char magic[8]; uint32_t flags, count;
    check(fread(magic, 1, 8, file) == 8 && memcmp(magic, "PD3D0001", 8) == 0 &&
        fread(&flags, 4, 1, file) == 1 && fread(&count, 4, 1, file) == 1,
        "valid worker output header");
    check(flags == 0 && count == 4, "all placed occurrences emitted without fallback warning");
    unsigned seen = 0;
    for (uint32_t i = 0; i < count; ++i) {
        uint32_t length, vertices; char name[1024]; float triangle[30];
        check(fread(&length, 4, 1, file) == 1 && length < sizeof(name) &&
            fread(name, 1, length, file) == length, "read occurrence name");
        name[length] = 0;
        check(fread(&vertices, 4, 1, file) == 1 && vertices == 3 &&
            fread(triangle, sizeof(float), 30, file) == 30, "one triangle per placed occurrence");
        double expected_y;
        if (strstr(name, " / Prototype")) { expected_y = 1100; seen |= 1; }
        else if (strstr(name, " / Instance")) { expected_y = 110; seen |= 2; }
        else if (strstr(name, " / Identity")) { expected_y = 100; seen |= 4; }
        else if (strstr(name, " / Inherited")) { expected_y = 100; seen |= 8; }
        else { check(0, "unexpected occurrence"); expected_y = 0; }
        check(near(triangle[0], 50) && near(triangle[1], expected_y) && near(triangle[2], 0),
            "worker composes parent rotation/translation with child placement");
        check(near(triangle[20], 49) && near(triangle[21], expected_y) && near(triangle[22], 0),
            "worker transforms geometry of a shared resource for every occurrence");
        check(near(triangle[3], 0) && near(triangle[4], 0) && near(triangle[5], 1),
            "transformed triangle retains correct surface normal");
    }
    check(seen == 15 && fgetc(file) == EOF, "all occurrences present with no trailing output");
    fclose(file); free(output);
}

int main(int argc, char **argv)
{
    if (argc != 3) return 2;
    prc_context *ctx = prc_api_new_context(NULL);
    check(ctx != NULL, "create PRC context");
    test_file_scoped_geometry(ctx);
    test_local_coordinates(ctx);
    test_prototype_placements(ctx, argv[1]);
    prc_api_release_context(ctx);
    test_worker(argv[2], argv[1]);
    printf("PASS: %u PRC regression assertions\n", assertions);
    return 0;
}
