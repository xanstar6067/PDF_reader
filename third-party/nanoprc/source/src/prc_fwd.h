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

#ifndef PRC_FWD_H
#define PRC_FWD_H

/* Forward declarations to break circular includes */
typedef struct prc_base_with_graphics_s prc_base_with_graphics;
typedef struct prc_misc_entity_reference_s prc_misc_entity_reference;

/* Marks a declaration as deprecated, producing a compiler warning (not an
   error) at each use site. Two macros are needed, not one, because GCC/Clang
   and MSVC disagree on where the annotation must go on a typedef:
     GCC/Clang : typedef OldType NewName __attribute__((deprecated(msg)));  (postfix)
     MSVC      : typedef __declspec(deprecated(msg)) OldType NewName;      (prefix)
   Putting __declspec in the postfix slot is a hard MSVC syntax error
   (confirmed: C2054), so each macro is a no-op on the compiler that doesn't
   use that slot; use BOTH at every deprecated-typedef site (see prc_api.h)
   and exactly one of the two will expand to anything for a given compiler.
   Other compilers get no-ops for both, so an unrecognized toolchain still
   compiles (just without the warning). */
#if defined(__GNUC__) || defined(__clang__)
#define PRC_DEPRECATED_PREFIX(msg)
#define PRC_DEPRECATED(msg) __attribute__((deprecated(msg)))
#elif defined(_MSC_VER)
#define PRC_DEPRECATED_PREFIX(msg) __declspec(deprecated(msg))
#define PRC_DEPRECATED(msg)
#else
#define PRC_DEPRECATED_PREFIX(msg)
#define PRC_DEPRECATED(msg)
#endif

/* C99 "inline" is used bare (paired with an explicit leading "static" at each
   call site, e.g. "static PRC_INLINE T *f(...)") rather than baked into this
   macro as "static inline", since MSVC 2015+ and GCC/Clang both accept plain
   C99 "inline" when compiling as C, and folding "static" in here would
   double it up at every use site. */
#define PRC_INLINE inline

#endif
