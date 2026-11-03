# Third-party components

GameLauncherWithGit is distributed under GPL-3.0-only; see `LICENSE`. Dependencies retain their own copyright and license notices.

## Native scenario capture

- LunaTranslator / LunaHook: HIllya51 and upstream contributors, GNU GPL v3. Source is the `library/LunaTranslator` Git submodule at `1f784b1a471f3a51a3e130a37d257333f4b9439f`. Its engine detection, Host, Hook and font loader are built without upstream source edits. The injector compiles upstream `src/NativeImpl/LunaSubprocess/dllinject.cpp` with a GameLauncher entry point.
- MinHook: Tsuda Kageyu and Vyacheslav Patkov, BSD-style terms. CMake fetches commit `8af6b4acae5a9388fd742b56fa79ece89d96f823`. Its complete copyright, redistribution conditions and disclaimers are in `MinHook-LICENSE.txt` beside the native binaries.
- uchardet: upstream contributors listed in `uchardet-AUTHORS.txt`; Mozilla-derived sources include MPL 1.1 / GPL 2.0-or-later / LGPL 2.1-or-later terms. CMake fetches commit `06029ec3340cdf6bf9a6a537dafb3f39eda0560e`; the supplied `uchardet-COPYING.txt` retains the upstream license texts. The build applies the source transformations defined by LunaHook's CMake configuration (static symbol/build compatibility), not a separately maintained copy.

`scripts/build-luna-native.ps1` places these notices and LunaTranslator's license beside the native components. Corresponding source is obtained with `git submodule update --init --recursive`; dependency sources and build changes are available through the pinned CMake declarations and this repository's `native/` directory. Binary redistributors must provide the corresponding source and build instructions for the version they distribute under the applicable licenses. A repository URL alone does not replace those obligations.

Managed NuGet dependencies and `library/MonochromeMemory.Log` remain subject to their package/source license terms. This file describes the native capture components; it is not a certification of a release package's complete license compliance.
