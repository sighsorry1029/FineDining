"""Run managed integration checks in an unmodified Valheim Mono runtime.
Usage: run.py <original-game-root> <FineDining.dll> <BepInEx-core> <Seasonality.dll> <Seasons.dll> [EpicLoot.dll]
Build WinterRuntimeSmoke.csproj first. This does not start the game or a server.
"""
import ctypes
import os
from pathlib import Path
import sys

provided = [Path(x).resolve() for x in sys.argv[1:]]
game, mod, core, seasonality, seasons = provided[:5]
managed = game / "valheim_Data" / "Managed"
if not managed.is_dir():
    managed = game / "valheim_server_Data" / "Managed"
runtime = game / "MonoBleedingEdge"
runner = Path(__file__).resolve().parent / "bin" / "Debug" / "net48" / "WinterRuntimeSmoke.exe"
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
domain = mono.mono_jit_init_version(b"FineDiningWinterChecks", b"v4.0.30319")
assembly = mono.mono_domain_assembly_open(domain, str(runner).encode())
if not domain or not assembly:
    raise SystemExit("Could not load the managed test runner in original Mono")
args = [str(p).encode() for p in (runner, mod, managed, core, seasonality, seasons, *provided[5:])]
argv = (ctypes.c_char_p * len(args))(*args)
raise SystemExit(mono.mono_jit_exec(domain, assembly, len(args), argv))
