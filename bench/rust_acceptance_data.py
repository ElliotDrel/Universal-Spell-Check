"""Create synthetic, encrypted QA data without accessing production credentials."""
import ctypes
import hashlib
import json
from pathlib import Path


def seed(root, folder):
    root = Path(root).resolve()
    data = root / "localappdata" / folder
    data.mkdir(parents=True, exist_ok=True)
    (data / "logs").mkdir(exist_ok=True)
    if not data.resolve().is_relative_to(root):
        raise ValueError("QA data escapes its root")
    for name in ("settings.json", "apikey.dat"):
        if (data / name).exists():
            raise ValueError("Refusing to overwrite QA fixture: " + name)
    settings = {"Model":"gpt-4.1", "DeveloperLogging":False, "StartOnLogin":False,
                "AcceptanceSentinel":"preserve-across-update"}
    (data / "settings.json").write_text(json.dumps(settings,indent=2),encoding="utf-8")

    class Blob(ctypes.Structure):
        _fields_ = [("cbData",ctypes.c_ulong),("pbData",ctypes.POINTER(ctypes.c_ubyte))]

    plaintext = json.dumps({"ActiveKeyId":"synthetic-qa", "Keys":[{"Id":"synthetic-qa",
        "Name":"Synthetic acceptance key", "Value":"acceptance-not-a-real-api-key"}]}).encode()
    buffer = (ctypes.c_ubyte * len(plaintext)).from_buffer_copy(plaintext)
    source = Blob(len(plaintext),buffer)
    encrypted = Blob()
    crypt = ctypes.WinDLL("crypt32",use_last_error=True)
    kernel = ctypes.WinDLL("kernel32",use_last_error=True)
    signature = [ctypes.POINTER(Blob),ctypes.c_void_p,ctypes.c_void_p,ctypes.c_void_p,
                 ctypes.c_void_p,ctypes.c_ulong,ctypes.POINTER(Blob)]
    crypt.CryptProtectData.argtypes = signature
    crypt.CryptUnprotectData.argtypes = signature
    kernel.LocalFree.argtypes = [ctypes.c_void_p]
    kernel.LocalFree.restype = ctypes.c_void_p
    if not crypt.CryptProtectData(ctypes.byref(source),None,None,None,None,1,ctypes.byref(encrypted)):
        raise OSError(ctypes.get_last_error())
    try:
        (data / "apikey.dat").write_bytes(ctypes.string_at(encrypted.pbData,encrypted.cbData))
        decoded = Blob()
        if not crypt.CryptUnprotectData(ctypes.byref(encrypted),None,None,None,None,1,ctypes.byref(decoded)):
            raise OSError(ctypes.get_last_error())
        try:
            assert ctypes.string_at(decoded.pbData,decoded.cbData) == plaintext
        finally:
            kernel.LocalFree(decoded.pbData)
    finally:
        kernel.LocalFree(encrypted.pbData)
    (data / "startup.initialized").write_text("Synthetic QA fixture; login writes disabled in acceptance build.",encoding="utf-8")
    hashes = {n:hashlib.sha256((data/n).read_bytes()).hexdigest() for n in ("settings.json","apikey.dat")}
    (root / "fixture-integrity.json").write_text(json.dumps(hashes,indent=2),encoding="utf-8")
    return hashes
