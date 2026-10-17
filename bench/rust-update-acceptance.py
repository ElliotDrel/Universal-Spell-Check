"""Operate only the prepared, private QA installation; mutations require --allow-desktop."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import winreg


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest() if path.exists() else None


def protected_matches(snapshot, fixture, production):
    expected = {"fixture/"+name:value for name,value in fixture.items()}
    if production is not None:
        expected.update(production)
    return all(snapshot.get(name) == value for name,value in expected.items())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", required=True)
    parser.add_argument("action", choices=["status", "publish", "offline", "restore-feed", "corrupt", "install", "start", "restart", "stop", "verify", "uninstall"])
    parser.add_argument("--version", type=int, choices=[1, 2, 3], default=1)
    parser.add_argument("--allow-desktop", action="store_true")
    args = parser.parse_args()
    if args.action not in ("status", "verify") and not args.allow_desktop:
        parser.error("Stop here. Desktop actions require Elliot's fresh Run authorization and --allow-desktop.")
    root = Path(args.root).resolve(strict=True)
    prepared = json.loads((root / "prepared.json").read_text(encoding="utf-8"))
    if Path(prepared["root"]).resolve() != root:
        raise ValueError("Prepared root does not match")
    install = root / "install"
    base = root / "localappdata"
    feed = root / "feed"
    exe = install / "current/UniversalSpellCheck.exe"
    updater = install / "Update.exe"
    for path in (install, base, feed, exe, updater):
        if not path.resolve().is_relative_to(root):
            raise ValueError("Path escapes acceptance root: " + str(path))
    env = os.environ.copy()
    env["LOCALAPPDATA"] = str(base)
    data = base / prepared["data_folder"]
    production = Path(prepared["production_data"])
    if production.resolve().is_relative_to(root):
        raise ValueError("Production protection path points into QA root")

    def snapshot():
        values = {"production/"+n:digest(production/n) for n in ("settings.json", "apikey.dat")}
        values.update({"fixture/"+n:digest(data/n) for n in ("settings.json", "apikey.dat")})
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Run") as key:
                values["production_login_startup"] = winreg.QueryValueEx(key, prepared["production_mutex"])[0]
        except FileNotFoundError:
            values["production_login_startup"] = None
        return values

    def assert_known_exe():
        actual = digest(exe)
        if actual not in [v["exe_sha256"] for v in prepared["versions"]]:
            raise ValueError("Installed binary is not one of the prepared QA versions")

    def stop():
        import ctypes
        from ctypes import wintypes
        user = ctypes.WinDLL("user32", use_last_error=True)
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        user.FindWindowW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]
        user.FindWindowW.restype = wintypes.HWND
        user.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
        user.GetWindowThreadProcessId.restype = wintypes.DWORD
        kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
        kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        hwnd = user.FindWindowW(None, prepared["mutex"] + ".HotkeyWindow")
        if not hwnd:
            return
        pid = wintypes.DWORD()
        thread = user.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        process = kernel.OpenProcess(0x101000, False, pid.value)
        if not process:
            raise OSError(ctypes.get_last_error())
        try:
            buffer = ctypes.create_unicode_buffer(32768)
            length = wintypes.DWORD(len(buffer))
            if not kernel.QueryFullProcessImageNameW(process, 0, buffer, ctypes.byref(length)):
                raise OSError(ctypes.get_last_error())
            if Path(buffer.value).resolve() != exe.resolve():
                raise ValueError("Refusing to stop an unexpected process")
            if not user.PostThreadMessageW(thread, 0x12, 0, 0):
                raise OSError(ctypes.get_last_error())
            if kernel.WaitForSingleObject(process, 30000) != 0:
                raise TimeoutError("QA app did not exit gracefully")
        finally:
            kernel.CloseHandle(process)

    before = snapshot()
    fixture = json.loads((root / "fixture-integrity.json").read_text(encoding="utf-8"))
    baseline_file = root / "run-production-baseline.json"
    if not baseline_file.exists() and args.action not in ("status", "verify"):
        baseline_file.write_text(json.dumps({k:v for k,v in before.items() if k.startswith("production/") or k=="production_login_startup"},indent=2),encoding="utf-8")
    production_baseline = json.loads(baseline_file.read_text(encoding="utf-8")) if baseline_file.exists() else None
    result = {"action":args.action, "version":args.version, "time":time.time(), "before":before, "status":"failed"}
    try:
        if not protected_matches(before, fixture, production_baseline):
            raise RuntimeError("Protected data differs from the frozen acceptance baseline")
        if args.action == "verify" and production_baseline is None:
            raise RuntimeError("No authorized acceptance run baseline exists")
        version = prepared["versions"][args.version-1]
        source = Path(version["directory"])
        if args.action == "publish":
            index = json.loads((source / "releases.win.json").read_text(encoding="utf-8-sig"))
            for asset in index["Assets"]:
                if asset["PackageId"] != prepared["package_id"] or Path(asset["FileName"]).name != asset["FileName"]:
                    raise ValueError("Unexpected release asset")
                shutil.copy2(source / asset["FileName"], feed / asset["FileName"])
            shutil.copy2(source / "releases.win.json", feed / "releases.win.json")
        elif args.action == "offline":
            if (feed / "releases.win.json.offline").exists():
                raise ValueError("Feed already offline")
            (feed / "releases.win.json").rename(feed / "releases.win.json.offline")
        elif args.action == "restore-feed":
            if (feed / "releases.win.json").exists():
                raise ValueError("Refusing to replace an active index")
            (feed / "releases.win.json.offline").rename(feed / "releases.win.json")
        elif args.action == "corrupt":
            index = json.loads((feed / "releases.win.json").read_text(encoding="utf-8-sig"))
            for asset in index["Assets"]:
                if asset["Version"] == version["version"]:
                    name = asset["FileName"]
                    if Path(name).name != name:
                        raise ValueError("Invalid package filename")
                    with (feed/name).open("r+b") as handle:
                        handle.seek(-1, 2)
                        byte = handle.read(1)
                        handle.seek(-1, 2)
                        handle.write(bytes([byte[0] ^ 0xff]))
        elif args.action == "install":
            if install.exists():
                raise ValueError("Install target already exists")
            registry_path = r"Software\Microsoft\Windows\CurrentVersion\Uninstall" + "\\" + prepared["package_id"]
            for hive in (winreg.HKEY_CURRENT_USER, winreg.HKEY_LOCAL_MACHINE):
                try:
                    with winreg.OpenKey(hive, registry_path):
                        raise ValueError("An installation already owns this QA package identity")
                except FileNotFoundError:
                    pass
            setup = list(source.glob("*-Setup.exe"))
            if len(setup) != 1:
                raise ValueError("Expected one prepared installer")
            subprocess.run([str(setup[0]), "--silent", "--installto", str(install), "--log", str(root/"results/setup.log")], env=env, check=True, timeout=120)
            assert_known_exe()
        elif args.action in ("start", "restart"):
            assert_known_exe()
            command = [str(exe), "--dashboard"] if args.action == "start" else [str(exe), "--restart"]
            child = subprocess.Popen(command, env=env, creationflags=subprocess.CREATE_NO_WINDOW)
            result["invoked_pid"] = child.pid
        elif args.action == "stop":
            stop()
        elif args.action == "verify":
            assert_known_exe()
            if digest(exe) != version["exe_sha256"]:
                raise ValueError("Unexpected installed version")
            if (install/"current/acceptance-version.txt").read_text() != version["version"]:
                raise ValueError("Version marker did not update")
        elif args.action == "uninstall":
            assert_known_exe()
            protected = [p for p in install.rglob("*") if "backup" in p.name.lower() or p.name.upper().startswith("BEFORE-") or p.name.lower().endswith(".bak")]
            if protected:
                raise ValueError("Protected backup names prevent uninstall: " + repr(protected))
            stop()
            subprocess.run([str(updater), "--silent", "--rootDir", str(install), "--log", str(root/"results/uninstall.log"), "uninstall"], env=env, check=True, timeout=120)
        result["installed_sha256"] = digest(exe)
        result["status"] = "invoked" if args.action in ("start", "restart") else "passed"
    except Exception as error:
        result["error"] = str(error)
        raise
    finally:
        result["after"] = snapshot()
        result["protected_data_unchanged"] = result["after"] == before and protected_matches(result["after"], fixture, production_baseline)
        target = root / "results" / (str(time.time_ns())+"-"+args.action+".json")
        target.write_text(json.dumps(result,indent=2),encoding="utf-8")
        if not result["protected_data_unchanged"]:
            raise RuntimeError("Protected data changed from the frozen baseline; stop testing")
    print(json.dumps(result,indent=2))


if __name__ == "__main__":
    main()
