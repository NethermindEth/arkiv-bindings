# Arkiv bindings

[![Test](https://github.com/nethermindeth/arkiv-bindings/actions/workflows/test-publish.yml/badge.svg)](https://github.com/nethermindeth/arkiv-bindings/actions/workflows/test-publish.yml)
[![Nethermind.Arkiv.Bindings](https://img.shields.io/nuget/v/Nethermind.Arkiv.Bindings)](https://www.nuget.org/packages/Nethermind.Arkiv.Bindings)

.NET bindings for [Arkiv](https://github.com/Arkiv-Network/arkiv)'s executor and [GolemDB](https://github.com/Arkiv-Network/golemdb)
state store, used by Nethermind's Arkiv plugin to run Arkiv database chains (Rabdi and the other GolemDB networks).

Arkiv runs no EVM for user transactions and keeps its whole state in GolemDB, whose commitment is the block header's
state root, so a client of these chains has to run Arkiv's own code. `native/arkiv-ffi` is a small C ABI (Rust) over
the crates `arkiv-reth` is built from, pinned to the release the network runs; `Nethermind.Arkiv.Bindings` is its
P/Invoke layer and ships the built library for each platform.

| | |
| --- | --- |
| `native/arkiv-ffi` | The native library: open a chain over a GolemDB store, execute and commit blocks, `eth_call` and `arkiv_*` RPC at the head, build blocks, list a store's accounts |
| `src/Nethermind.Arkiv.Bindings` | `ArkivNative`: the C ABI for .NET; the package, with `runtimes/<rid>/native/libarkiv_ffi.*` |
| `src/Nethermind.Arkiv.Bindings.Tests` | Rabdi's genesis and blocks 1-700 executed through the bindings to their headers' state roots |

Platforms: linux-x64, linux-arm64, osx-arm64, osx-x64; win-x64 and win-arm64 are experimental (packed when they build).

## Build and test

Requirements: .NET 10 SDK or later, Rust (the toolchain `native/arkiv-ffi/rust-toolchain.toml` pins), libclang.

```sh
(cd native/arkiv-ffi && cargo test --release && cargo build --release)
mkdir -p src/Nethermind.Arkiv.Bindings/runtimes/linux-x64/native
cp native/arkiv-ffi/target/release/libarkiv_ffi.so src/Nethermind.Arkiv.Bindings/runtimes/linux-x64/native/
dotnet test --project src/Nethermind.Arkiv.Bindings.Tests -c release
```

If bindgen cannot find `stdbool.h` (libclang without its resource headers), set `BINDGEN_EXTRA_CLANG_ARGS` to a
compiler's include directory, e.g. `-I/usr/lib/gcc/x86_64-linux-gnu/13/include`.

## Releasing

1. Run **Build libarkiv_ffi** (`build-arkiv-ffi.yml`) with *Create a pull request*: it builds every platform and opens
   a pull request committing the binaries (Git LFS) with the source tree they were built from (`runtimes/SOURCE`).
2. Merge it, then run **Test / Publish** with *Staging*, then *Production*. Publishing refuses binaries built from
   another source than the one being published.

The package version is `VersionPrefix` in `src/Directory.Build.props` (the Arkiv release the crates are pinned to) and
the workflow run number. Updating Arkiv means bumping the tags in `native/arkiv-ffi/Cargo.toml`, the version, and
rebuilding the binaries.

## License

This project is licensed under the [MIT](LICENSE) license.

The package also ships prebuilt `libarkiv_ffi` binaries, which statically link Arkiv's crates (MIT), Arkiv's reth fork
(MIT/Apache-2.0) and their dependencies; their licenses are in [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES), generated
with `cargo about generate about.hbs` in `native/arkiv-ffi`.
