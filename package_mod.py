import shutil
import sys
import zipfile
from pathlib import Path


MOD_ID = "AsyncInput"
ROOT = Path(__file__).resolve().parent


def find_dll() -> Path | None:
    candidates = [
        ROOT / "MobilePlugin" / "bin" / "Release" / "net10.0" / f"{MOD_ID}.dll",
        ROOT / f"{MOD_ID}.dll",
    ]
    for candidate in candidates:
        if candidate.is_file():
            return candidate

    return None


def main() -> int:
    version = (ROOT / "VERSION.txt").read_text(encoding="utf-8").strip()
    if not version:
        print("VERSION.txt is empty.", file=sys.stderr)
        return 1

    dll = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else find_dll()
    if dll is None or not dll.is_file():
        print(f"{MOD_ID}.dll was not found.", file=sys.stderr)
        print(f"Build MobilePlugin first, or pass the DLL path: python3 package_mod.py path/to/{MOD_ID}.dll", file=sys.stderr)
        return 1

    tmp = ROOT / "tmp_package"
    mod_dir = tmp / MOD_ID
    zip_path = ROOT / f"{MOD_ID}-{version}.zip"

    if tmp.exists():
        shutil.rmtree(tmp)
    if zip_path.exists():
        zip_path.unlink()
    mod_dir.mkdir(parents=True)

    shutil.copy2(dll, mod_dir / f"{MOD_ID}.dll")

    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(mod_dir.rglob("*")):
            if path.is_file():
                archive.write(path, path.relative_to(tmp))

    shutil.rmtree(tmp)
    print(zip_path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
