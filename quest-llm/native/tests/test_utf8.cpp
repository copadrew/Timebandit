// test_utf8.cpp — exercises the streaming UTF-8 boundary guard.
//
// This is the one piece of pure logic in the native layer that can silently
// corrupt output, and it is annoying to debug through a headset, so it gets a
// test that runs on the host with no NDK and no llama.cpp:
//
//   g++ -std=c++17 -Wall -Wextra -I../include -o test_utf8 test_utf8.cpp && ./test_utf8

#include "lu_utf8.h"

#include <cstdio>
#include <string>

static int g_failures = 0;

static void check(const char * name, const std::string & in, size_t expect) {
    const size_t got = lu_utf8_truncate(in.data(), in.size());
    const bool ok = (got == expect);
    if (!ok) ++g_failures;
    std::printf("%-40s len=%3zu -> %3zu (want %3zu) %s\n",
                name, in.size(), got, expect, ok ? "ok" : "FAIL");
}

int main() {
    check("empty",                    "",                 0);
    check("pure ascii",               "hello",            5);

    // U+00E9 é = C3 A9
    check("complete 2-byte",          "a\xC3\xA9",        3);
    check("split 2-byte",             "a\xC3",            1);

    // U+20AC € = E2 82 AC
    check("complete 3-byte",          "\xE2\x82\xAC",     3);
    check("split 3-byte after lead",  "x\xE2",            1);
    check("split 3-byte after two",   "x\xE2\x82",        1);

    // U+1F600 😀 = F0 9F 98 80
    check("complete 4-byte",          "\xF0\x9F\x98\x80", 4);
    check("split 4-byte after lead",  "\xF0",             0);
    check("split 4-byte after three", "\xF0\x9F\x98",     0);
    check("ascii then split emoji",   "hi\xF0\x9F",       2);

    // Malformed input is flushed rather than held. Holding it would stall the
    // stream forever waiting for a lead byte that is never coming; see the
    // comment at the end of lu_utf8_truncate.
    check("stray continuation byte",  "\x80",             1);
    check("four continuation bytes",  "\x80\x80\x80\x80", 4);

    // The realistic case: a long run of text ending in a truncated character.
    std::string longish(200, 'a');
    longish += "\xE2\x82";
    check("200 ascii + split 3-byte",  longish,          200);

    std::printf("\n%s\n", g_failures ? "FAILED" : "all tests passed");
    return g_failures != 0;
}
