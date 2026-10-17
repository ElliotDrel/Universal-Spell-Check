# Rust candidate assets

- Embed the existing channel icons from `src/Assets/`.
- Bundle Instrument Serif, Instrument Sans and JetBrains Mono in this directory, with each font's OFL license.
- Fetch fonts from the upstream [Google Fonts repository](https://github.com/google/fonts): [Instrument Serif](https://github.com/google/fonts/tree/main/ofl/instrumentserif), [Instrument Sans](https://github.com/google/fonts/tree/main/ofl/instrumentsans), and [JetBrains Mono](https://github.com/google/fonts/tree/main/ofl/jetbrainsmono).
- Load fonts from embedded memory; make no font download at app startup.
