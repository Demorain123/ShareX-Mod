using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;

namespace LongCapture.RcStressRunner;

internal static class Program
{
    private const int Repetitions = 10;
    private const long MaxManagedGrowthBytes = 64L * 1024L * 1024L;
    private const long MaxPrivateGrowthBytes = 224L * 1024L * 1024L;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: LongCapture.RcStressRunner <LongCapture.exe>");
            return 2;
        }

        string executable = Path.GetFullPath(args[0]);
        if (!File.Exists(executable))
        {
            Console.Error.WriteLine("LongCapture.exe not found: " + executable);
            return 3;
        }

        string managedAssembly = Path.ChangeExtension(executable, ".dll");
        if (!File.Exists(managedAssembly))
        {
            Console.Error.WriteLine("Managed LongCapture.dll companion not found: " + managedAssembly);
            return 4;
        }

        try
        {
            string directory = Path.GetDirectoryName(executable)!;
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                string candidate = Path.Combine(directory, name.Name + ".dll");
                return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
            };

            // The self-contained LongCapture.exe is a native Windows apphost. The workflow runs
            // that executable directly with --self-test before this stress gate. For in-process
            // repeated capture/memory measurement, load the managed LongCapture.dll shipped beside
            // the exact same executable; this exercises the same published application code while
            // allowing ten capture lifecycles to share one process.
            Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(managedAssembly);
            Type program = assembly.GetType("LongCapture.Standalone.Program", throwOnError: true)!;
            InvokeStatic(program, "InitializeDesktopUiHosts");

            Type rcSuite = assembly.GetType("LongCapture.Standalone.LongCaptureRcSelfTests", throwOnError: true)!;
            object? rcResult = InvokeStatic(rcSuite, "RunOrThrow");
            Console.WriteLine(rcResult?.ToString() ?? "LongCapture RC shell checks completed.");

            MethodInfo smoke = program.GetMethod(
                "RunScrollingCaptureSmokeTest",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException(program.FullName, "RunScrollingCaptureSmokeTest");

            long baselineManaged = 0;
            long baselinePrivate = 0;
            long maximumManaged = 0;
            long maximumPrivate = 0;

            for (int iteration = 1; iteration <= Repetitions; iteration++)
            {
                int code = Convert.ToInt32(Invoke(smoke));
                if (code != 0)
                {
                    throw new InvalidOperationException(
                        $"Published LongCapture capture smoke failed on repetition {iteration}/{Repetitions} with exit code {code}.");
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                long managed = GC.GetTotalMemory(forceFullCollection: true);
                using Process current = Process.GetCurrentProcess();
                current.Refresh();
                long privateBytes = current.PrivateMemorySize64;

                maximumManaged = Math.Max(maximumManaged, managed);
                maximumPrivate = Math.Max(maximumPrivate, privateBytes);

                Console.WriteLine(
                    $"capture repetition {iteration}/{Repetitions}: managed={FormatMiB(managed)} MiB private={FormatMiB(privateBytes)} MiB");

                // Ignore first-run JIT/Avalonia/WinForms cache initialization. The second
                // completed real capture is the stable baseline for leak detection.
                if (iteration == 2)
                {
                    baselineManaged = managed;
                    baselinePrivate = privateBytes;
                    maximumManaged = managed;
                    maximumPrivate = privateBytes;
                }
            }

            if (baselineManaged <= 0 || baselinePrivate <= 0)
                throw new InvalidOperationException("RC stress runner did not establish a post-warmup memory baseline.");

            long managedGrowth = Math.Max(0, maximumManaged - baselineManaged);
            long privateGrowth = Math.Max(0, maximumPrivate - baselinePrivate);

            Console.WriteLine(
                $"RC stress summary: runs={Repetitions}, managedGrowth={FormatMiB(managedGrowth)} MiB, privateGrowth={FormatMiB(privateGrowth)} MiB");

            if (managedGrowth > MaxManagedGrowthBytes)
            {
                throw new InvalidOperationException(
                    $"Managed memory grew {FormatMiB(managedGrowth)} MiB after warmup; RC limit is {FormatMiB(MaxManagedGrowthBytes)} MiB.");
            }

            if (privateGrowth > MaxPrivateGrowthBytes)
            {
                throw new InvalidOperationException(
                    $"Private memory grew {FormatMiB(privateGrowth)} MiB after warmup; RC limit is {FormatMiB(MaxPrivateGrowthBytes)} MiB.");
            }

            Console.WriteLine("LongCapture RC repeated-run, failure-recovery and memory gates passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static object? InvokeStatic(Type type, string name)
    {
        MethodInfo method = type.GetMethod(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(type.FullName, name);
        return Invoke(method);
    }

    private static object? Invoke(MethodInfo method)
    {
        try
        {
            return method.Invoke(null, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static string FormatMiB(long bytes) => (bytes / (1024d * 1024d)).ToString("F1");
}
