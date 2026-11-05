"""Collects the deployed trainer files from the game folder into payload.zip.

Only the files the trainer actually needs are included; logs, caches and the
BepInEx global config cache are skipped so the package stays tiny.
"""

import os
import shutil
import sys
import zipfile

DEFAULT_GAME = os.environ.get(
    "CFL_GAME_DIR", r"E:\SteamLibrary\steamapps\common\Crossfire Legion (Demo)")

HERE = os.path.dirname(os.path.abspath(__file__))
STAGE = os.path.join(HERE, "payload")
ZIP_PATH = os.path.join(HERE, "payload.zip")

REQUIRED = [
    "winhttp.dll",
    "doorstop_config.ini",
    ".doorstop_version",
    r"BepInEx\config\BepInEx.cfg",
    r"BepInEx\plugins\CrossfireTrainer.dll",
    r"Data\Scripts\Source\Extensions\zzz_trainer.lua",
]

# whole BepInEx sub-trees worth shipping
TREES = ["BepInEx\\core"]


def main():
    game = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_GAME

    for rel in REQUIRED:
        if not os.path.exists(os.path.join(game, rel)):
            print("missing: " + rel)
            print("game dir is not ready: " + game)
            return 1

    if os.path.isdir(STAGE):
        shutil.rmtree(STAGE)
    os.makedirs(STAGE)

    def copy(rel):
        src = os.path.join(game, rel)
        dst = os.path.join(STAGE, rel)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(src, dst)

    for rel in REQUIRED:
        copy(rel)

    for tree in TREES:
        base = os.path.join(game, tree)
        for name in os.listdir(base):
            copy(os.path.join(tree, name))

    if os.path.exists(ZIP_PATH):
        os.remove(ZIP_PATH)

    count = 0
    with zipfile.ZipFile(ZIP_PATH, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for root, _dirs, files in os.walk(STAGE):
            for name in files:
                full = os.path.join(root, name)
                arc = os.path.relpath(full, STAGE).replace("\\", "/")
                z.write(full, arc)
                count += 1

    print("files: %d, zip: %d bytes" % (count, os.path.getsize(ZIP_PATH)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
