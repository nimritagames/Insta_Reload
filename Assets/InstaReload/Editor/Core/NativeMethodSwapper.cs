/*
 * ============================================================================
 * INSTARELOAD - NATIVE METHOD SWAPPER
 * ============================================================================
 *
 * PURPOSE:
 *   Swaps methods at the NATIVE ASSEMBLY level using direct memory manipulation.
 *   This is the technique used by commercial hot reload tools!
 *
 * THE BREAKTHROUGH:
 *   Instead of using MonoMod's IL manipulation (which fails when calling new methods),
 *   we write a JMP instruction directly into the native machine code!
 *
 * HOW IT WORKS:
 *
 *   STEP 1: Force JIT Compilation
 *   - Use RuntimeHelpers.PrepareMethod() to JIT-compile both methods
 *   - This converts IL to native x86/x64 assembly code
 *
 *   STEP 2: Get Function Pointers
 *   - Use MethodHandle.GetFunctionPointer() to get native code addresses
 *   - These are actual memory addresses in the process space
 *
 *   STEP 3: Make Memory Writable
 *   - Use VirtualProtect() to change memory permissions (normally code is read-only)
 *   - PAGE_EXECUTE_READWRITE allows us to modify the code
 *
 *   STEP 4: Write JMP Instruction
 *   - Write x64 JMP instruction directly to original method's address
 *   - JMP format: E9 [4-byte relative offset]
 *   - Offset = (target - source - 5)
 *
 *   STEP 5: Restore Memory Protection
 *   - Set memory back to PAGE_EXECUTE_READ for security
 *
 * EXAMPLE:
 *
 *   Original Update() at 0x12345678:
 *   Before:
 *     push rbp
 *     mov rbp, rsp
 *     ... (original code)
 *
 *   After:
 *     E9 [offset]     ← JMP to hot version
 *     ... (rest overwritten/ignored)
 *
 *   Hot Update() at 0x87654321:
 *     call Log()      ← Works! Same assembly
 *     call LogOne()   ← Works! Same assembly
 *     ret
 *
 * WHY THIS SOLVES OUR PROBLEM:
 *
 *   MonoMod's issue:
 *   - Tries to import LogOne() reference into Assembly-CSharp module
 *   - LogOne() doesn't exist there → FAILS
 *
 *   Native JMP solution:
 *   - Original Update() just jumps to hot assembly
 *   - Hot Update() calls LogOne() within same assembly → SUCCESS!
 *   - No cross-assembly reference resolution needed!
 *
 * PLATFORM NOTES:
 *   - Currently supports x64 (5-byte near JMP)
 *   - Unity Editor is 64-bit on modern systems
 *   - IL2CPP builds won't work (AOT compiled, no runtime JIT)
 *   - Mono backend required (which Unity Editor uses)
 *
 * SAFETY:
 *   - Only works in Mono/JIT environments
 *   - Requires unsafe code and platform invoke
 *   - Memory corruption possible if offsets wrong
 *   - Should only be used in Editor, never in builds
 *
 * REFERENCES:
 *   - https://github.com/zapu/UnityHotSwap
 *   - https://tryfinally.dev/detours-redirecting-csharp-methods-at-runtime
 *
 * ============================================================================
 */

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nimrita.InstaReload.Editor
{
    internal static class NativeMethodSwapper
    {
        #region P/Invoke Declarations

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(
            IntPtr lpAddress,
            UIntPtr dwSize,
            uint flNewProtect,
            out uint lpflOldProtect);

        // Memory protection constants
        private const uint PAGE_EXECUTE_READWRITE = 0x40;
        private const uint PAGE_EXECUTE_READ = 0x20;

        #endregion

        #region Public API

        /// <summary>
        /// Swaps the original method to jump to the hot method at the native assembly level.
        /// </summary>
        /// <param name="original">The original method to redirect</param>
        /// <param name="hot">The hot method to redirect to</param>
        /// <returns>True if swap succeeded, false otherwise</returns>
        public static bool TrySwapMethod(MethodBase original, MethodBase hot)
        {
            try
            {
                // Validate inputs
                if (original == null || hot == null)
                {
                    InstaReloadLogger.LogError("[NativeSwapper] Cannot swap null methods");
                    return false;
                }

                // Check if we're on a supported platform
                if (!Environment.Is64BitProcess)
                {
                    InstaReloadLogger.LogError("[NativeSwapper] Only 64-bit processes supported");
                    return false;
                }

                // STEP 1: Force JIT compilation of both methods
                InstaReloadLogger.LogVerbose($"[NativeSwapper] Preparing methods for swap: {original.Name}");

                RuntimeHelpers.PrepareMethod(original.MethodHandle);
                RuntimeHelpers.PrepareMethod(hot.MethodHandle);

                // STEP 2: Get function pointers
                IntPtr originalAddr = original.MethodHandle.GetFunctionPointer();
                IntPtr hotAddr = hot.MethodHandle.GetFunctionPointer();

                InstaReloadLogger.LogVerbose($"[NativeSwapper] Original: 0x{originalAddr.ToInt64():X}, Hot: 0x{hotAddr.ToInt64():X}");

                // STEP 3 & 4: Write JMP instruction
                if (!WriteJmpInstruction(originalAddr, hotAddr))
                {
                    InstaReloadLogger.LogError("[NativeSwapper] Failed to write JMP instruction");
                    return false;
                }

                InstaReloadLogger.LogVerbose($"[NativeSwapper] ✓ Successfully swapped {original.DeclaringType?.Name}::{original.Name}");
                return true;
            }
            catch (Exception ex)
            {
                InstaReloadLogger.LogError($"[NativeSwapper] Swap failed: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Private Implementation

        /// <summary>
        /// Writes a JMP instruction to redirect from source to target address.
        /// x64 format: E9 [4-byte relative offset]
        /// </summary>
        private static bool WriteJmpInstruction(IntPtr sourceAddr, IntPtr targetAddr)
        {
            try
            {
                // Calculate relative offset
                // JMP instruction is 5 bytes: E9 [offset]
                // Offset is relative to the NEXT instruction (source + 5)
                long offset = targetAddr.ToInt64() - sourceAddr.ToInt64() - 5;

                // Check if offset fits in 32-bit signed integer (±2GB range)
                if (offset > int.MaxValue || offset < int.MinValue)
                {
                    InstaReloadLogger.LogError($"[NativeSwapper] Jump offset too large: {offset:X}");
                    return false;
                }

                // STEP 3: Make memory writable
                const int JMP_SIZE = 5;
                uint oldProtection;
                if (!VirtualProtect(sourceAddr, new UIntPtr(JMP_SIZE), PAGE_EXECUTE_READWRITE, out oldProtection))
                {
                    InstaReloadLogger.LogError($"[NativeSwapper] VirtualProtect failed: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                try
                {
                    // STEP 4: Write JMP instruction
                    unsafe
                    {
                        byte* ptr = (byte*)sourceAddr.ToPointer();

                        // Write JMP opcode (E9)
                        *ptr = 0xE9;

                        // Write 4-byte relative offset (little-endian)
                        *(int*)(ptr + 1) = (int)offset;
                    }

                    InstaReloadLogger.LogVerbose($"[NativeSwapper] Wrote JMP instruction with offset: {offset:X}");
                }
                finally
                {
                    // STEP 5: Restore original memory protection
                    VirtualProtect(sourceAddr, new UIntPtr(JMP_SIZE), oldProtection, out _);
                }

                return true;
            }
            catch (Exception ex)
            {
                InstaReloadLogger.LogError($"[NativeSwapper] Failed to write JMP: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Debug Utilities

        /// <summary>
        /// Dumps the first N bytes of native code at the given address (for debugging).
        /// </summary>
        public static void DumpNativeCode(IntPtr address, int byteCount = 16)
        {
            try
            {
                unsafe
                {
                    byte* ptr = (byte*)address.ToPointer();
                    var bytes = new System.Text.StringBuilder();

                    for (int i = 0; i < byteCount; i++)
                    {
                        bytes.AppendFormat("{0:X2} ", ptr[i]);
                    }

                    InstaReloadLogger.LogVerbose($"[NativeSwapper] Memory at 0x{address.ToInt64():X}: {bytes}");
                }
            }
            catch (Exception ex)
            {
                InstaReloadLogger.LogError($"[NativeSwapper] Failed to dump memory: {ex.Message}");
            }
        }

        #endregion
    }
}
