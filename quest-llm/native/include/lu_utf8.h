/*
 * lu_utf8.h — UTF-8 boundary guard for the streaming output queue.
 *
 * Header-only so tests/test_utf8.cpp can exercise it without linking llama.cpp.
 */
#ifndef LU_UTF8_H
#define LU_UTF8_H

#include <stddef.h>

/*
 * Returns the length of the longest prefix of s[0..n) that ends on a UTF-8
 * character boundary.
 *
 * Why this is needed: llama_token_to_piece can emit a fragment of a multi-byte
 * codepoint, because byte-fallback vocabularies split one character across
 * several tokens. The managed side decodes each polled chunk independently, so
 * handing it half a sequence produces a replacement character in the middle of
 * otherwise fine text.
 */
static inline size_t lu_utf8_truncate(const char * s, size_t n) {
    if (n == 0) return 0;

    /* Scan back at most 4 bytes looking for the lead byte of the last sequence. */
    for (size_t back = 0; back < 4 && back < n; ++back) {
        const size_t i = n - 1 - back;
        const unsigned char c = (unsigned char) s[i];
        if ((c & 0xC0) == 0x80) continue;          /* continuation, keep walking */

        size_t seq;
        if      ((c & 0x80) == 0x00) seq = 1;
        else if ((c & 0xE0) == 0xC0) seq = 2;
        else if ((c & 0xF0) == 0xE0) seq = 3;
        else if ((c & 0xF8) == 0xF0) seq = 4;
        else return i;                              /* invalid lead, drop it */

        return (i + seq <= n) ? n : i;              /* complete -> all, else cut */
    }

    /*
     * No lead byte in the final four bytes. This cannot happen for a valid but
     * merely incomplete tail: the longest UTF-8 sequence is four bytes, so a
     * genuine partial character always has its lead within the last three
     * positions and is handled above. Reaching here means the data is actually
     * malformed, and the right response is to flush it rather than hold it —
     * returning 0 would stall the stream permanently waiting for a lead byte
     * that is never coming. The managed decoder turns the stray bytes into
     * replacement characters, which is a visible glitch instead of a hang.
     */
    return n;
}

#endif /* LU_UTF8_H */
