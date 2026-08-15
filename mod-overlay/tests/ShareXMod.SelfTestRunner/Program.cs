using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;

if (args.Length != 1)
    throw new ArgumentException("Usage: ShareXMod.SelfTestRunner <ShareX.ScreenCaptureLib.dll>");

string assemblyPath = Path.GetFullPath(args[0]);
if (!File.Exists(assemblyPath))
    throw new FileNotFoundException("ShareX.ScreenCaptureLib.dll not found.", assemblyPath);

string assemblyDirectory = Path.GetDirectoryName(assemblyPath)!;
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    string candidate = Path.Combine(assemblyDirectory, name.Name + ".dll");
    return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
};

Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
Type type = assembly.GetType(
    "ShareX.ScreenCaptureLib.ShareXModRecipePlannerSelfTests", throwOnError: true)!;
MethodInfo method = type.GetMethod(
    "RunOrThrow", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new MissingMethodException(type.FullName, "RunOrThrow");

try
{
    object? result = method.Invoke(null, null);
    Console.WriteLine(result?.ToString() ?? "ShareX-Mod self-tests completed.");
}
catch (TargetInvocationException ex) when (ex.InnerException != null)
{
    ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
    throw;
}
