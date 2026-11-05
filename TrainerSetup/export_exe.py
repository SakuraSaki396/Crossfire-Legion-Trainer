"""Copies the built installer to the project root under its delivered filename.

Kept in Python on purpose: cmd.exe reads .bat files with the OEM/ANSI code page,
which corrupts non-ASCII paths, so the export step must not live in the batch file.

The version in the file name comes from ..\\version.txt, the same file both csproj
files read, so the exe name, the exe's own version resource and the in-game panel
version can never drift apart. Older `Crossfire Legion 修改器 v*.exe` builds are
removed afterwards so only the current one is left in the folder.
"""

import glob
import hashlib
import os
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SOURCE = os.path.join(HERE, "bin", "Release", "net48", "CrossfireTrainerSetup.exe")
VERSION_FILE = os.path.join(ROOT, "version.txt")
NAME = "Crossfire Legion 修改器 v%s.exe"


def main():
    if not os.path.exists(SOURCE):
        print("missing build output: " + SOURCE)
        return 1

    version = open(VERSION_FILE, encoding="utf-8").read().strip()
    if not version:
        print("version.txt is empty")
        return 1

    target = os.path.join(ROOT, NAME % version)
    shutil.copy2(SOURCE, target)

    # drop older builds, keep the one we just wrote
    for old in glob.glob(os.path.join(ROOT, NAME % "*")):
        if os.path.abspath(old) != os.path.abspath(target):
            try:
                os.remove(old)
                print("removed old build: " + os.path.basename(old))
            except OSError as e:
                print("could not remove %s: %s" % (old, e))

    data = open(target, "rb").read()
    print("exported: %s" % target)
    print("version: %s" % version)
    print("size: %d bytes" % len(data))
    print("sha256: %s" % hashlib.sha256(data).hexdigest()[:32])
    return 0


if __name__ == "__main__":
    sys.exit(main())
