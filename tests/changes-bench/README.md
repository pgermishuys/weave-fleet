# changes-bench

Times the session's Changes view on scratch Fleets, so a change to it can be measured before and after on the same
machine. Nothing here touches a real Fleet profile: each Fleet gets its own port, its own database and a scratch
profile folder (HOME, USERPROFILE, APPDATA, LOCALAPPDATA, XDG_*), bound to 127.0.0.1, with the test harness.

```powershell
cd tests/changes-bench
bun install

# 1. Build the Fleet to measure (any commit): client first, then a JIT publish (NativeAOT needs a C++ linker).
#    cd client; bun install; bun run build; cd ..
#    dotnet publish src/WeaveFleet.Api -c Release -o C:\fleet-scratch\changes\bin-x -p:SkipModsHost=true -p:PublishAot=false

# 2. A big public repository for the "big" scenario (once), with long paths on:
#    git clone --depth 1 https://github.com/dotnet/runtime.git C:\fleet-scratch\changes\home\proj\bigrepo
#    cd ...\bigrepo; git config core.longpaths true; git restore --source=HEAD --staged --worktree :/

# 3. Start it, build the invented repositories (--repos, first time) and its sessions
bun run fleet.ts start --bin C:\fleet-scratch\changes\bin-x --home C:\fleet-scratch\changes\home --port 6390
bun run setup.ts --home C:\fleet-scratch\changes\home --base http://127.0.0.1:6390 --repos

# 4. Measure (--ui list for builds before #862, whose Changes was a file list)
bun run bench.ts --base http://127.0.0.1:6390 --home C:\fleet-scratch\changes\home --label x --runs 3

bun run fleet.ts stop --port 6390
```

Several builds can run side by side on different ports against the same repositories (each repository is on a
feature branch, so Changes compares it with where it left main, whatever the session's start). Run the benches one
at a time, on an idle machine.

Scenarios (`--only a,b`): `open-small`, `open-large` (353 files: 300 edited, 40 new, 10 deleted, a 9,000-line rewrite,
a lockfile, a binary), `open-big` (14 files in a 58,000-file repository), `switch` (between sessions with Changes
open), `live` (20 saves 400 ms apart while Changes is open), `find` (Ctrl+F on the large session), `busy` (with
`.git/index.lock` held).

Each records time to the first file row and to a diff drawn, the /diffs requests (count, bytes, slowest), when they
settle, the git processes Fleet ran (count and summed time, from `GIT_TRACE2_EVENT`), and what the panel shows at the
end. Results: `results-<label>.json`.
