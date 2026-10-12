# FFmpeg toolchain

MediaPager downloads checksum-pinned FFmpeg and FFprobe binaries at first use into the
managed tools directory (`Tools:Directory/ffmpeg/9.0.2/<runtime-id>`). It does not search
the host `PATH` or install software through the host operating system's package manager.

The pinned platform builds are GPL-enabled and include GPL codec implementations such as
x264 and x265. The FFmpeg codebase and its optional components have their own licensing
terms; see the [FFmpeg license and legal considerations](https://ffmpeg.org/legal.html),
the [FFmpeg source releases](https://ffmpeg.org/releases/), and the build-provider metadata
recorded in each installed `tool-manifest.json`.

Build sources:

- Windows x64: [BtbN FFmpeg Builds](https://github.com/BtbN/FFmpeg-Builds), GPL static 9.0
  release branch.
- Linux and macOS x64/arm64: [Martin Riedl's FFmpeg Build Server](https://ffmpeg.martin-riedl.de/),
  FFmpeg 9.0.2 release builds.

The installer verifies each archive's pinned SHA-256 before extraction, keeps the exact
archive URLs/checksums and build identifier beside the installed tools, and rechecks the
extracted executable hashes before reuse. The provider build pages publish the component
versions and codec configuration for each platform archive.
