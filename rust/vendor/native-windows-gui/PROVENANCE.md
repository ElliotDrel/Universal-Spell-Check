# Local NWG patch

Upstream: https://github.com/gabdube/native-windows-gui

Registry version: 1.0.13. Original crate SHA-256: `4f7003a669f68deb6b7c57d74fff4f8e533c44a3f0b297492440ef4ff5a28454`. Sources were copied from that local Cargo registry package, not from upstream HEAD.

Only source modification: `src/win32/high_dpi.rs::dpi` releases the screen DC acquired by `GetDC`, and returns 96 if the query fails. Repeated native dashboard resizing grew process GDI resources.

The crate archive omitted LICENSE. Its declared license is MIT; LICENSE was fetched from the upstream repository license endpoint, with its source in LICENSE-SOURCE.txt. The crate's recorded VCS commit `2e983ce20e8b395e1ad920ed02d458a61e3a75fe` could not be resolved upstream (HTTP 404), so that license fetch is not asserted to be version-pinned.

Keep this patch until an upstream release contains the fix, then remove the local override. Do not edit other vendored code as part of app work.
