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

#include <stdio.h>
#include <stdarg.h>
#include <string.h>
#include "../include/prc_context.h"

prc_exception*
prc_error_top(prc_context *ctx)
{
    return ctx->exception;
}

void
prc_print_error_stack(prc_context *ctx)
{
    prc_exception *curr = prc_error_top(ctx);
    while (curr != NULL)
    {
        printf("%s", curr->message);
        if (curr->repeat_count > 0)
        {
            printf("  (...and %u more identical error%s squelched)\n",
                   curr->repeat_count, curr->repeat_count == 1 ? "" : "s");
        }
        curr = curr->prev;
    }
}

void
prc_vferror(prc_context *ctx, int code, const char *file, int line, const char *format, va_list args)
{
    char message[256];
    int len;
    prc_exception *new_except;

    snprintf(message, sizeof(message), "%s: Line %d: ", file, line);
    len = (int) strlen(message);
    if (len < (int)sizeof(message))
    {
        vsnprintf(&message[len], sizeof(message) - len, format, args); /* does null termination */
    }

    /* Squelch runs of identical errors -- e.g. a corrupted count field
       driving a loop into millions of iterations that all hit the same
       default/unknown-type case -- so a repeating error can't grow the
       exception stack or the eventual printed report without bound. Count
       the repeat on the existing top-of-stack node instead of allocating
       and printing a new one for every occurrence. */
    if (ctx->exception != NULL && ctx->exception->code == code &&
        strncmp(ctx->exception->message, message, sizeof(message)) == 0)
    {
        ctx->exception->repeat_count++;
        return;
    }

    new_except = prc_calloc(ctx, 1, sizeof(prc_exception));
    if (new_except == NULL)
    {
        /* Things have really run off the rails! The error being reported is
           already lost to OOM; don't also crash the host process. */
        prc_print_error_stack(ctx);
        printf("Failure to allocate exeception, catastrophic failure!\n");
        return;
    }

    new_except->code = code;
    strncpy(new_except->message, message, sizeof(new_except->message));
    new_except->message[sizeof(new_except->message) - 1] = '\0';

    if (ctx->exception == NULL)
        ctx->exception = new_except;
    else
    {
        ctx->exception->next = new_except;
        new_except->prev = ctx->exception;
        ctx->exception = new_except;
    }
}

void
prc_ferror(prc_context *ctx, int code, const char *file, int line, const char *format, ...)
{
    va_list ap;
    va_start(ap, format);
    prc_vferror(ctx, code, file, line, format, ap);
    va_end(ap);
}

void
prc_error_pop(prc_context *ctx)
{
    if (ctx->exception != NULL)
    {
        prc_exception *prev = ctx->exception->prev;
        prc_free(ctx, ctx->exception);
        ctx->exception = prev;
    }
}
