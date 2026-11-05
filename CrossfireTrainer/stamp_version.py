"""Writes the version from version.txt into zzz_trainer.lua.

The Lua script carries its own version only so the log line reads naturally; the
authoritative number lives in version.txt (read by both csproj files). Stamping it
here keeps the two from drifting apart. Idempotent: it only rewrites the one line.
"""

import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
VERSION_FILE = os.path.join(os.path.dirname(HERE), "version.txt")
LUA = os.path.join(HERE, "zzz_trainer.lua")


def main():
    version = open(VERSION_FILE, encoding="utf-8").read().strip()
    if not version:
        print("version.txt is empty")
        return 1

    text = open(LUA, encoding="utf-8", newline="").read()
    new, n = re.subn(r'(?m)^T\.version\s*=\s*"[^"]*"',
                     'T.version = "%s"' % version, text)
    if n != 1:
        print("expected exactly one T.version line, found %d" % n)
        return 1
    if new != text:
        open(LUA, "w", encoding="utf-8", newline="").write(new)
        print("zzz_trainer.lua stamped v" + version)
    else:
        print("zzz_trainer.lua already v" + version)
    return 0


if __name__ == "__main__":
    sys.exit(main())
