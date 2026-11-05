#!/usr/bin/env python3
"""Breaks each protection in the Datasync adapters in turn, and checks that the integration tests notice.

The simulator's mutants test the protocol logic in Leeway.Core. They cannot reach the adapters, so this does
that job for them. The first run of this idea found three protections that no test was really covering.

Usage, from the repository root, with a clean working tree:   python scripts/adapter-sabotage.py
It edits source files, restores them after each case, and exits non-zero if any sabotage goes unnoticed.
It takes about two minutes per case, because the integration suite does.
"""
import os
import re
import subprocess
import sys

CLIENT = "src/Leeway.Datasync.Client/LeewayClient.cs"
CONTEXT = "src/Leeway.Datasync.Client/LeewayDbContext.cs"
SERVER = "src/Leeway.Datasync.Server/LeewayAuthority.cs"
SUITE = "tests/Leeway.Integration.Tests"
LIMIT_SECONDS = 420

CASES = [
    ("Server stamps with the plain clock (C19)", SERVER,
     "authority.LastStampMs = Math.Max(Now, authority.LastStampMs + 1);", "authority.LastStampMs = Now;"),
    ("Server does not check for a replay", SERVER,
     "if (existing is not null)", 'if (existing is null && row.Id == "never")'),
    ("Server ignores what the writer had seen", SERVER,
     "stamps.Admit(op, Now)", "stamps.Admit(op with { Observed = default }, Now)"),
    ("Client drops settling operations from the view", CLIENT,
     "(x.State != OutboxState.Acknowledged || x.EntityStamp > rowStamp)", "(x.State != OutboxState.Acknowledged)"),
    ("Client trusts PushResult (C24)", CLIENT,
     "if (unreachable || queue.Count > rejected)", "if (unreachable)"),
    ("Wallet update has no balance check", CLIENT,
     "w.Resource == resource && w.Spent + w.Returned + amount <= granted", "w.Resource == resource"),
    ("Client does not reapply local changes after a pull", CLIENT,
     "            await ReprojectAsync(context, row, cancellationToken);\n        }\n\n        return highest;",
     "        }\n\n        return highest;"),
    ("No ServerWins resolver on the operation table", CONTEXT,
     "cfg.ConflictResolver = new ServerWinsConflictResolver();", ""),
]


def run_suite(env):
    """Returns the names of failed tests, or None if the suite did not produce a result."""
    build = subprocess.run(["dotnet", "build", SUITE, "-nologo", "-v", "q"], capture_output=True, text=True, env=env, timeout=300)
    if build.returncode != 0:
        print("    The sabotaged code did not build:", [l for l in build.stdout.splitlines() if " error " in l][:1], flush=True)
        return None

    exe = os.path.abspath(f"{SUITE}/bin/Debug/net10.0/Leeway.Integration.Tests")
    exe += ".exe" if os.name == "nt" else ""
    try:
        run = subprocess.run([exe, "-noColor"], capture_output=True, text=True, errors="replace", env=env, timeout=LIMIT_SECONDS)
    except subprocess.TimeoutExpired:
        print(f"    No result within {LIMIT_SECONDS}s.", flush=True)
        return None

    # A run that executed no tests must never read as "survived".
    if "Total:" not in run.stdout:
        print("    The suite did not run:", run.stdout[:200], flush=True)
        return None

    return sorted(set(re.findall(r"\.(\w+) \[FAIL\]", run.stdout)))


def main():
    env = dict(os.environ, AVALONIA_TELEMETRY_OPTOUT="1")
    noticed = 0
    for name, path, old, new in CASES:
        raw = open(path, encoding="utf-8", newline="").read()
        source = raw.replace("\r\n", "\n")
        if old not in source:
            print(f"{name}: the code has moved, so update this script.", flush=True)
            continue

        open(path, "w", encoding="utf-8", newline="\n").write(source.replace(old, new, 1))
        try:
            failed = run_suite(env)
        finally:
            open(path, "w", encoding="utf-8", newline="").write(raw)

        if failed:
            noticed += 1
            print(f"{name}: CAUGHT by {len(failed)} test(s)", flush=True)
            for test in failed[:3]:
                print(f"    {test}", flush=True)
        else:
            print(f"{name}: {'NO RESULT' if failed is None else 'NOT CAUGHT'}", flush=True)

    print(f"Caught {noticed} of {len(CASES)}.", flush=True)
    return 0 if noticed == len(CASES) else 1


if __name__ == "__main__":
    sys.exit(main())
