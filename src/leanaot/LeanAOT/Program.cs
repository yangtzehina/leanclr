using System.Linq;
using System.Text;
using dnlib.DotNet;
using LeanAOT.GenerationPlan;
using LeanAOT.ToCpp;
using NLog;
using CommandLine;
using CommandLine.Text;

namespace LeanAOT;

internal class Program
{
    /// <summary>
    /// LeanAOT native CLI: -d searchDir -a AssemblyName -o outDir (assembly names without .dll).
    /// IL2CPP-compatible: --convert-to-cpp --assembly=path/to.dll ... --generatedcppdir=... [--symbols-folder=...] ...
    /// </summary>
    private sealed class CliOptions
    {
        [Option('d', Required = false, HelpText = "DLL search directory. Can be specified multiple times. Not required if every --assembly is a full path to a .dll.")]
        public IEnumerable<string> DllSearchPaths { get; set; }

        [Option('a', "assembly", Required = false, HelpText = "Assembly to AOT: short name (with -d) or full path to .dll (IL2CPP style). Can be specified multiple times.")]
        public IEnumerable<string> Assemblies { get; set; }

        [Option('o', "output", Required = false, HelpText = "Output directory for generated C++ code (LeanAOT short form).")]
        public string OutputCodeDir { get; set; }

        [Option("generatedcppdir", Required = false, HelpText = "Output directory for generated C++ code (IL2CPP name, same as -o).")]
        public string GeneratedCppDir { get; set; }

        [Option("convert-to-cpp", Required = false, HelpText = "IL2CPP compatibility: request managed-to-C++ conversion (informational).")]
        public bool ConvertToCpp { get; set; }

        [Option("symbols-folder", Required = false, HelpText = "IL2CPP: symbols output folder (reserved).")]
        public string SymbolsFolder { get; set; }

        [Option("data-folder", Required = false, HelpText = "IL2CPP: data folder (reserved).")]
        public string DataFolder { get; set; }

        [Option("enable-analytics", Required = false, HelpText = "IL2CPP compatibility flag (reserved).")]
        public bool EnableAnalytics { get; set; }

        [Option("emit-null-checks", Required = false, HelpText = "IL2CPP compatibility flag (reserved).")]
        public bool EmitNullChecks { get; set; }

        [Option("enable-array-bounds-check", Required = false, HelpText = "IL2CPP compatibility flag (reserved).")]
        public bool EnableArrayBoundsCheck { get; set; }

        [Option("emit-method-map", Required = false, HelpText = "IL2CPP compatibility flag (reserved).")]
        public bool EmitMethodMap { get; set; }

        [Option("emit-source-mapping", Required = false, HelpText = "IL2CPP compatibility: emit managed-to-generated-C++ source mapping (reserved for codegen).")]
        public bool EmitSourceMapping { get; set; }

        /// <summary>
        /// tuanjie only.
        /// </summary>
        [Option("use-slim-meta-file-format", Required = false, HelpText = "IL2CPP compatibility flag (reserved).")]
        public bool UseSlimMetaFileFormat { get; set; }

        [Option("generics-option", Required = false, HelpText = "IL2CPP generics option (reserved), e.g. None,EnableFullSharing.")]
        public string GenericsOption { get; set; }

        [Option("dotnetprofile", Required = false, HelpText = "IL2CPP: e.g. unityaot-linux (reserved).")]
        public string DotnetProfile { get; set; }

        [Option("incremental-g-c-time-slice", Required = false, HelpText = "IL2CPP: incremental GC time slice (reserved).")]
        public int? IncrementalGCTimeSlice { get; set; }

        [Option("profiler-report", Required = false, HelpText = "IL2CPP compatibility flag (reserved).")]
        public bool ProfilerReport { get; set; }

        [Option("profiler-output-file", Required = false, HelpText = "IL2CPP: profiler trace output path (reserved).")]
        public string ProfilerOutputFile { get; set; }

        [Option("print-command-line", Required = false, HelpText = "Print the effective command line to stdout.")]
        public bool PrintCommandLine { get; set; }

        [Option("compiler-flags", Required = false, HelpText = "IL2CPP: C++ compiler flags (e.g. -fno-exceptions). Repeatable; stored in config, LeanAOT does not compile C++.")]
        public IEnumerable<string> CompilerFlags { get; set; }

        [Option("usymtool-path", Required = false, HelpText = "IL2CPP: usym tool path (reserved).")]
        public string UsymToolPath { get; set; }

        [Option("static-lib-il2-cpp", Required = false, HelpText = "IL2CPP: static lib il2cpp path (reserved).")]
        public bool StaticLibIl2CppPath { get; set; }

        [Option("map-file-parser", Required = false, HelpText = "IL2CPP: map file parser executable path (reserved).")]
        public string MapFileParser { get; set; }

        [Option("directory", Required = false, HelpText = "IL2CPP: managed assemblies directory. Used to auto-discover assemblies when --assembly is omitted.")]
        public string Directory { get; set; }

        [Option("baselib-directory", Required = false, HelpText = "IL2CPP: baselib directory path (reserved).")]
        public string BaseLibDirectory { get; set; }

        [Option("avoid-dynamic-library-copy", Required = false, HelpText = "IL2CPP compatibility flag (reserved).")]
        public bool AvoidDynamicLibraryCopy { get; set; }

        [Option("stats-output-dir", Required = false, HelpText = "IL2CPP: stats output directory (reserved).")]
        public string StatsOutputDir { get; set; }

        /// <summary>
        /// LeanAOT-only: <c>aot.xml</c> rule paths (repeat option for multiple files). See docs/aot-rule-file-design.md.
        /// </summary>
        [Option("leanaot-aot-rule-file", Required = false, HelpText = "LeanAOT-only: path to an aot.xml rule file (repeat for multiple files).")]
        public IEnumerable<string> AotMethodRuleFiles { get; set; }

        /// <summary>
        /// LeanAOT-only: PGO-generated exact include lists (repeat for multiple files).
        /// </summary>
        [Option("leanaot-pgo-rule-file", Required = false, HelpText = "LeanAOT-only: path to a PGO rule file with exact method signatures (repeat for multiple files).")]
        public IEnumerable<string> PgoRuleFiles { get; set; }

        /// <summary>
        /// LeanAOT-only: assembly short names (same as <c>-a</c> / <c>--assembly</c>, without .dll) to omit from <c>global-metadata.dat</c> only.
        /// </summary>
        [Option("leanaot-exclude-assembly-from-global-metadata", Required = false, HelpText = "LeanAOT-only: assembly short name to omit from global-metadata.dat only (repeat for multiple); must appear in -a/--assembly list.")]
        public IEnumerable<string> AssembliesExcludedFromGlobalMetadata { get; set; }

        [Option("leanaot-generic-list", Required = false, HelpText = "LeanAOT-only: closed generic instantiations to compile besides the static closure, one name key per line (repeatable).")]
        public IEnumerable<string> GenericListFiles { get; set; }

        [Option("leanaot-generic-exclude", Required = false, HelpText = "LeanAOT-only: leave generic instantiations whose method full name contains this string interpreted (repeatable).")]
        public IEnumerable<string> GenericExcludes { get; set; }

        [Option("leanaot-no-generics", Required = false, HelpText = "LeanAOT-only: do not compile generic instantiations.")]
        public bool NoGenerics { get; set; }

        [Option("leanaot-no-gc-roots", Required = false, HelpText = "LeanAOT-only: keep object references only in C++ locals, without the per-method GC root slots (default: keep them). Valid because the runtime collects only between calls (GC.Collect inside a call is a request for the next collection).")]
        public bool NoGcRoots { get; set; }

        [Option("leanaot-no-exception-handlers", Required = false, HelpText = "LeanAOT-only: leave every method with try/catch/finally/fault interpreted (default: compile them; filter clauses always stay interpreted).")]
        public bool NoExceptionHandlers { get; set; }

        [Option("leanaot-generic-keys-to-names", Required = false, HelpText = "LeanAOT-only: convert runtime keys (one per line, file IN) to name keys (file OUT): IN:OUT. Needs -d only.")]
        public string GenericKeysToNames { get; set; }

        [Option("leanaot-enable-layout-validation", Required = false, HelpText = "LeanAOT-only: enable managed type layout validation in codegen (default off).")]
        public bool LeanAotEnableLayoutValidation { get; set; }

        [Option("leanaot-enable-pgo-profile", Required = false, HelpText = "LeanAOT-only: emit PGO profile instrumentation in generated code.")]
        public bool LeanAotEnablePgoProfile { get; set; }

        [Option("leanaot-may-throw-exception-in-icall", Required = false, HelpText = "LeanAOT-only: enable may throw exception in icall (default off).")]
        public bool LeanAotMayThrowExceptionInIcall { get; set; }

        [Option("leanaot-runtime-api-profile", Required = false, HelpText = "LeanAOT-only: runtime API profile to load. Defaults to LEANCLR_RUNTIME_API_PROFILE or coreclr-net10.")]
        public string LeanAotRuntimeApiProfile { get; set; }

        [Option("leanaot-unity-version", Required = false, HelpText = "LeanAOT-only: Unity editor version string (e.g. 6000.0.4f1).")]
        public string LeanAotUnityVersion { get; set; }

        /// <summary>
        /// LeanAOT-only: flat copy of managed assemblies (by assembly file name) for stripped duplicate staging.
        /// </summary>
        [Option("leanaot-managed-stripped-duplicate-path", Required = false, HelpText = "LeanAOT-only: copy -a/--assembly DLLs into this directory using only the assembly file name (ignore source path).")]
        public string LeanAotManagedStrippedDuplicatePath { get; set; }
    }

    private static Logger s_logger;

    private static void SetupApp()
    {
        ConsoleUtil.EnableQuickEditMode(false);
        Console.OutputEncoding = Encoding.UTF8;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        int processorCount = Environment.ProcessorCount;
        ThreadPool.SetMinThreads(Math.Max(4, processorCount), 0);
        ThreadPool.SetMaxThreads(Math.Max(16, processorCount * 2), 2);

        var nlogConfigPath = Path.Combine(AppContext.BaseDirectory, "nlog.xml");
        LogManager.Setup().LoadConfigurationFromFile(nlogConfigPath);
        s_logger = LogManager.GetCurrentClassLogger();
    }

    static void Main(string[] args)
    {
        SetupApp();

        int exitCode = 0;
        var helpWriter = new StringWriter();
        var parser = new Parser(settings =>
        {
            settings.AllowMultiInstance = true;
            settings.CaseInsensitiveEnumValues = true;
            settings.HelpWriter = helpWriter;
            settings.IgnoreUnknownArguments = true;
        });
        string[] effectiveArgs;
        try
        {
            var primary = GetEffectiveCommandLineArgs(args ?? Array.Empty<string>());
            effectiveArgs = MergeLeanAotExtraArgsFromEnvironment(primary);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.Exit(1);
            return;
        }

        var normalizedArgs = NormalizeSingleDashLongOptions(effectiveArgs);
        var parseResult = parser.ParseArguments<CliOptions>(normalizedArgs);
        if (parseResult.Tag == ParserResultType.NotParsed)
        {
            Console.Error.WriteLine(helpWriter.ToString());
            Environment.Exit(1);
        }
        parseResult
            .WithParsed(options =>
            {
                if (options.PrintCommandLine)
                {
                    Console.WriteLine(string.Join(" ", effectiveArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
                }

                if (!string.IsNullOrWhiteSpace(options.GenericKeysToNames))
                {
                    exitCode = ConvertGenericKeys(options);
                    return;
                }

                if (!TryNormalizeCli(options, out var dllSearchPaths, out var aotAssemblyNames, out var outputCodeDir, out var errorMessage))
                {
                    s_logger.Error(errorMessage);
                    exitCode = 1;
                    return;
                }

                try
                {
                    var runtimeApiCatalog = LoadRuntimeApiCatalogFromCurrentDirectory(options.LeanAotRuntimeApiProfile);
                    Run(dllSearchPaths, aotAssemblyNames, outputCodeDir, options, runtimeApiCatalog);
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException)
                {
                    s_logger.Error(ex, "Failed to load runtime API catalog.");
                    exitCode = 1;
                }
                catch (AotRuleFileException ex)
                {
                    s_logger.Error(ex, ex.Message);
                    exitCode = 1;
                }
                catch (AotRuleConflictException ex)
                {
                    s_logger.Error(ex, ex.Message);
                    exitCode = 1;
                }
            });
        Environment.ExitCode = exitCode;
    }

    private static RuntimeApiCatalog LoadRuntimeApiCatalogFromCurrentDirectory(string requestedProfile)
    {
        var baseDir = AppContext.BaseDirectory;
        var profile = string.IsNullOrWhiteSpace(requestedProfile)
            ? Environment.GetEnvironmentVariable("LEANCLR_RUNTIME_API_PROFILE")
            : requestedProfile;
        var catalog = RuntimeApiCatalog.LoadFromDirectory(baseDir, string.IsNullOrWhiteSpace(profile) ? "coreclr-net10" : profile.Trim());
        s_logger.Info(
            "Loaded runtime API profile {0} from {1}: core_modules={2}, icalls={3}, intrinsics={4}, icalls_newobj={5}, intrinsics_newobj={6}, static_linked_pinvoke_dlls={7}, static_linked_pinvoke_methods={8}",
            catalog.ProfileName,
            catalog.SourceDirectory,
            catalog.CoreLibraryModuleCount,
            catalog.IcallCount,
            catalog.IntrinsicCount,
            catalog.IcallNewobjCount,
            catalog.IntrinsicNewobjCount,
            catalog.StaticLinkedPInvokeDllCount,
            catalog.StaticLinkedPInvokeMethodCount);
        return catalog;
    }

    private static bool TryNormalizeCli(
        CliOptions options,
        out List<string> dllSearchPaths,
        out List<string> aotAssemblyNames,
        out string outputCodeDir,
        out string errorMessage)
    {
        dllSearchPaths = new List<string>();
        aotAssemblyNames = new List<string>();
        outputCodeDir = null;
        errorMessage = null;

        var rawAssemblies = options.Assemblies?.ToList() ?? new List<string>();
        if (rawAssemblies.Count == 0 && !string.IsNullOrWhiteSpace(options.Directory))
        {
            var managedDir = Path.GetFullPath(options.Directory.Trim());
            if (!System.IO.Directory.Exists(managedDir))
            {
                errorMessage = $"Managed directory not found: {managedDir}";
                return false;
            }

            AddUniquePath(dllSearchPaths, managedDir);
            rawAssemblies.AddRange(System.IO.Directory.EnumerateFiles(managedDir, "*.dll", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName));
        }

        if (rawAssemblies.Count == 0)
        {
            errorMessage = "Missing required input: specify at least one assembly (-a / --assembly), or use --directory to auto-discover DLLs.";
            return false;
        }

        foreach (var d in options.DllSearchPaths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(d))
                continue;
            AddUniquePath(dllSearchPaths, Path.GetFullPath(d));
        }

        foreach (var raw in rawAssemblies)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var trimmed = raw.Trim();
            var name = GetAssemblyShortName(trimmed);
            if (!aotAssemblyNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                aotAssemblyNames.Add(name);
            var dir = Path.GetDirectoryName(trimmed);
            if (!string.IsNullOrEmpty(dir) && !dllSearchPaths.Contains(dir, StringComparer.OrdinalIgnoreCase))
                dllSearchPaths.Add(dir);
        }

        if (dllSearchPaths.Count == 0)
        {
            errorMessage =
                "No DLL search path: use -d for each search directory, or pass full paths with --assembly=path/to.dll (IL2CPP style).";
            return false;
        }

        outputCodeDir = !string.IsNullOrWhiteSpace(options.GeneratedCppDir)
            ? options.GeneratedCppDir.Trim()
            : options.OutputCodeDir?.Trim();
        if (string.IsNullOrEmpty(outputCodeDir))
        {
            errorMessage = "Missing output directory: use -o / --output or --generatedcppdir (IL2CPP).";
            return false;
        }

        foreach (var rawRule in options.AotMethodRuleFiles ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(rawRule))
                continue;
            var rulePath = Path.GetFullPath(rawRule.Trim());
            if (!File.Exists(rulePath))
            {
                errorMessage = $"AOT rule file not found: {rulePath}";
                return false;
            }
        }

        foreach (var rawRule in options.PgoRuleFiles ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(rawRule))
                continue;
            var rulePath = Path.GetFullPath(rawRule.Trim());
            if (!File.Exists(rulePath))
            {
                errorMessage = $"PGO rule file not found: {rulePath}";
                return false;
            }
        }

        foreach (var excludedAssName in options.AssembliesExcludedFromGlobalMetadata ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(excludedAssName))
                continue;
            if (!aotAssemblyNames.Contains(excludedAssName, StringComparer.OrdinalIgnoreCase))
            {
                errorMessage =
                    $"Assembly '{excludedAssName}' is not in the AOT assembly list; --leanaot-exclude-assembly-from-global-metadata only applies to assemblies passed via -a/--assembly (or discovered with --directory).";
                return false;
            }
        }

        return true;
    }

    private static string GetAssemblyShortName(string assemblyInput)
    {
        var fileName = Path.GetFileName(assemblyInput);
        if (fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileNameWithoutExtension(fileName);
        }

        return fileName;
    }

    /// <summary>
    /// Appends tokens from <c>LEANAOT_EXTRA_ARGS</c> after Unity argv / rsp.
    /// Same token rules as Unity rsp: whitespace split, double quotes for spaces.
    /// </summary>
    private static string[] MergeLeanAotExtraArgsFromEnvironment(string[] primaryArgs)
    {
        var extraRaw = Environment.GetEnvironmentVariable("LEANAOT_EXTRA_ARGS");
        if (string.IsNullOrWhiteSpace(extraRaw))
            return primaryArgs ?? Array.Empty<string>();

        var line = extraRaw.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ").Trim();
        if (line.Length == 0)
            return primaryArgs ?? Array.Empty<string>();

        var extraTokens = TokenizeUnityRspSingleLine(line).Select(NormalizeUnityRspArgument).Where(t => t.Length > 0).ToArray();
        if (extraTokens.Length == 0)
            return primaryArgs ?? Array.Empty<string>();

        if (primaryArgs == null || primaryArgs.Length == 0)
            return extraTokens;

        var merged = new string[primaryArgs.Length + extraTokens.Length];
        primaryArgs.CopyTo(merged, 0);
        extraTokens.CopyTo(merged, primaryArgs.Length);
        return merged;
    }

    private static string[] GetEffectiveCommandLineArgs(string[] args)
    {
        var list = new List<string>();
        foreach (var a in args)
        {
            if (string.IsNullOrWhiteSpace(a))
                continue;
            list.Add(UnwrapOuterQuotes(a.Trim()));
        }

        if (list.Count == 1 && list[0].Length > 0 && list[0][0] == '@')
        {
            var rspPath = UnwrapOuterQuotes(list[0].Substring(1).Trim());
            if (string.IsNullOrEmpty(rspPath))
                return list.ToArray();

            var fullPath = Path.GetFullPath(rspPath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"Response file not found: {fullPath}");

            return ReadUnityRspFile(fullPath);
        }

        return list.ToArray();
    }

    private static string UnwrapOuterQuotes(string token)
    {
        if (string.IsNullOrEmpty(token))
            return token;
        var t = token.Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')
            return t[1..^1].Trim();
        return t;
    }

    private static string[] ReadUnityRspFile(string fullPath)
    {
        var raw = File.ReadAllText(fullPath).Trim();
        if (raw.Length == 0)
            return Array.Empty<string>();

        // 逻辑单行：换行视为空格（Unity 语义上只有一行）
        var line = raw.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ").Trim();
        if (line.Length == 0)
            return Array.Empty<string>();
        if (line[0] == '#')
            return Array.Empty<string>();

        return TokenizeUnityRspSingleLine(line).Select(NormalizeUnityRspArgument).ToArray();
    }

    private static List<string> TokenizeUnityRspSingleLine(string line)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i]))
                i++;
            if (i >= line.Length)
                break;

            if (line[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    sb.Append(line[i]);
                    i++;
                }

                tokens.Add(sb.ToString());
            }
            else
            {
                var start = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i]))
                    i++;
                tokens.Add(line.Substring(start, i - start));
            }
        }

        return tokens;
    }

    private static string NormalizeUnityRspArgument(string token)
    {
        var eq = token.IndexOf('=');
        if (eq < 0)
            return token;

        var prefix = token.Substring(0, eq + 1);
        var valueStr = token.Substring(eq + 1).Trim();
        valueStr = StripUnityQuotedValue(valueStr);
        return prefix + valueStr;
    }

    private static string StripUnityQuotedValue(string value)
    {
        var v = value.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
            return v[1..^1];
        return v;
    }

    private static string[] NormalizeSingleDashLongOptions(IEnumerable<string> args)
    {
        return args.Select(a =>
        {
            if (string.IsNullOrEmpty(a) || !a.StartsWith("-") || a.StartsWith("--"))
                return a;
            if (a.Length <= 2)
                return a;

            var eqIndex = a.IndexOf('=');
            var optionName = eqIndex >= 0 ? a.Substring(1, eqIndex - 1) : a.Substring(1);
            if (optionName.Length > 1 && optionName.Contains('-'))
                return "--" + a.Substring(1);
            return a;
        }).ToArray();
    }

    private static void AddUniquePath(List<string> list, string path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        if (!list.Contains(path, StringComparer.OrdinalIgnoreCase))
            list.Add(path);
    }

    private static void ApplyIl2CppOptionsToGlobalConfig(GlobalConfig config, CliOptions options)
    {
        config.ConvertToCpp = options.ConvertToCpp;
        config.EnableAnalytics = options.EnableAnalytics;
        config.EmitNullChecks = options.EmitNullChecks;
        config.EnableArrayBoundsCheck = options.EnableArrayBoundsCheck;
        config.EmitMethodMap = options.EmitMethodMap;
        config.EmitSourceMapping = options.EmitSourceMapping;
        config.UseSlimMetaFileFormat = options.UseSlimMetaFileFormat;
        config.GenericsOption = options.GenericsOption;
        config.DotnetProfile = options.DotnetProfile;
        config.IncrementalGCTimeSlice = options.IncrementalGCTimeSlice;
        config.ProfilerReport = options.ProfilerReport;
        config.ProfilerOutputFile = options.ProfilerOutputFile;
        config.PrintCommandLine = options.PrintCommandLine;
        config.SymbolsFolder = options.SymbolsFolder ?? (!string.IsNullOrWhiteSpace(options.GeneratedCppDir) ? Path.Combine(options.GeneratedCppDir, "Symbols") : null);
        // in unity 2019 and 2021, the data folder is under generated cpp dir.
        config.DataFolder = options.DataFolder ?? (!string.IsNullOrWhiteSpace(options.GeneratedCppDir) ? Path.Combine(options.GeneratedCppDir, "Data") : null);
        config.MapFileParser = options.MapFileParser;
        config.BaseLibDirectory = options.BaseLibDirectory;
        config.AvoidDynamicLibraryCopy = options.AvoidDynamicLibraryCopy;
        config.StatsOutputDir = options.StatsOutputDir;
        config.CompilerFlags = NormalizeCompilerFlags(options.CompilerFlags);
        config.EnableLayoutValidation = options.LeanAotEnableLayoutValidation;
        config.EnableProfileInstrumentation = options.LeanAotEnablePgoProfile;
        config.OmitGcRoots = options.NoGcRoots;
        if (options.LeanAotEnableLayoutValidation)
        {
            s_logger.Info("LeanAOT layout validation enabled (--leanaot-enable-layout-validation).");
        }
        if (options.LeanAotEnablePgoProfile)
        {
            s_logger.Info("LeanAOT profile instrumentation enabled (--leanaot-enable-pgo-profile).");
        }
        config.MayThrowExceptionInIcall = options.LeanAotMayThrowExceptionInIcall;
        config.UnityVersion = string.IsNullOrWhiteSpace(options.LeanAotUnityVersion)
            ? null
            : options.LeanAotUnityVersion.Trim();

        config.AotMethodRuleFiles = (options.AotMethodRuleFiles ?? Enumerable.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => Path.GetFullPath(s.Trim()))
            .ToList();

        if (config.AotMethodRuleFiles is { Count: > 0 })
        {
            foreach (var rulePath in config.AotMethodRuleFiles)
                s_logger.Info("LeanAOT AOT rule file: {0}", rulePath);
        }

        config.PgoRuleFiles = (options.PgoRuleFiles ?? Enumerable.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => Path.GetFullPath(s.Trim()))
            .ToList();

        if (config.PgoRuleFiles is { Count: > 0 })
        {
            foreach (var rulePath in config.PgoRuleFiles)
                s_logger.Info("LeanAOT PGO rule file: {0}", rulePath);
        }

        config.AssembliesExcludedFromGlobalMetadata = new List<string>();
        foreach (var raw in options.AssembliesExcludedFromGlobalMetadata ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var shortName = Path.GetFileName(raw.Trim());
            if (config.AssembliesExcludedFromGlobalMetadata.Contains(shortName, StringComparer.OrdinalIgnoreCase))
                continue;
            config.AssembliesExcludedFromGlobalMetadata.Add(shortName);
        }

        if (config.AssembliesExcludedFromGlobalMetadata.Count > 0)
            s_logger.Info("LeanAOT assemblies excluded from global-metadata.dat: {0}", string.Join(", ", config.AssembliesExcludedFromGlobalMetadata));

        config.ManagedStrippedDuplicatePath = string.IsNullOrWhiteSpace(options.LeanAotManagedStrippedDuplicatePath)
            ? null
            : options.LeanAotManagedStrippedDuplicatePath.Trim();
    }

    private static string NormalizeCompilerFlags(IEnumerable<string> rawParts)
    {
        if (rawParts == null)
            return null;
        var tokens = new List<string>();
        foreach (var raw in rawParts)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var t = raw.Trim().Replace("\r", string.Empty).Trim();
            if (t.Length > 0)
                tokens.Add(t);
        }
        return tokens.Count == 0 ? null : string.Join(" ", tokens);
    }

    private static void EnsureEmptyProfilerOutputFile(GlobalConfig config)
    {
        var path = config.ProfilerOutputFile?.Trim();
        if (string.IsNullOrEmpty(path))
            return;

        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(full, string.Empty);
        s_logger.Info("Created empty profiler output file: {0}", full);
    }

    private static void Run(List<string> dllSearchPaths, List<string> aotAssemblyNames, string outputCodeDir, CliOptions il2CppOptions,
                            RuntimeApiCatalog runtimeApiCatalog)
    {
        var generator = new CppGenerator();
        var assemblyCache = new Core.AssemblyCache(new Core.MultiDirectoryAssemblyResolver(dllSearchPaths));
        Core.MetaUtil.CompileExceptionHandlers = !il2CppOptions.NoExceptionHandlers;

        var rulePaths = (il2CppOptions.AotMethodRuleFiles ?? Enumerable.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => Path.GetFullPath(s.Trim()))
            .ToList();
        AotMethodRulesEvaluator aotRules = null;
        if (rulePaths.Count > 0)
            aotRules = new AotMethodRulesEvaluator(rulePaths, assemblyCache, aotAssemblyNames);

        var pgoRulePaths = (il2CppOptions.PgoRuleFiles ?? Enumerable.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => Path.GetFullPath(s.Trim()))
            .ToList();
        PgoMethodIncludeIndex pgoIncludeIndex = PgoMethodIncludeIndex.Load(pgoRulePaths);
        if (pgoIncludeIndex != null)
        {
            foreach (var ass in pgoIncludeIndex.RestrictedAssemblies)
                s_logger.Info("LeanAOT PGO include assembly {0}: {1} method(s)", ass, pgoIncludeIndex.GetIncludeCount(ass));
        }

        var manifestArgs = new ManifestArgs()
        {
            assemblyCache = assemblyCache,
            aotAssemblyNames = aotAssemblyNames,
            AotRulesEvaluator = aotRules,
            PgoIncludeIndex = pgoIncludeIndex,
            ShouldSkipUnsupportedRuntimeMethod = method => ShouldSkipUnsupportedRuntimeIntrinsic(method, runtimeApiCatalog),
            ShouldSkipRuntimeResolvedNativeMethod = method => ShouldSkipRuntimeResolvedNative(method, runtimeApiCatalog),
            Generics = il2CppOptions.NoGenerics ? null : new GenericPlannerArgs
            {
                ListFiles = (il2CppOptions.GenericListFiles ?? Enumerable.Empty<string>())
                    .Where(s => !string.IsNullOrWhiteSpace(s) && File.Exists(s.Trim()))
                    .Select(s => Path.GetFullPath(s.Trim()))
                    .ToList(),
                IsRuntimeImplemented = IsRuntimeImplementedGeneric,
                Exclude = (il2CppOptions.GenericExcludes ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrEmpty(s)).ToList(),
            },
        };
        var manifest = new Manifest(manifestArgs);

        var metaService = new MetadataService();
        var globalServices = new GlobalServices()
        {
            Config = new GlobalConfig(),
            ManifestService = new ManifestService(manifest),
            TypeNameService = new TypeNameService(metaService),
            InvokerService = new InvokerService(metaService),
            DirectCallBridgeService = new DirectCallBridgeService(metaService),
            MonoPInvokeCallbackService = new MonoPInvokeCallbackService(metaService),
            MetadataService = metaService,
            RuntimeApiCatalog = runtimeApiCatalog,
        };
        ApplyIl2CppOptionsToGlobalConfig(globalServices.Config, il2CppOptions);
        GlobalServices.Inst = globalServices;

        EnsureEmptyProfilerOutputFile(globalServices.Config);

        var confBuilder = new GenerationConfigBuilder()
        {
            outputCodeDir = outputCodeDir,
            manifest = manifest,
            dllSearchPaths = dllSearchPaths,
            assemblyCache = assemblyCache,
        };
        if (Directory.Exists(confBuilder.outputCodeDir))
        {
            Directory.Delete(confBuilder.outputCodeDir, true);
        }
        Directory.CreateDirectory(confBuilder.outputCodeDir);
        var conf = confBuilder.Build();
        generator.Generate(conf);

        Il2CppCompatibilityOutputs.EmitIfRequested(globalServices.Config, manifest, dllSearchPaths, aotAssemblyNames, metaService);
        WriteManifestReport(Path.Combine(confBuilder.outputCodeDir, "manifest-report.json"), manifest);
    }

    /// <summary>
    /// Generic methods the runtime implements itself (intrinsics and icalls registered with a "&lt;&gt;" name in
    /// src/runtime/intrinsics/*.cpp and src/runtime/icalls/*.cpp). Their IL bodies are placeholders or fallbacks, so an
    /// instantiation is never compiled from them; compiled callers reach them through the runtime.
    /// </summary>
    private static readonly HashSet<string> s_runtimeImplementedGenerics = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Activator::CreateInstance",
        "System.Array::GetGenericValueImpl",
        "System.Array::SetGenericValueImpl",
        "System.Double::ConvertToIntegerNative",
        "System.Single::ConvertToIntegerNative",
        "System.Runtime.CompilerServices.RuntimeHelpers::CreateSpan",
        "System.Runtime.CompilerServices.RuntimeHelpers::IsBitwiseEquatable",
        "System.Runtime.CompilerServices.RuntimeHelpers::IsReferenceOrContainsReferences",
        "System.Runtime.CompilerServices.Unsafe::Add",
        "System.Runtime.CompilerServices.Unsafe::AddByteOffset",
        "System.Runtime.CompilerServices.Unsafe::AreSame",
        "System.Runtime.CompilerServices.Unsafe::As",
        "System.Runtime.CompilerServices.Unsafe::AsPointer",
        "System.Runtime.CompilerServices.Unsafe::AsRef",
        "System.Runtime.CompilerServices.Unsafe::ByteOffset",
        "System.Runtime.CompilerServices.Unsafe::Copy",
        "System.Runtime.CompilerServices.Unsafe::IsAddressGreaterThan",
        "System.Runtime.CompilerServices.Unsafe::IsAddressLessThan",
        "System.Runtime.CompilerServices.Unsafe::ReadUnaligned",
        "System.Runtime.CompilerServices.Unsafe::SizeOf",
        "System.Runtime.CompilerServices.Unsafe::SkipInit",
        "System.Runtime.CompilerServices.Unsafe::Subtract",
        "System.Runtime.CompilerServices.Unsafe::SubtractByteOffset",
        "System.Runtime.CompilerServices.Unsafe::Unbox",
        "System.Runtime.CompilerServices.Unsafe::WriteUnaligned",
        "System.Runtime.InteropServices.MemoryMarshal::GetArrayDataReference",
        "System.Threading.Interlocked::CompareExchange",
        "System.Threading.Interlocked::Exchange",
        "System.Threading.Volatile::Read",
        "System.Threading.Volatile::Write",
    };

    /// <summary>Also everything under System.Runtime.Intrinsics: the runtime answers IsSupported with false for all of
    /// it (vm/intrinsics.cpp is_runtime_intrinsics_is_supported) where the IL would say true.
    /// Other [Intrinsic] generic methods are compiled from their IL, which is what the interpreter runs for them too.</summary>
    private static bool IsRuntimeImplementedGeneric(MethodDef method)
    {
        if (method.Module.Assembly.Name != LeanAOT.Core.GenericInstantiation.CoreLibName)
            return false;
        TypeDef root = method.DeclaringType;
        while (root.DeclaringType != null)
            root = root.DeclaringType;
        return root.Namespace.StartsWith("System.Runtime.Intrinsics", StringComparison.Ordinal) ||
            s_runtimeImplementedGenerics.Contains($"{method.DeclaringType.FullName}::{method.Name}");
    }

    /// <summary>--leanaot-generic-keys-to-names IN:OUT: runtime keys of instantiations (profile output, valid for the DLLs
    /// in the -d directories) to name keys that stay valid when the DLLs are rebuilt.</summary>
    private static int ConvertGenericKeys(CliOptions options)
    {
        string spec = options.GenericKeysToNames.Trim();
        int sep = spec.LastIndexOf(':');
        if (sep <= 0)
        {
            s_logger.Error("--leanaot-generic-keys-to-names expects IN:OUT");
            return 1;
        }
        var dirs = (options.DllSearchPaths ?? Enumerable.Empty<string>()).Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => Path.GetFullPath(d.Trim())).ToList();
        var cache = new Core.AssemblyCache(new Core.MultiDirectoryAssemblyResolver(dirs));
        var names = new SortedSet<string>(StringComparer.Ordinal);
        int failed = 0;
        foreach (string raw in File.ReadAllLines(spec.Substring(0, sep)))
        {
            string key = raw.Trim();
            if (key.Length == 0 || key[0] == '#')
                continue;
            LeanAOT.Core.GenericInstantiation gi = null;
            try
            {
                gi = LeanAOT.Core.GenericInstantiation.ParseRidKey(key, cache);
            }
            catch (Exception)
            {
            }
            if (gi == null)
            {
                failed++;
                continue;
            }
            names.Add(gi.NameKey);
        }
        File.WriteAllLines(spec.Substring(sep + 1), names);
        Console.WriteLine($"generic keys: {names.Count} written, {failed} not resolved");
        return 0;
    }

    /// <summary>Which methods were compiled and why the others stay interpreted, per assembly (for coverage reports).</summary>
    private static void WriteManifestReport(string path, Manifest manifest)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        bool firstAss = true;
        if (manifest.Generics != null)
        {
            var plans = manifest.GenericMethodPlans.ToList();
            sb.Append("  \"(generics)\": {\"aot\": ").Append(plans.Count);
            sb.Append(", \"origins\": {").Append(string.Join(", ", plans.GroupBy(p => p.Origin).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{JsonString(g.Key)}: {g.Count()}"))).Append('}');
            sb.Append(", \"homes\": {").Append(string.Join(", ", plans.GroupBy(p => p.Home).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{JsonString(g.Key)}: {g.Count()}"))).Append('}');
            sb.Append(", \"list\": {\"entries\": ").Append(manifest.Generics.ListEntries).Append(", \"missing\": ").Append(manifest.Generics.ListEntriesMissing).Append('}');
            sb.Append(", \"skipped\": {");
            bool firstReason = true;
            foreach (var (reason, methods) in manifest.Generics.Skipped)
            {
                sb.Append(firstReason ? "\n" : ",\n").Append("    ").Append(JsonString(reason)).Append(": [");
                firstReason = false;
                sb.Append(string.Join(", ", methods.Select(JsonString)));
                sb.Append(']');
            }
            sb.Append(firstReason ? "}}" : "\n  }}");
            firstAss = false;
        }
        foreach (var (assName, plan) in manifest.AssemblyPlans.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(firstAss ? "" : ",\n").Append("  ").Append(JsonString(assName)).Append(": {\"aot\": ").Append(plan.MethodPlans.Count).Append(", \"skipped\": {");
            firstAss = false;
            bool firstReason = true;
            if (manifest.SkippedMethods.TryGetValue(assName, out var byReason))
            {
                foreach (var (reason, methods) in byReason)
                {
                    sb.Append(firstReason ? "\n" : ",\n").Append("    ").Append(JsonString(reason)).Append(": [");
                    firstReason = false;
                    sb.Append(string.Join(", ", methods.Select(JsonString)));
                    sb.Append(']');
                }
            }
            sb.Append(firstReason ? "}}" : "\n  }}");
        }
        sb.Append("\n}\n");
        File.WriteAllText(path, sb.ToString());
    }

    private static string JsonString(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"' || c == '\\')
                sb.Append('\\').Append(c);
            else if (c < 0x20)
                sb.Append($"\\u{(int)c:x4}");
            else
                sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    private static bool ShouldSkipUnsupportedRuntimeIntrinsic(MethodDef method, RuntimeApiCatalog runtimeApiCatalog)
    {
        if (method == null || runtimeApiCatalog == null || !runtimeApiCatalog.IsCoreLibraryModule(method.Module))
        {
            return false;
        }

        bool isRuntimeIntrinsicsType = method.DeclaringType.FullName.StartsWith("System.Runtime.Intrinsics.", StringComparison.Ordinal) ||
            method.DeclaringType.FullName == "System.Runtime.Intrinsics";
        bool isMarkedIntrinsic = method.CustomAttributes.Any(IsIntrinsicAttribute) ||
            method.DeclaringType.CustomAttributes.Any(IsIntrinsicAttribute);
        if (!isRuntimeIntrinsicsType && !isMarkedIntrinsic)
        {
            return false;
        }

        if (runtimeApiCatalog.TryGetIcallOrIntrinsic(method, out _, out _))
        {
            return false;
        }

        return !method.IsConstructor || !runtimeApiCatalog.TryGetIcallOrIntrinsicNewobj(method, out _, out _);
    }

    // An icall without a runtime API catalog entry (icalls.json) would get a wrapper that looks the function up by name
    // and calls it with a C signature derived from the managed one, but this runtime registers icall functions as
    // RtResult-returning C++ functions: in wasm that is a call_indirect signature trap (Monitor.Enter ->
    // TryEnter_FastPath). A P/Invoke wrapper calls a native function pointer, but most QCalls here are registered only
    // as invokers (no function pointer), so the wrapper, which the shim prefers over the P/Invoke invoker, would throw
    // EntryPointNotFound where the interpreter succeeds. Both stay with the runtime's invokers; AOT code reaches them
    // through a direct-call bridge like any interpreted method.
    private static bool ShouldSkipRuntimeResolvedNative(MethodDef method, RuntimeApiCatalog runtimeApiCatalog)
    {
        if (method.IsPinvokeImpl)
            return true;
        return method.IsInternalCall && !runtimeApiCatalog.TryGetIcallOrIntrinsic(method, out _, out _);
    }

    private static bool IsIntrinsicAttribute(CustomAttribute attribute)
    {
        return attribute.TypeFullName == "System.Runtime.CompilerServices.IntrinsicAttribute" ||
            attribute.AttributeType.Name == "IntrinsicAttribute";
    }
}
