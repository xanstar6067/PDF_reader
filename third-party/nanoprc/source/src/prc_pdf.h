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

#include <stdint.h>
#include "prc_context.h"

#ifndef PRC_PDF_H
#define PRC_PDF_H

#define PDF_MAX_DICT_VALUE 256

#define PDF_NUM_XREF_OBJECTS_INIT 10

#define PDF_XREF_ENTRY_LENGTH 20
#define PDF_TYPE_NAME "/Type"
#define PDF_TYPE_NAME_LEN 5
#define PDF_TYPE_3D "/3D"
#define PDF_TYPE_3D_LEN 3
#define PDF_LENGTH_NAME "/Length"
#define PDF_LENGTH_NAME_LEN 7
#define PDF_STREAM_NAME "stream"
#define PDF_STREAM_NAME_LEN 6
#define PDF_ENDSTREAM_NAME "endstream"
#define PDF_ENDSTREAM_NAME_LEN 9
#define PDF_ENDOBJ_NAME "endobj"
#define PDF_ENDOBJ_NAME_LEN 6
#define PDF_FLATEDECODE_NAME "/FlateDecode"
#define PDF_FLATEDECODE_NAME_LEN 12
#define PDF_FILTER_NAME "/Filter"
#define PDF_FILTER_NAME_LEN 7
#define PDF_SUBTYPE_NAME "/Subtype"
#define PDF_SUBTYPE_NAME_LEN 8
#define PDF_XREF_NAME "xref"
#define PDF_XREF_NAME_LEN 4
#define PDF_TRAILER_NAME "trailer"
#define PDF_TRAILER_NAME_LEN 7
#define PDF_STARTXREF_NAME "startxref"
#define PDF_STARTXREF_NAME_LEN 9
#define PDF_XREFSTM_NAME "/XRefStm"
#define PDF_XREFSTM_NAME_LEN 8
#define PDF_PRC_NAME "/PRC"
#define PDF_PRC_NAME_LEN 4
#define PDF_DECODE_PARMS_NAME "/DecodeParms"
#define PDF_DECODE_PARMS_NAME_LEN 12
#define PDF_COLUMNS_NAME "/Columns"
#define PDF_COLUMNS_NAME_LEN 8
#define PDF_PREDICTOR_NAME "/Predictor"
#define PDF_PREDICTOR_NAME_LEN 10
#define PDF_COLORS_NAME "/Colors"
#define PDF_COLORS_NAME_LEN 7
#define PDF_BITS_PER_COMPONENT_NAME "/BitsPerComponent"
#define PDF_BITS_PER_COMPONENT_NAME_LEN 17
#define PDF_COLUMNS_NAME "/Columns"
#define PDF_COLUMNS_NAME_LEN 8
#define PDF_EARLY_CHANGE_NAME "/EarlyChange"
#define PDF_EARLY_CHANGE_NAME_LEN 12
#define PDF_OBJECT_NAME "obj"
#define PDF_OBJECT_NAME_LEN 3
#define PDF_VA_NAME "/VA"
#define PDF_VA_NAME_LEN 3
#define PDF_3DVIEW_NAME "3DView"
#define PDF_3DVIEW_NAME_LEN 6
#define PDF_VA_NAME "/VA"
#define PDF_VA_NAME_LEN 3
#define PDF_C2W_NAME "/C2W"
#define PDF_C2W_NAME_LEN 4
#define PDF_CO_NAME "/CO"
#define PDF_CO_NAME_LEN 3
#define PDF_IN_NAME "/IN"
#define PDF_IN_NAME_LEN 3
#define PDF_XN_NAME "/XN"
#define PDF_XN_NAME_LEN 3
#define PDF_W_NAME "/W"
#define PDF_W_NAME_LEN 2
#define PDF_INDEX_NAME "/Index"
#define PDF_INDEX_NAME_LEN 6
#define PDF_N_NAME "/N"
#define PDF_N_NAME_LEN 2
#define PDF_FIRST_NAME "/First"
#define PDF_FIRST_NAME_LEN 6
#define PDF_EXTENDS_NAME "/Extends"
#define PDF_EXTENDS_NAME_LEN 8
#define PDF_FREE_OBJECT_NAME "f"
#define PDF_FREE_OBJECT_NAME_LEN 1
#define PDF_IN_USE_OBJECT_NAME "n"
#define PDF_IN_USE_OBJECT_NAME_LEN 1
#define PDF_SIZE_NAME "/Size"
#define PDF_SIZE_NAME_LEN 5
#define PDF_ENCRYPT_NAME "/Encrypt"
#define PDF_ENCRYPT_NAME_LEN 8
#define PDF_TYPE_ANNOT "/Annot"
#define PDF_TYPE_ANNOT_LEN 6
#define PDF_RICHMEDIA_NAME "/RichMedia"
#define PDF_RICHMEDIA_NAME_LEN 10
#define PDF_RICHMEDIASETTINGS_NAME "/RichMediaSettings"
#define PDF_RICHMEDIASETTINGS_NAME_LEN 18
#define PDF_RICHMEDIACONTENT_NAME "/RichMediaContent"
#define PDF_RICHMEDIACONTENT_NAME_LEN 17
#define PDF_ASSETS_NAME "/Assets"
#define PDF_ASSETS_NAME_LEN 7
#define PDF_NAMES_NAME "/Names"
#define PDF_NAMES_NAME_LEN 6
#define PDF_EF_NAME "/EF"
#define PDF_EF_NAME_LEN 4
#define PDF_F_NAME "/F"
#define PDF_F_NAME_LEN 2
#define PDF_VIEW_NAME "/View"
#define PDF_VIEW_NAME_LEN 5
#define PDF_ACTIVATION_NAME "/Activation"
#define PDF_ACTIVATION_NAME_LEN 11
#define PDF_PREV_NAME "/Prev"
#define PDF_PREV_NAME_LEN 5
#define PDF_PRC_STREAM_HEADER "PRC"
#define PDF_PRC_STREAM_HEADER_LEN 3
#define PDF_ID_NAME "/ID"
#define PDF_ID_NAME_LEN 3
#define PDF_ENCRYPT_META_DATA_NAME "/EncryptMetadata"
#define PDF_ENCRYPT_META_DATA_NAME_LEN 16

typedef struct prc_pdf_projection_s prc_pdf_projection;
typedef struct prc_pdf_3dview_s prc_pdf_3dview;
typedef struct prc_pdf_render_mode_s prc_pdf_render_mode;
typedef struct prc_pdf_lighting_scheme_s prc_pdf_lighting_scheme;
typedef struct prc_pdf_lighting_details_s prc_pdf_lighting_details;
typedef struct prc_pdf_xref_s prc_pdf_xref;
typedef struct prc_pdf_view_array_s prc_pdf_view_array;
typedef struct prc_pdf_decode_params_s prc_pdf_decode_params;
typedef struct prc_pdf_scanline_decoder_s prc_pdf_scanline_decoder;
typedef struct prc_pdf_span_s prc_pdf_span;
typedef struct prc_pdf_head_xref_s prc_pdf_head_xref;
typedef struct prc_pdf_uncompressed_object_stream_list_s prc_pdf_uncompressed_object_stream_list;
typedef struct prc_pdf_uncompressed_object_stream_s prc_pdf_uncompressed_object_stream;
typedef struct prc_pdf_object_stream_offsets_s prc_pdf_object_stream_offsets;
typedef struct prc_pdf_decrypt_params_s prc_pdf_decrypt_params;
typedef struct prc_pdf_crypt_handler_s prc_pdf_crypt_handler;
typedef struct CRYPT_sha2_context_s CRYPT_sha2_context;
typedef struct CRYPT_aes_context_s CRYPT_aes_context;
typedef struct CRYPT_aes_crypt_context_s CRYPT_aes_crypt_context;
typedef struct CRYPT_md5_context_s CRYPT_md5_context;
typedef struct CRYPT_rc4_context_s CRYPT_rc4_context;

int pdf_extract_prc(prc_context *ctx, uint8_t *pdf_buff_in, uint32_t size_in,
    uint8_t **buff_out, uint32_t *size_out, prc_pdf_view_array **views,
    uint32_t *number_views);

int pdf_tiff_predictor(prc_context *ctx, prc_pdf_decode_params decode_params,
    uint8_t *buff_in_xref, uint32_t size_in_xref, uint8_t **buff_out_xref_decode,
    uint32_t *size_out_xref_decode);

int pdf_png_predictor(prc_context *ctx, prc_pdf_decode_params decode_params,
    uint8_t *buff_in, uint32_t size_in, uint8_t **buff_out_decode,
    uint32_t *size_out_decode);

int prc_pdf_object_stream_decompress(prc_context *ctx, uint8_t *pdf_buff_in,
    uint32_t pdf_size_in, prc_pdf_head_xref *compressed_xref,
    uint32_t num_content_streams,
    prc_pdf_uncompressed_object_stream_list *uncompressed_object_stream_list,
    uint32_t xref_head_offset, uint8_t streams_encrypted,
    prc_pdf_decrypt_params *decryption_params);

int32_t pdf_search_for_tag(prc_context *ctx, uint8_t *ptr, uint8_t *boundary,
    uint8_t *tag_name, uint32_t tag_name_len, uint8_t *bound_tag_name,
    uint32_t bound_tag_name_len, uint8_t *found);

int pdf_get_stream_data(prc_context *ctx, uint8_t *ptr_in_obj, uint8_t *boundary,
    uint8_t *ptr_in_stream, uint32_t stream_length, uint8_t **buff_out,
    uint32_t *size_out);

int pdf_get_stream_info(prc_context *ctx, uint8_t *ptr_in, uint8_t *boundary,
    uint8_t **ptr_stream_start, uint32_t *stream_length, uint32_t *obj_num,
    uint32_t *gen_num);

int pdf_eat_white_space(prc_context *ctx, uint8_t **ptr, uint8_t *boundary);

int pdf_count_xref_sections(prc_context *ctx, uint8_t *pdf_buff_in,
    uint32_t xref_offset, uint32_t size_in, uint32_t *num_xrefs,
    uint32_t **xref_offsets);

int pdf_parse_xref(prc_context *ctx, uint8_t *pdf_buff_in,
    uint32_t size_in, prc_pdf_head_xref *xref_head,
    prc_pdf_uncompressed_object_stream_list *stream_list,
    uint32_t *xref_offsets, uint8_t *streams_are_encrypted,
    prc_pdf_decrypt_params *encryption_params);

int pdf_check_for_dict_int_entry(prc_context *ctx, uint8_t *ptr_in, uint8_t *boundary,
    uint8_t *tag_name, uint32_t tag_name_len, int *value_out);

int pdf_get_decode_params(prc_context *ctx, uint8_t *pdf_buff_in, uint8_t *file_end,
    prc_pdf_decode_params *decode_params);

uint8_t* prc_pdf_get_ptr_to_obj(prc_context *context, uint8_t *pdf_buff_in,
    uint32_t size_in, prc_pdf_head_xref *head_xref,
    prc_pdf_uncompressed_object_stream_list *stream_list,
    int32_t object_number, uint32_t *size_out,
    uint8_t *is_object_stream_item);

int pdf_apply_predictor(prc_context *ctx, uint8_t *buff_in, uint32_t size_in,
    uint8_t **buff_out_decode, uint32_t *size_out_decode,
    prc_pdf_decode_params decode_params);

int pdf_get_integer_array_prc(prc_context *ctx, uint8_t *ptr_in, uint8_t *file_end,
    uint8_t *tag_name, uint32_t tag_length, uint8_t *tag_end_name,
    uint32_t tag_end_length, int32_t *elements, uint32_t *num_elements_in,
    uint32_t max_elements);

int pdf_get_integer_prc(prc_context *ctx, uint8_t *ptr_in, uint8_t *file_end,
    uint8_t *tag_name, uint32_t tag_length, uint8_t *tag_end_name,
    uint32_t tag_end_length, uint32_t *value);

int prc_pdf_dict_get_uinteger(prc_context *ctx, uint8_t *input_buffer,
    uint8_t *buffer_end, const char *key, uint32_t *value, uint32_t default_value);

int prc_pdf_dict_get_boolean(prc_context *ctx, uint8_t *input_buffer,
    uint8_t *buffer_end, const char *key, uint8_t *value, uint8_t default_value);

int prc_pdf_dict_get_integer(prc_context *ctx, uint8_t *input_buffer,
    uint8_t *buffer_end, const char *key, int32_t *value, int32_t default_value);

int prc_pdf_dict_get_bytestring(prc_context *ctx, uint8_t *input_buffer,
    uint8_t *buffer_end, const char *key, char *string, uint32_t max_str_len);

int prc_pdf_dict_get_dict(prc_context *ctx, uint8_t *input_buffer,
    uint8_t *buffer_end, const char *key, uint8_t **dict_start, uint8_t **dict_end);

int prc_pdf_dict_get_ref(prc_context *ctx, uint8_t *input_buffer, uint8_t *buffer_end,
    const char *key, uint32_t *object_num, uint32_t *gen_num);

int prc_pdf_dict_get_hexstring(prc_context *ctx, uint8_t *input_buffer,
    uint8_t *buffer_end, const char *key, uint8_t *hexstring, uint32_t max_str_len,
    uint32_t *actual_str_len);

int prc_pdf_dict_get_type(prc_context *ctx, uint8_t *input_buffer, uint8_t *buffer_end,
    const char *key, uint8_t *keyout, uint32_t max_keyout_len, uint32_t *actual_keyout_len);

int prc_pdf_dict_get_literal_string(prc_context *ctx, uint8_t *input_buffer, uint8_t *buffer_end,
    const char *key, uint8_t *string, uint32_t max_str_len, uint32_t *actual_str_len);

int pdf_parse_decryption(prc_context *ctx, prc_pdf_head_xref *head_xref,
    uint8_t *pdf_buff_in, uint8_t *pdf_buff_end, uint32_t object_num,
    prc_pdf_decrypt_params *decrypt_params);

int pdf_get_decrypted_stream_data(prc_context *ctx, uint8_t *ptr_in_stream,
    uint32_t stream_length, prc_pdf_decrypt_params *decrypt_params,
    uint8_t *decrypted_data, size_t decypted_size, uint32_t obj_num,
    uint32_t gen_num, uint32_t *actual_decrypted_size);

size_t pdf_decrypt_get_size(prc_context *ctx, prc_pdf_decrypt_params *decrypt_params,
    size_t src_size);

int pdf_decrypt_string(prc_context *ctx, uint8_t *ptr_in_stream,
    uint32_t stream_length, prc_pdf_decrypt_params *decrypt_params,
    uint32_t obj_num, uint32_t gen_num, uint8_t **decrypted_data,
    uint32_t *decypted_size);

int pdf_get_file_id(prc_context *ctx, uint8_t *pdf_buff_in, uint32_t size_in,
    uint8_t *file_id, uint32_t *file_id_length);

int pdf_get_file_id_from_object(prc_context *ctx, uint8_t *ptr_in, uint8_t *file_end,
    uint8_t *file_id, uint32_t *file_id_length);

typedef enum {
    PRC_PDF_PROJ_ORTHO = 0,
    PRC_PDF_PROJ_PERSEPCTIVE = 1
} prc_pdf_projection_t;

typedef enum {
    PRC_PDF_PROJ_CLIP_ANF = 0, /* Automatic near far (default) */
    PRC_PDF_PROJ_CLIP_XNF = 1  /* Explicit near far */
} prc_pdf_projection_clip_t;

typedef enum {
    PRC_PDF_PROJ_SCALE_W = 0, /* Default */
    PRC_PDF_PROJ_SCALE_H = 1,
    PRC_PDF_PROJ_SCALE_MIN = 2,
    PRC_PDF_PROJ_SCALE_MAX = 3
} prc_pdf_projection_scale_name_t;

enum {
    PRC_PDF_XREF_FREE_TYPE = 0, /* Free object in the xref table */
    PRC_PDF_XREF_USED_TYPE = 1, /* Used object in the xref table */
    PRC_PDF_XREF_COMPRESSED_TYPE = 2, /* Compressed object in the xref table */
};

typedef enum {
    PRC_PDF_RM_SOLID = 0,
    PRC_PDF_RM_SOLID_WIREFRAME = 1,
    PRC_PDF_RM_TRANSPARENT = 2,
    PRC_PDF_RM_TRANSPARENT_WIREFRAME = 3,
    PRC_PDF_RM_BOUNDING_BOX = 4,
    PRC_PDF_RM_TRANSPARENT_BOUNDING_BOX = 5,
    PRC_PRD_RM_TRANSPARENT_BOUNDING_BOX_OUTLINE = 6,
    PRC_PDF_RM_WIREFRAME = 7,
    PRC_PDF_RM_SHADED_WIREFRAME = 8,
    PRC_PDF_RM_HIDDEN_WIREFRAME = 9,
    PRC_PDF_RM_VERTICES = 10,
    PRC_PDF_RM_SHADED_VERTICES = 11,
    PRC_PDF_RM_ILLUSTRATION = 12,
    PRC_PDF_RM_SOLID_OUTLINE = 13,
    PRC_PDF_RM_SHADED_ILLUSTRATION = 14
} prc_pdf_render_modes_t;

typedef enum
{
    PRC_PDF_LIGHT_ARTWORK = 0,
    PRC_PDF_LIGHT_NONE = 1,
    PRC_PDF_LIGHT_WHITE = 2,
    PRC_PDF_LIGHT_DAY = 3,
    PRC_PDF_LIGHT_NIGHT = 4,
    PRC_PDF_LIGHT_HARD = 5,
    PRC_PDF_LIGHT_PRIMARY = 6,
    PRC_PDF_LIGHT_BLUE = 7,
    PRC_PDF_LIGHT_RED = 8,
    PRC_PDF_LIGHT_CUBE = 9,
    PRC_PDF_LIGHT_CAD = 10,
    PRC_PDF_LIGHT_HEADLAMP = 11
} prc_pdf_lighting_scheme_t;

typedef enum
{
    PRC_PDF_CIPHER_NONE = 0,
    PRC_PDF_CIPHER_RC4 = 1,
    PRC_PDF_CIPHER_AES = 2,
    PRC_PDF_CIPHER_AES2 = 3
} prc_pdf_encryption_cipher_t;


typedef enum
{
    PRC_PDF_PW_ENCODE_UNKOWN,
    PRC_PDF_PW_ENCODE_NONE,
    PRC_PDF_PW_ENCODE_Latin1ToUtf8,
    PRC_PDF_PW_ENCODE_UNKOWN_Utf8toLatin1
} prc_pdf_password_encoding_t;

struct prc_pdf_decode_params_s
{
    int predictor;
    int colors;
    int bits_per_component;
    int columns;
    int early_change;
};
struct prc_pdf_view_array_s
{
    double matrix[12];
    double center_orbit_z;
    char *internal_name;
    char *external_name;
};

struct prc_pdf_lighting_details_s
{
    uint8_t has_ambient;
    double ambient_color[3];
    uint8_t num_sources;
    double *source_color;
    double *source_direction;
};

struct prc_pdf_lighting_scheme_s
{
    prc_pdf_lighting_scheme_t subtype;
    prc_pdf_lighting_details *details;
};

struct prc_pdf_span_s
{
    uint8_t *data;
    uint32_t size; /* Size of the data in bytes */
};

struct prc_pdf_scanline_decoder_s
{
    int orig_width;
    int orig_height;
    int output_width;
    int output_height;
    int comps;
    int bpc;
    uint32_t pitch;
    int next_line;
    uint8_t *last_scanline;
    uint32_t last_scanline_size;

    int predictor_type;
    int colors;
    int bits_per_component;
    int columns;
    uint32_t predict_pitch;
    size_t left_over;
    uint8_t *last_line;
    uint32_t last_line_size;
    uint8_t *predict_buffer;
    uint32_t predict_buffer_size;
    uint8_t *predict_raw;
    uint32_t predict_raw_size;
};

struct prc_pdf_projection_s
{
    prc_pdf_projection_t projection_type;
    prc_pdf_projection_clip_t clip_type;
    double far_clip;  /* Only valid if clip_type is XNF */
    double near_clip; /* Only valid if clip_type is XNF */
    double fov; /* Only valid if projection_type is PRC_PDF_PROJ_PERSEPCTIVE (between 0 and 180) */
    double projection_scaling; /* Only valid if projection_type is PRC_PDF_PROJ_PERSEPCTIVE */
    prc_pdf_projection_scale_name_t projection_scaling_name; /* Only valid if projection_type is PRC_PDF_PROJ_PERSEPCTIVE */
    double othro_scale; /* Only valid if projection_type is PRC_PDF_PROJ_ORTHO (applies to x and y) */
    prc_pdf_projection_scale_name_t ortho_scale_bind; /* Only valid if projection_type is PRC_PDF_PROJ_ORTHO */
};

struct prc_pdf_render_mode_s
{
    prc_pdf_render_modes_t subtype;
    double auxillary_color[3];
    double face_color[3]; /* Ugh we need to BG color from the PDF file */
    double opacity;
    double crease_value;
};

struct prc_pdf_3dview_s
{
    char *external_name;
    char *internal_name;
    double camera_to_world_matrix[16];
    double background_color[3];
    double camera_along_z;
    prc_pdf_projection projection;
    prc_pdf_render_mode render_mode;
    prc_pdf_lighting_scheme lighting;
    prc_pdf_3dview *next;
};

struct prc_pdf_xref_s
{
    uint32_t object_number;
    uint32_t generation_number;
    uint32_t byte_offset;
    uint8_t is_compressed;
    uint8_t is_free; /* If true, this object is free and not used in the PDF file */
    uint32_t object_stream_index; /* If this is a compressed object, this is the index into the object stream */
    uint8_t type;
};

#define CRYPT_AES_MAX_NB 8
#define CRYPT_AES_MAX_NR 14
#define CRYPT_AES_SCHED_SIZE (CRYPT_AES_MAX_NR + 1) * CRYPT_AES_MAX_NB

struct CRYPT_aes_context_s
{
    int Nb;
    int Nr;
    uint32_t keysched[CRYPT_AES_SCHED_SIZE];
    uint32_t invkeysched[CRYPT_AES_SCHED_SIZE];
    uint32_t iv[CRYPT_AES_MAX_NB];
};

struct CRYPT_aes_crypt_context_s
{
    uint8_t m_bIV;
    uint32_t m_block_offset;
    CRYPT_aes_context *aes_ctx;
    uint8_t m_block[16];
};

struct CRYPT_md5_context_s
{
    uint32_t total[2];
    uint32_t state[4];
    uint8_t buffer[64];
};

struct CRYPT_rc4_context_s
{
    int32_t x;
    int32_t y;
    int32_t m[256];
};

struct prc_pdf_crypt_handler_s
{
    size_t key_length;
    prc_pdf_encryption_cipher_t cipher;
    CRYPT_aes_context aes_ctx;
    uint8_t encryption_key[32];
};

struct prc_pdf_decrypt_params_s
{
    uint32_t version;
    uint32_t revision;
    uint32_t permissions;
    size_t key_length;
    int num_key_bits;
    prc_pdf_encryption_cipher_t cipher;
    prc_pdf_password_encoding_t password_encoding;
    uint8_t encryption_key[32];
    uint8_t *crypt_filter_dict_ptr;
    uint8_t *crypt_filter_dict_end;
    uint8_t *def_filter_dict_ptr;
    uint8_t *def_filter_dict_end;
    char *password;
    uint32_t password_length;
    prc_pdf_crypt_handler crypt_handler;
    uint8_t file_id[PDF_MAX_DICT_VALUE];
    uint32_t file_id_length;
};

struct prc_pdf_head_xref_s
{
    uint8_t byte_field[3];
    uint32_t num_objects; /* Number of objects in the xref table */
    prc_pdf_xref *xref_objects; /* Array of xref objects */
};

struct prc_pdf_object_stream_offsets_s
{
    uint32_t object_number; /* Object number */
    uint32_t offset; /* Object offset in the stream */
};

struct prc_pdf_uncompressed_object_stream_s
{
    uint8_t *stream;
    uint32_t stream_length;
    uint32_t stream_object_number;
    uint32_t n; /* Number of objects in the stream */
    uint32_t first; /* byte offset of the first compressed object */
    prc_pdf_object_stream_offsets *object_offsets; /* Array of object offsets in the stream */
};

struct prc_pdf_uncompressed_object_stream_list_s
{
    uint32_t number_streams;
    prc_pdf_uncompressed_object_stream *ustream;
};

/* Stream Predictor Info */
typedef enum
{
    PRC_PDF_PREDICTOR_NONE = 1,
    PRC_PDF_PREDICTOR_TIFF = 2,
    PRC_PDF_PREDICTOR_PNG_NONE = 10,
    PRC_PDF_PREDICTOR_PNG_SUB = 11,
    PRC_PDF_PREDICTOR_PNG_UP = 12,
    PRC_PDF_PREDICTOR_PNG_AVG = 13,
    PRC_PDF_PREDICTOR_PNG_PAETH = 14,
    PRC_PDF_PREDICTOR_PNG_OPTIM = 15
} prc_pdf_stream_predictor_t;

/* Encryption related material */
struct CRYPT_sha2_context_s {
    uint64_t total_bytes;
    uint64_t state[8];
    uint8_t buffer[128];
};

#endif
