# RnRomManager

A self-hosted web app for managing a personal backup collection of Game Boy and Game Boy Color ROMs.

Implementation is in progress. Ingestion records unmatched ROMs and ambiguous game groups in a persistent review queue, with up to five suggestions for distinct DAT base titles. Admins can now resolve or dismiss reviews through the API, including DAT search, manual identification and attachment to an existing game. See the [ingestion notes](https://github.com/rniemand/RnRomManager/blob/main/docs/features.md) for details. The review UI is an upcoming step.

For a reproducible local collection, use the [synthetic Basic/Full seed sets](https://github.com/rniemand/RnRomManager/blob/main/docs/development.md). They include matching fixture DATs and require no real ROMs or network downloads.
