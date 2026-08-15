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
string[] suites =
{
    "ShareX.ScreenCaptureLib.ShareXModRecipePlannerSelfTests",
    "ShareX.ScreenCaptureLib.ShareXModRecipeAnchorSelfTests",
    "ShareX.ScreenCaptureLib.ShareXModCaptureIntegritySelfTests",
    "ShareX.ScreenCaptureLib.ShareXModTemplateRouterEvidenceSelfTests",
    "ShareX.ScreenCaptureLib.ShareXModPaginationIntentSelfTests"
};

foreach (string suite in suites)
{
    Type type = assembly.GetType(suite, throwOnError: true)!;
    MethodInfo method = type.GetMethod(
        "RunOrThrow", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(type.FullName, "RunOrThrow");

    try
    {
        object? result = method.Invoke(null, null);
        Console.WriteLine(result?.ToString() ?? suite + " completed.");
    }
    catch (TargetInvocationException ex) when (ex.InnerException != null)
    {
        ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        throw;
    }
}
