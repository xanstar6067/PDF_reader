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

#ifndef PRC_DOUBLE_H
#define PRC_DOUBLE_H

/* From ISO_14739-1_2014 */
union ieee754_double
{
    double d;
    /* This is the IEEE 754 double-precision format. */
    struct
    {
#ifdef PRC_BIG_ENDIAN
        unsigned int negative : 1;
        unsigned int exponent : 11;
        /* Together these comprise the mantissa.  */
        unsigned int mantissa0 : 20;
        unsigned int mantissa1 : 32;
#elif defined(PRC_LITTLE_ENDIAN)
        /* Together these comprise the mantissa.  */
        unsigned int mantissa1 : 32;
        unsigned int mantissa0 : 20;
        unsigned int exponent : 11;
        unsigned int negative : 1;
#else
#error "Big/Little endian to be defined"
#endif
    } ieee;
};

union ieee754_float
{
    float f;
    /* This is the IEEE 754 float-precision format. */
    struct {
#ifdef PRC_BIG_ENDIAN
        unsigned int negative : 1;
        unsigned int exponent : 8;
        unsigned int mantissa : 23;
#elif defined(PRC_LITTLE_ENDIAN)
        unsigned int mantissa : 23;
        unsigned int exponent : 8;
        unsigned int negative : 1;
#else
#error "Big/Little endian to be defined"
#endif
    } ieee;
};

#define NUMBEROFELEMENTINACOFDOE (2077)
enum ValueType
{
    VT_double,
    VT_exponent
};

/* Coding of a frequent double or exponent value. */
typedef struct sCodageOfFrequentDoubleOrExponent_s sCodageOfFrequentDoubleOrExponent;
struct sCodageOfFrequentDoubleOrExponent_s
{
    /* Value type (VT_double or VT_exponent) */
    short Type;
    /* Number of bits. */
    short NumberOfBits;
    /* Bit values. */
    unsigned Bits;
    /* Unsigned or double value. */
    union
    {
        unsigned ul[2]; /* Two unsigned values. */
        double Value; /* Double value. */
    } u2uod;
};

#ifdef PRC_BIG_ENDIAN
# define DOUBLEWITHTWODWORDINTREE(upper,lower) {upper,lower}
#elif defined(PRC_LITTLE_ENDIAN)
# define DOUBLEWITHTWODWORDINTREE(upper,lower) {lower,upper}
#endif

// Macros for LITTLE ENDIAN machines
#ifdef PRC_LITTLE_ENDIAN
# define DOUBLEWITHTWODWORD(upper,lower) lower,upper
# define UPPERPOWER (1)
# define LOWERPOWER (!UPPERPOWER)
# define NEXTBYTE(pbd) ((pbd)--)
# define PREVIOUSBYTE(pbd) ((pbd)++)
# define MOREBYTE(pbd,pbend) ((pbd)>=(pbend))
# define OFFSETBYTE(pbd,offset) ((pbd)-=offset)
# define BEFOREBYTE(pbd) ((pbd)+1)
# define DIFFPOINTERS(p1,p2) ((unsigned)((p2)-(p1)))
# define SEARCHBYTE(pbstart,b,nb) (unsigned char *)memchr((pbstart),(b),(nb))
# define BYTEAT(pb,i) *((pb)+(i))
// Macros for BIG ENDIAN machines
#elif defined(PRC_BIG_ENDIAN)
# define DOUBLEWITHTWODWORD(upper,lower) upper,lower
# define UPPERPOWER (0)
# define LOWERPOWER (!UPPERPOWER)
# define NEXTBYTE(pbd) ((pbd)++)
# define PREVIOUSBYTE(pbd) ((pbd)--)
# define MOREBYTE(pbd,pbend) ((pbd)<=(pbend))
# define OFFSETBYTE(pbd,offset) ((pbd)+=offset)
# define BEFOREBYTE(pbd) ((pbd)-1)
# define DIFFPOINTERS(p1,p2) ((p1)-(p2))
# define SEARCHBYTE(pbstart,b,nb) (unsigned char *)prc_memrchr((pbstart),(b),(nb))
# define BYTEAT(pb,i) *((pb)-(i))
#else
# error "Big/Little endian to be defined"
#endif
// Common macros and types
#define MAXLENGTHFORCOMPRESSEDTYPE ((22+1+1+4+6*(1+8))+7)/8
#define NEGATIVE(d) (((union ieee754_double *)&(d))->ieee.negative)
#define EXPONENT(d) (((union ieee754_double *)&(d))->ieee.exponent)
#define MANTISSA0(d) (((union ieee754_double *)&(d))->ieee.mantissa0)
#define MANTISSA1(d) (((union ieee754_double *)&(d))->ieee.mantissa1)
typedef unsigned char PRCbyte;
typedef unsigned short PRCword;
typedef unsigned PRCdword;

static PRCdword
stadwZero[2] = { DOUBLEWITHTWODWORD(0x00000000,0x00000000) },
stadwNegativeZero[2] = { DOUBLEWITHTWODWORD(0x80000000,0x00000000) };

extern const sCodageOfFrequentDoubleOrExponent prc_acofdoe[NUMBEROFELEMENTINACOFDOE];

#define STAT_V
#define STAT_DOUBLE

int stCOFDOECompare(const void*, const void*);

#ifdef PRC_BIG_ENDIAN
void* prc_memrchr(const void*, int, size_t);
#endif

/* Look up the value */
const sCodageOfFrequentDoubleOrExponent* get_acofdoe_value(prc_context* ctx, unsigned, short);

#endif
