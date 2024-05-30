using Silk.NET.Core.Native;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace LemonPlayer.Windows
{
    public static class FuncExt
    {
        [DebuggerStepThrough]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe static bool IsEmpty<T>(this ComPtr<T> obj) where T : unmanaged, IComVtbl<T>
            => obj.Handle == null;

        [DebuggerStepThrough]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe static void ThrowIfFailure(this HResult hr)
            => SilkMarshal.ThrowHResult(hr);
    }
}
