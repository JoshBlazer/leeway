#!/usr/bin/env python3
"""Checks that the real NuGet packages work, by compiling an outside project against them.

A generator can pass every in-process test, yet fail to load from a package. This packs the libraries into a local
feed, builds scripts/package-consumer against that feed, and checks three things: the generated API compiled, LEE001
and LEE002 appeared, and no analyzer failed to load.

Usage, from the repository root:   python scripts/package-check.py
"""
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path


def main():
    env = dict(os.environ, DOTNET_NOLOGO="1", DOTNET_CLI_TELEMETRY_OPTOUT="1")
    root = Path(__file__).resolve().parent.parent
    with tempfile.TemporaryDirectory(prefix="leeway-package-check-") as temp:
        work = Path(temp)
        feed = work / "feed"
        consumer = work / "consumer"
        shutil.copytree(root / "scripts" / "package-consumer", consumer, ignore=shutil.ignore_patterns("bin", "obj"))

        pack = subprocess.run(["dotnet", "pack", "Leeway.slnx", "-c", "Release", "-o", str(feed)], cwd=root, capture_output=True, text=True, env=env)
        if pack.returncode != 0:
            print(pack.stdout[-2000:])
            print("FAILED: dotnet pack")
            return 1

        print("Packages:", ", ".join(sorted(p.name for p in feed.glob("*.nupkg"))))
        (consumer / "nuget.config").write_text(
            f'<configuration><packageSources><clear /><add key="local" value="{feed}" />'
            '<add key="nuget" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>')

        # A private package folder, so that a package from an earlier run cannot be used by mistake.
        env["NUGET_PACKAGES"] = str(work / "packages")
        build = subprocess.run(["dotnet", "build", "-nologo"], cwd=consumer, capture_output=True, text=True, env=env)

        failures = []
        if build.returncode != 0:
            failures.append("the consumer did not compile, so the generated API may be missing")
        for rule in ("LEE001", "LEE002"):
            if f"warning {rule}" not in build.stdout:
                failures.append(f"{rule} did not appear")
        if "CS8032" in build.stdout or "AD0001" in build.stdout:
            failures.append("an analyzer or generator failed to load or crashed")

        if failures:
            print(build.stdout[-3000:])
            print("FAILED:", "; ".join(failures))
            return 1

        print("Generated API compiled, and LEE001 and LEE002 appeared.")
        print("PACKAGE CHECK PASSED")
        return 0


if __name__ == "__main__":
    sys.exit(main())
