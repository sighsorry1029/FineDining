"""Original game Mono, managed fixtures only; no Unity world or BepInEx boot.
Usage: run.py <original-game-root> <FineDining.dll> <BepInEx-core> <EpicLoot.dll> <PoVPlus.Compat.dll>
"""
import ctypes
import os
from pathlib import Path
import sys

game, mod, core, epic, compat = [Path(p).resolve() for p in sys.argv[1:]]
managed = game / "valheim_Data" / "Managed"
if not managed.is_dir():
    managed = game / "valheim_server_Data" / "Managed"
runtime = game / "MonoBleedingEdge"
runner = Path(__file__).resolve().parent / "bin" / "Debug" / "net48" / "EpicLootSmoke.exe"
search = os.add_dll_directory(str(runtime / "EmbedRuntime"))
mono = ctypes.CDLL(str(runtime / "EmbedRuntime" / "mono-2.0-bdwgc.dll"))
mono.mono_set_dirs.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
mono.mono_set_assemblies_path.argtypes = [ctypes.c_char_p]
mono.mono_jit_init_version.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
mono.mono_jit_init_version.restype = ctypes.c_void_p
mono.mono_domain_assembly_open.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
mono.mono_domain_assembly_open.restype = ctypes.c_void_p
mono.mono_jit_exec.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int, ctypes.POINTER(ctypes.c_char_p)]
mono.mono_jit_exec.restype = ctypes.c_int
mono.mono_set_dirs(str(managed).encode(), str(runtime / "etc").encode())
mono.mono_set_assemblies_path(os.pathsep.join(map(str, (managed, core, mod.parent, runner.parent))).encode())
domain = mono.mono_jit_init_version(b"FineDiningEpicLootChecks", b"v4.0.30319")
assembly = mono.mono_domain_assembly_open(domain, str(runner).encode())
if not domain or not assembly:
    raise SystemExit("Could not load the managed test runner in original Mono")
args = [str(p).encode() for p in (runner, mod, managed, core, epic, compat)]
argv = (ctypes.c_char_p * len(args))(*args)
raise SystemExit(mono.mono_jit_exec(domain, assembly, len(args), argv))
