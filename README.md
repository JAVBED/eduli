# eduli

A standalone CLI for classic Java MinecraftEdu, extracted from [Codex-Ipsa Launcher](https://github.com/Codex-Ipsa/CodexIpsa-Launcher).

```sh
eduli help
eduli 1.8.9
eduli 1.7.10
```

## GitHub builds

[GitHub Actions](../../actions/workflows/build.yml) builds self-contained x64 executables for Windows, Linux, and macOS on pushes to `main`, pull requests, and manual runs. Download the platform archive from a workflow run's artifacts. Users do not need .NET installed.

Successful pushes and manual runs on `main` publish the three archives plus SHA-256 checksums to a GitHub release named `build-<commit>`. Pushing a version tag such as `v1.0.0` publishes under that tag instead. Rerunning the same commit updates its existing assets. Pull requests build artifacts without publishing releases.

macOS builds target Intel x64. Apple Silicon requires Rosetta 2 because these historical game libraries target Intel. Linux requires a graphical desktop and the usual OpenGL/audio system libraries. Cross-platform game behavior has not yet been verified on macOS or Linux.

## Local game files

Keep `minecraftedu.bundle.zip` beside the executable, or set `EDULI_BUNDLE` to its location. The launcher archives contain the CLI; the large game-data bundle is distributed separately and is not committed to Git. `bundle.json` records its expected size and SHA-256.

The bundle contains 65 supplied installer variants with shared files stored once. Exact version IDs select a variant; short versions select the newest compatible classroom build. The launcher prepares separate game directories and preserves saves/settings between launches.

Java 8 is detected automatically. If a compatible x64 runtime is unavailable, eduli downloads Eclipse Temurin for the current operating system, verifies its checksum, and installs it privately under the data directory.

| Variable | Purpose |
| --- | --- |
| `EDULI_HOME` | Override the data directory. |
| `EDULI_BUNDLE` | Path to the local game bundle. |
| `EDULI_JAVA` | Preferred Java 8 executable. |
| `EDULI_USERNAME` | Local player name; defaults to `Player`. |
| `EDULI_OFFLINE` | Set to `1` to prevent launcher downloads. Java must already be available. |

Default data directories are `%LOCALAPPDATA%\eduli` on Windows, `~/Library/Application Support/eduli` on macOS, and `$XDG_DATA_HOME/eduli` or `~/.local/share/eduli` on Linux. Instances live in `instances/<full-version-id>`. Newer clients use `.minecraft/mods`; older clients use `minecraft/mods` inside the instance.

The game may still contact historical news/skin/resource services. A local player profile is used; Microsoft authentication is not implemented.

## Source and data packaging

The CLI project is `src/eduli.csproj` and uses .NET 10. The GitHub workflow handles publishing.

To regenerate game data, place the supplied installers under `minecraftedu/`, run `python scripts/pack.py`, then `python scripts/prepare_bundle.py dist/minecraftedu.base.zip`. Python 3.11+ is required for these packaging scripts only. Missing platform libraries are fetched from Mojang's library host; their provenance and hashes are recorded in `scripts/platform-downloads.json`.

Launcher source is GPL-3.0; see [LICENSE](LICENSE). Game files and third-party libraries retain their respective licenses.
