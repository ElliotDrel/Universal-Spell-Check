# Confirmed live campaign results

The frozen Release correction Worker benchmark passed the absolute goal in two independent complete live blocks. Model `gpt-4.1` and prompt remained unchanged. Both blocks verified all 99 destination insertions with exact text/format, nine repetitions per case, using 100 actual native HTTP sends each (including startup).

| Case | Loop 2 median ms | Confirmation median ms | Verified per block |
|---|---:|---:|---:|
| short | 857.07 | 825.94 | 9/9 |
| paragraph | 903.71 | 1029.43 | 9/9 |
| long | 1927.26 | 1750.42 | 9/9 |
| unchanged | 872.67 | 730.63 | 9/9 |
| brands | 815.60 | 801.42 | 9/9 |
| literals | 935.41 | 816.55 | 9/9 |
| unicode | 792.98 | 750.86 | 9/9 |
| multiline | 833.61 | 848.06 | 9/9 |
| rich-link | 908.24 | 975.21 | 9/9 |
| rich-list | 751.78 | 751.11 | 9/9 |
| structured | 920.90 | 1022.82 | 9/9 |
| **Equal-weight mean** | **956.29** | **936.59** | **99/99** |

Native HTTP accounting: rejected baseline 55 + rejected loop 1 33 + loop 2 100 + independent confirmation 100 = **288/2100**. Two optimization loops completed; one separate confirmation. Correction attempts across campaign: **282/284 verified**, with both failed samples retained. Accepted candidate and confirmation: **198/198 verified**. No calls were used to replenish failed blocks.

The baseline stopped at 54/99 corrections when the native foreground guard blocked publication; no score. Loop 1 stopped at 32/99 when the API added list-line spaces; no score. Loop 2 restores source line-end ASCII spacing through actual product processing. Original captured evidence is unchanged; a separate explicit correctness oracle fixes the historical expected-space defect. No measured before/after speedup is claimed against incomplete references.

The same immutable executable SHA-256, frozen binding and schedule were independently verified across the two complete blocks. QPC spans accepted coordinator trigger through destination verification, including live API, native pipeline, two-animation-frame observation, snapshot delivery and coordinator polling. It excludes physical hardware hotkey dispatch, tray/dashboard and loading overlay. Disposable full-document rich editor deletion precedes native default paste; this is not certification of arbitrary Chrome contenteditable insertion behavior.

Clipboard restored byte-for-byte, protected production state/startup unchanged, and final normal-context inventory found zero owned benchmark processes. No merge/release or old PR closure.

Known limits: bare single-block spans retain unsupported_editor; historical rare native paste no-ops remain unresolved. One offline stray-character negative attempt was rejected and retained, not explained away. These limits are not claimed fixed by the accepted 198 synthetic insertions.

Evidence: [loop 2](iteration-02.json), [independent confirmation](confirmation-live-01.json), [rejected baseline](baseline-live-01.json), [rejected loop 1](iteration-01.json), [fixture proof](fixture-after-grant.json), [measurement revision](measurement-revision.json), [source/build checks](../checks.json), [stopped state](../state.json), [resume boundary](../resume.json). Complete generated directories, executable/source snapshots, request ledgers and failed/private artifacts remain in prepared ignored storage; do not delete.

A final cfg(test)-only schema guard rebuild changes 24 linker/debug identity bytes. [PE equivalence](final-build-equivalence.json) verifies all other bytes and executable/data sections identical to the measured immutable snapshot. Future manifest collector naming was also corrected; actual per-attempt browser identities and verification gates were already correct. No post-goal API calls were made.
