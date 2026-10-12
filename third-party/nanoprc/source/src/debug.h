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

#ifndef DEBUG_H
#define DEBUG_H

#define DEBUG 1
#undef DEBUG /* Comment this line to enable debug logging */
#ifdef DEBUG
#define DEBUG_LOG(...) printf(__VA_ARGS__);
#else
#define DEBUG_LOG(...)
#endif

#define DEBUG2 1
#undef DEBUG2 /* Comment this line to enable debug logging */
#ifdef DEBUG2
#define DEBUG_LOG2(...) printf(__VA_ARGS__);
#else
#define DEBUG_LOG2(...)
#endif

#define PRC_WARN(...) printf(__VA_ARGS__);

#endif /* DEBUG_H */
