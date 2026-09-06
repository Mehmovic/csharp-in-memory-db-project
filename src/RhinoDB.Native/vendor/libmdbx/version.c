/* Pre-generated from version.c.in for the vendored amalgamated build - see
 * VERSION.json/VERSION.txt for the exact upstream tag/commit this reflects.
 * Do not hand-edit; re-generate when re-vendoring from a newer upstream tag.
 ******************************************************************************/

#include "internals.h"

#if MDBX_VERSION_MAJOR != 0 || MDBX_VERSION_MINOR != 13
#error "API version mismatch! Had `git fetch --tags` done?"
#endif

static const char sourcery[] = "vendored_v0_13_9";

__dll_export
#ifdef __attribute_used__
    __attribute_used__
#elif defined(__GNUC__) || __has_attribute(__used__)
    __attribute__((__used__))
#endif
#ifdef __attribute_externally_visible__
        __attribute_externally_visible__
#elif (defined(__GNUC__) && !defined(__clang__)) || __has_attribute(__externally_visible__)
    __attribute__((__externally_visible__))
#endif
    const struct MDBX_version_info mdbx_version = {
        0,
        13,
        9,
        0,
        "", /* pre-release suffix of SemVer */
        {"2025-10-31T09:33:51Z", "dca663bdf7f9f8830ac0d46059c44089d56cf521", "926e90ac9a13eb761afc85d37641e4acfa8ea998",
         "v0.13.9-0-g926e90ac"},
        sourcery};

__dll_export
#ifdef __attribute_used__
    __attribute_used__
#elif defined(__GNUC__) || __has_attribute(__used__)
    __attribute__((__used__))
#endif
#ifdef __attribute_externally_visible__
        __attribute_externally_visible__
#elif (defined(__GNUC__) && !defined(__clang__)) || __has_attribute(__externally_visible__)
    __attribute__((__externally_visible__))
#endif
    const char *const mdbx_sourcery_anchor = sourcery;
