#if NETSTANDARD2_0
namespace System.Runtime.CompilerServices;
internal class IsExternalInit;
#else
[assembly: global::System.Runtime.CompilerServices.TypeForwardedTo(
    typeof(global::System.Runtime.CompilerServices.IsExternalInit))]
#endif
