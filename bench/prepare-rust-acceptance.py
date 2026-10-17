"""Build local acceptance packages without launching the app or an installer."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
from rust_acceptance_data import seed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", required=True)
    parser.add_argument("--vpk", required=True, help="Extracted net10.0 vpk.dll")
    parser.add_argument("--dotnet", required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    root = Path(args.root).resolve()
    root.mkdir(parents=True, exist_ok=True)
    if (root / "prepared.json").exists():
        raise ValueError("This acceptance root is already prepared; choose a fresh root")
    for part in ("feed", "localappdata", "payloads", "releases", "results"):
        (root / part).mkdir(exist_ok=True)
    env = os.environ.copy()
    env["CARGO_INCREMENTAL"] = "0"
    env["DOTNET_ROOT"] = str(Path(args.dotnet).resolve().parent)
    env["PATH"] = env["DOTNET_ROOT"] + os.pathsep + env.get("PATH", "")
    env["USC_ACCEPTANCE_ROOT"] = str(root)
    source = (repo / "src/BuildChannel.cs").read_text(encoding="utf-8")
    constants = {name: re.search(r'const string '+name+r' = "([^";]+)";', source).group(1)
                 for name in ("AcceptancePackageId", "AcceptanceDisplayName", "AcceptanceMutexName", "SharedDataFolder")}
    versions = ["0.13.0-acceptance." + str(i) for i in (1, 2, 3)]
    checks = []

    def run(name, command, child_env=env):
        result = subprocess.run(command, cwd=repo, env=child_env, stdout=subprocess.PIPE,
                                stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
        log = root / "results" / (name + ".log")
        if log.exists():
            attempt = 1
            while log.with_name(name + ".attempt-" + str(attempt) + ".log").exists():
                attempt += 1
            log.rename(log.with_name(name + ".attempt-" + str(attempt) + ".log"))
        log.write_text(result.stdout, encoding="utf-8")
        checks.append({"name": name, "exit": result.returncode})
        (root / "results/checks.json").write_text(json.dumps(checks, indent=2), encoding="utf-8")
        print(name, result.returncode, flush=True)
        if result.returncode:
            raise RuntimeError(result.stdout[-7000:])

    run("format", ["cargo", "fmt", "--manifest-path", "rust/Cargo.toml", "--check"])
    run("helper-guards", ["python", "-m", "unittest", "discover", "-s", "bench", "-p", "test_rust_acceptance.py"])
    for name, flags in (("dev", []), ("prod", ["--no-default-features"]),
                        ("acceptance", ["--no-default-features", "--features", "acceptance"])):
        env["USC_ACCEPTANCE_VERSION"] = versions[0]
        run("tests-"+name, ["cargo", "test", "--locked", "--manifest-path", "rust/Cargo.toml"]+flags)
        run("clippy-"+name, ["cargo", "clippy", "--locked", "--manifest-path", "rust/Cargo.toml", "--all-targets"]+flags+["--", "-D", "warnings"])
    packages = []
    for index, version in enumerate(versions):
        env["USC_ACCEPTANCE_VERSION"] = version
        run("build-"+version, ["cargo", "build", "--locked", "--manifest-path", "rust/Cargo.toml", "--release", "--no-default-features", "--features", "acceptance"])
        payload = root / "payloads" / version
        run("stage-"+version, ["pwsh", "-NoProfile", "-File", str(repo / "rust/package.ps1"), "-SkipBuild", "-Output", str(payload)])
        (payload / "acceptance-version.txt").write_text(version, encoding="utf-8")
        release = root / "releases" / version
        command = [args.dotnet, args.vpk, "pack", "--packId", constants["AcceptancePackageId"],
                   "--packVersion", version, "--packDir", str(payload), "--mainExe", "UniversalSpellCheck.exe",
                   "--packTitle", constants["AcceptanceDisplayName"], "--outputDir", str(release),
                   "--runtime", "win-x64", "--channel", "win", "--delta", "None", "--shortcuts", "None",
                   "--noPortable", "--skipVeloAppCheck"]
        run("pack-"+version, command)
        packages.append({"version": version, "directory": str(release),
                         "exe_sha256": hashlib.sha256((payload / "UniversalSpellCheck.exe").read_bytes()).hexdigest()})
    normal_env = env.copy()
    normal_env.pop("USC_ACCEPTANCE_ROOT", None)
    normal_env.pop("USC_ACCEPTANCE_VERSION", None)
    run("build-editor-candidate", ["cargo", "build", "--locked", "--manifest-path", "rust/Cargo.toml", "--release", "--no-default-features"], normal_env)
    run("package-editor-candidate", ["pwsh", "-NoProfile", "-File", str(repo / "rust/package.ps1"), "-SkipBuild", "-Output", str(root / "editor-candidate")], normal_env)
    seed(root, constants["SharedDataFolder"])
    (root / "prepared.json").write_text(json.dumps({"status":"prepared-not-run", "package_id":constants["AcceptancePackageId"],
        "root":str(root), "localappdata":str(root/"localappdata"), "install_target":str(root/"install"),
        "data_folder":constants["SharedDataFolder"], "mutex":constants["AcceptanceMutexName"],
        "production_data":str(Path(os.environ["LOCALAPPDATA"])/constants["SharedDataFolder"]),
        "production_mutex":re.search(r'const string MutexName = "([^";]+)";', source.split("#else")[1].split("#endif")[0]).group(1),
        "versions":packages, "editor_candidate":str(root/"editor-candidate"), "desktop_actions_performed":False,
        "checks":checks}, indent=2), encoding="utf-8")
    print("Prepared only. No application, installer, hotkey or clipboard test was run.", flush=True)


if __name__ == "__main__":
    main()
