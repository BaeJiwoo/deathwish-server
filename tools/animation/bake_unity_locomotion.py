"""Generate Bob's native Unity Generic walk and stand clips in a temporary Unity project."""
import argparse
from pathlib import Path
import shutil
import subprocess
import tempfile
from zipfile import ZipFile

SERVER = Path(__file__).resolve().parents[2]
RESOURCES = SERVER / "integrations/Unity/Resources"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity-editor", required=True, type=Path, help="Unity 6000.3.11f1 editor executable")
    parser.add_argument("--assets", default=SERVER.parent / "deathwish-assets", type=Path)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="deathwish-bake-walk-") as temp:
        project = Path(temp)
        assets = project / "Assets"
        (assets / "Editor").mkdir(parents=True)
        (project / "ProjectSettings").mkdir()
        (project / "ProjectSettings/ProjectVersion.txt").write_text("m_EditorVersion: 6000.3.11f1\n")
        (project / "Packages").mkdir()
        (project / "Packages/manifest.json").write_text('{"dependencies":{"com.unity.modules.animation":"1.0.0"}}\n')
        shutil.copyfile(SERVER / "tools/animation/unity/BakeShooterLocomotion.cs", assets / "Editor/BakeShooterLocomotion.cs")
        shutil.copyfile(args.assets / "Characters/bob_rigged.fbx", assets / "bob_rigged.fbx")
        with ZipFile(args.assets / "Animations/ShooterPack.zip") as pack:
            pack.extract("walk forward.fbx", assets)
        result = subprocess.run([str(args.unity_editor.resolve()), "-batchmode", "-nographics", "-quit", "-projectPath",
                                 str(project), "-executeMethod", "BakeShooterLocomotion.Run", "-logFile", str(project / "bake.log")])
        log_path = project / "bake.log"
        log = log_path.read_text(errors="replace") if log_path.is_file() else ""
        if result.returncode:
            print(log[-12000:])
            raise SystemExit(result.returncode)
        print(next(line for line in log.splitlines() if line.startswith("Walk verified")))
        for name in ("ShooterStand.anim", "ShooterWalk.anim"):
            generated = (assets / name).read_bytes()
            if args.check:
                if generated != (RESOURCES / name).read_bytes():
                    raise SystemExit(f"{name} differs from the checked-in clip.")
            else:
                (RESOURCES / name).write_bytes(generated)
                meta = RESOURCES / (name + ".meta")
                if not meta.exists():
                    shutil.copyfile(assets / (name + ".meta"), meta)


if __name__ == "__main__":
    main()
