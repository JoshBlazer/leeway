#!/usr/bin/env python3
"""Breaks each protection in the Datasync adapters in turn, and checks that the integration tests notice.

The simulator's mutants test the protocol logic in Leeway.Core. They cannot reach the adapters, so this does
that job for them. The first run of this idea found three protections that no test was really covering.

Usage, from the repository root, with a clean working tree:   python scripts/adapter-sabotage.py [--hardening-only]
It edits source files, restores them after each case, and exits non-zero if any sabotage goes unnoticed.
A case without a test class runs the whole integration suite, at about four minutes each.
"""
import os
import re
import subprocess
import sys

CLIENT = "src/Leeway.Datasync.Client/LeewayClient.cs"
CONTEXT = "src/Leeway.Datasync.Client/LeewayDbContext.cs"
SERVER = "src/Leeway.Datasync.Server/LeewayAuthority.cs"
ACCESS = "src/Leeway.Datasync.Server/Controllers.cs"
HARDENING = "Leeway.Integration.Tests.Leeway.HardeningTests"
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
    ("A device can send operations as another device", ACCESS,
     "authority.IsOpen || (authority.CurrentDevice is { } device && device == entity?.Device)", "authority.IsOpen || authority.CurrentDevice is not null", HARDENING),
    ("Entities can be read with no identity", ACCESS,
     "public override ValueTask<bool> IsAuthorizedAsync(TableOperation operation, T? entity, CancellationToken cancellationToken = default)\n        => ValueTask.FromResult(operation is TableOperation.Query or TableOperation.Read && (authority.IsOpen || authority.CurrentDevice is not null));",
     "public override ValueTask<bool> IsAuthorizedAsync(TableOperation operation, T? entity, CancellationToken cancellationToken = default)\n        => ValueTask.FromResult(operation is TableOperation.Query or TableOperation.Read);", HARDENING),
    ("A device can read every lease", ACCESS,
     "        string? device = authority.CurrentDevice;\n        return x => x.Device == device;\n    }\n}\n\n/// <summary>Clients may only append",
     "        return null;\n    }\n}\n\n/// <summary>Clients may only append", HARDENING),
    ("A replay reveals another device's operation", ACCESS,
     "        string? device = authority.CurrentDevice;\n        return x => x.Device == device;\n    }\n}\n\n/// <summary>Stores an operation",
     "        return null;\n    }\n}\n\n/// <summary>Stores an operation", HARDENING),
    ("Startup allows no decision on authentication", SERVER,
     "if (options.AuthenticateDevice is null && !options.AllowUnauthenticatedDevices)", "if (false)", HARDENING),
    ("Server pruning ignores acknowledgements", SERVER,
     ".Where(x => x.Device == progress.Device && x.Sequence < progress.SettledBelow)", ".Where(x => x.Device == progress.Device)", HARDENING),
    ("Server forgets to record a device's progress", SERVER,
     "progress.SettledBelow = op.SettledBelow;", "", HARDENING),
    ("Client pruning removes unsettled operations", CLIENT,
     ".Where(x => x.State == OutboxState.Acknowledged || x.State == OutboxState.Discarded)\n                .OrderByDescending(x => x.Sequence)",
     ".OrderByDescending(x => x.Sequence)", HARDENING),
    ("Any failed operation can be discarded", CLIENT,
     "queued.State != CommunityToolkit.Datasync.Client.Offline.OperationState.Failed || !IsClearRefusal(queued.HttpStatusCode)",
     "queued.State != CommunityToolkit.Datasync.Client.Offline.OperationState.Failed", HARDENING),
    ("Discarding does not give the wallet back", CLIENT,
     "wallet.Spent -= op.Kind == OperationKind.Spend ? op.Amount : 0;", "", HARDENING),
    ("The server ignores a delete", SERVER,
     "entity.Deleted = MergeSchema.IsDeleted(document);", "", HARDENING),
    ("The client sends everything at once, unordered", CLIENT,
     "LeewayOutboxRow? next = await context.LeewayOutbox.Where(x => x.State == OutboxState.Pending).OrderBy(x => x.Sequence).FirstOrDefaultAsync(cancellationToken);",
     "LeewayOutboxRow? next = await context.LeewayOutbox.Where(x => x.State == OutboxState.Pending).OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(cancellationToken);", HARDENING),
]


def run_suite(env, test_class=None):
    """Returns the names of failed tests, or None if the suite did not produce a result."""
    build = subprocess.run(["dotnet", "build", SUITE, "-nologo", "-v", "q"], capture_output=True, text=True, env=env, timeout=300)
    if build.returncode != 0:
        print("    The sabotaged code did not build:", [l for l in build.stdout.splitlines() if " error " in l][:1], flush=True)
        return None

    exe = os.path.abspath(f"{SUITE}/bin/Debug/net10.0/Leeway.Integration.Tests")
    exe += ".exe" if os.name == "nt" else ""
    try:
        arguments = [exe, "-noColor"] + (["-class", test_class] if test_class else [])
        run = subprocess.run(arguments, capture_output=True, text=True, errors="replace", env=env, timeout=LIMIT_SECONDS)
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
    cases = [c for c in CASES if len(c) == 5] if "--hardening-only" in sys.argv else CASES
    for name, path, old, new, *rest in cases:
        raw = open(path, encoding="utf-8", newline="").read()
        source = raw.replace("\r\n", "\n")
        if old not in source:
            print(f"{name}: the code has moved, so update this script.", flush=True)
            continue

        open(path, "w", encoding="utf-8", newline="\n").write(source.replace(old, new, 1))
        try:
            failed = run_suite(env, rest[0] if rest else None)
        finally:
            open(path, "w", encoding="utf-8", newline="").write(raw)

        if failed:
            noticed += 1
            print(f"{name}: CAUGHT by {len(failed)} test(s)", flush=True)
            for test in failed[:3]:
                print(f"    {test}", flush=True)
        else:
            print(f"{name}: {'NO RESULT' if failed is None else 'NOT CAUGHT'}", flush=True)

    print(f"Caught {noticed} of {len(cases)}.", flush=True)
    return 0 if noticed == len(cases) else 1


if __name__ == "__main__":
    sys.exit(main())
