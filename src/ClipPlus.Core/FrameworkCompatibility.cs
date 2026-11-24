#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    // Required by the C# compiler for init-only properties on .NET Framework.
    internal static class IsExternalInit { }
}
#endif
