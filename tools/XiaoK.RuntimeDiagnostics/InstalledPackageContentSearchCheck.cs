using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

internal static class InstalledPackageContentSearchCheck
{
    private const string PackageName = "MingKaiLin.XiaoK";
    private const string PackagePublisher = "CN=XiaoK Local Development";
    private const string AdapterAssemblyName = "XiaoK.Adapters.Windows.dll";
    private const string CoreAssemblyName = "XiaoK.Core.dll";

    public static async Task<int> RunAsync(string packageDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("已安装包文件搜索检查只支持 Windows。");
            return 2;
        }

        var packageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectory));
        if (!Directory.Exists(packageRoot) || !IsExpectedInstalledPackage(packageRoot))
        {
            Console.Error.WriteLine("目标不是已安装的小K MSIX包目录；没有加载或执行程序集。");
            return 2;
        }

        var adapterPath = Path.Combine(packageRoot, AdapterAssemblyName);
        var corePath = Path.Combine(packageRoot, CoreAssemblyName);
        var manifestPath = Path.Combine(packageRoot, "AppxManifest.xml");
        if (!File.Exists(adapterPath) || !File.Exists(corePath) || !File.Exists(manifestPath)
            || !ManifestMatches(manifestPath))
        {
            Console.Error.WriteLine("安装包身份或所需程序集不符合预期；未执行检查。");
            return 2;
        }

        var installedAdapterHash = await HashFileAsync(adapterPath);
        var installedCoreHash = await HashFileAsync(corePath);
        var matchingLayout = await FindMatchingValidationLayoutAsync(manifestPath,
            installedAdapterHash, installedCoreHash);
        if (matchingLayout is null)
        {
            Console.Error.WriteLine("找不到身份、版本和适配器/Core散列均与已安装包一致的本地验证包副本；未执行检查。");
            return 1;
        }

        var fixtureRoot = CreateFixtureRoot();
        try
        {
            CreateSyntheticFiles(fixtureRoot);
            var context = new InstalledPackageLoadContext(matchingLayout);
            var adapterAssembly = context.LoadFromAssemblyPath(Path.Combine(matchingLayout, AdapterAssemblyName));
            var coreAssembly = context.LoadFromAssemblyPath(Path.Combine(matchingLayout, CoreAssemblyName));
            Console.WriteLine($"装载验证包程序集：适配器={adapterAssembly.FullName}，Core={coreAssembly.FullName}");
            var tools = CreateWindowsDesktopTools(adapterAssembly, fixtureRoot);
            var resultOne = await InvokeSearchAsync(adapterAssembly, coreAssembly, tools,
                "Needle", "MatchingFileContentLocationsListed");
            var dataOne = ReadResultProperty(resultOne, "Data") ?? string.Empty;
            var successOne = ReadResultProperty(resultOne, "Success") == "True";
            var notesPath = Path.Combine(fixtureRoot, "allowed", "notes.txt");
            var passedOne = successOne
                && dataOne.Contains(notesPath, StringComparison.OrdinalIgnoreCase)
                && dataOne.Contains("第1、3行", StringComparison.Ordinal)
                && !dataOne.Contains("Needle", StringComparison.OrdinalIgnoreCase)
                && !dataOne.Contains("PRIVATE_SENTINEL", StringComparison.Ordinal)
                && !dataOne.Contains("outside.txt", StringComparison.Ordinal)
                && !dataOne.Contains("excluded.env", StringComparison.Ordinal)
                && !dataOne.Contains("binary.png", StringComparison.Ordinal);

            var resultTwo = await InvokeSearchAsync(adapterAssembly, coreAssembly, tools,
                "竹子", "MatchingFileContentLocationsListed");
            var dataTwo = ReadResultProperty(resultTwo, "Data") ?? string.Empty;
            var successTwo = ReadResultProperty(resultTwo, "Success") == "True";
            var utf16Path = Path.Combine(fixtureRoot, "allowed", "utf16.txt");
            var passedTwo = successTwo
                && dataTwo.Contains(utf16Path, StringComparison.OrdinalIgnoreCase)
                && dataTwo.Contains("第1行", StringComparison.Ordinal)
                && !dataTwo.Contains("竹子", StringComparison.Ordinal)
                && !dataTwo.Contains("PRIVATE_SENTINEL", StringComparison.Ordinal);

            var passed = passedOne && passedTwo;
            Console.WriteLine($"MSIX包版本：{ReadPackageVersion(manifestPath)}");
            Console.WriteLine($"已安装/本地Windows文件适配器SHA-256：{installedAdapterHash}");
            Console.WriteLine($"已安装/本地Core程序集SHA-256：{installedCoreHash}");
            Console.WriteLine("程序集对照：身份和版本相符的仓库验证包副本与已安装包逐字节相同。");
            Console.WriteLine("加载来源：仓库验证包副本；未从WindowsApps受保护安装目录加载代码。");
            Console.WriteLine("合成文件检查：" + (passedOne ? "通过（UTF-8、多行位置、范围与正文不泄露）" : "失败"));
            Console.WriteLine("UTF-16检查：" + (passedTwo ? "通过（只返回路径与行号）" : "失败"));
            Console.WriteLine(passed
                ? "通过：检查调用与已安装包适配器/Core逐字节相同的本地验证副本，只使用本工具生成的合成文件。"
                : "未通过：输出只提供固定摘要，不打印搜索文件正文。普通用户界面与真实目录未操作。");
            context.Unload();
            return passed ? 0 : 1;
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot);
        }
    }

    private static object CreateWindowsDesktopTools(Assembly adapterAssembly, string fixtureRoot)
    {
        var desktopAppType = adapterAssembly.GetType("XiaoK.Adapters.Windows.DesktopApp", throwOnError: true)!;
        var windowsDesktopToolsType = adapterAssembly.GetType("XiaoK.Adapters.Windows.WindowsDesktopTools", throwOnError: true)!;
        var appCollection = Array.CreateInstance(desktopAppType, 0);
        var roots = new[] { new KeyValuePair<string, string>("user-files", Path.Combine(fixtureRoot, "allowed")) };
        var constructor = windowsDesktopToolsType.GetConstructors()
            .SingleOrDefault(candidate => candidate.GetParameters().Length == 6)
            ?? throw new MissingMethodException("安装版WindowsDesktopTools构造签名发生变化。");
        return constructor.Invoke([appCollection, roots, null, null, null, null]);
    }

    private static async Task<object> InvokeSearchAsync(Assembly adapterAssembly, Assembly coreAssembly,
        object tools, string query, string expectedOutcome)
    {
        var proposalType = coreAssembly.GetType("XiaoK.Core.ToolProposal");
        if (proposalType is null)
            throw new MissingMemberException("已安装包Core程序集缺少ToolProposal契约。");
        var proposalConstructor = proposalType.GetConstructors().Single();
        var parameters = proposalConstructor.GetParameters();
        var arguments = ImmutableDictionary.Create<string, string>(StringComparer.Ordinal)
            .Add("query", query)
            .Add("root_id", "user-files");
        var preconditionType = coreAssembly.GetType("XiaoK.Core.ToolPrecondition", throwOnError: true)!;
        var outcomeType = coreAssembly.GetType("XiaoK.Core.ToolExpectedOutcome", throwOnError: true)!;
        var preconditions = Enum.Parse(preconditionType, "ConfiguredSearchRoot");
        var outcome = Enum.Parse(outcomeType, expectedOutcome);
        var proposal = proposalConstructor.Invoke(["file.search.content.v1", arguments, "user-files", preconditions, outcome]);
        var method = adapterAssembly.GetType("XiaoK.Adapters.Windows.WindowsDesktopTools", throwOnError: true)!
            .GetMethod("SearchFileContentsAsync", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMethodException("安装版内容搜索入口不存在。");
        var task = method.Invoke(tools, [proposal, CancellationToken.None]) as Task
            ?? throw new InvalidOperationException("安装版搜索入口没有返回Task。");
        await task;
        return task.GetType().GetProperty("Result")?.GetValue(task)
            ?? throw new InvalidOperationException("安装版搜索结果为空。");
    }

    private static string? ReadResultProperty(object result, string name) =>
        result.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(result)?.ToString();

    private static async Task<string> HashFileAsync(string path) =>
        Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));

    private static async Task<string?> FindMatchingValidationLayoutAsync(string manifestPath,
        string installedAdapterHash, string installedCoreHash)
    {
        var repositoryRoot = FindRepositoryRoot();
        if (repositoryRoot is null) return null;
        var validationRoot = Path.Combine(repositoryRoot, "artifacts", "msix-validation");
        if (!Directory.Exists(validationRoot) || (File.GetAttributes(validationRoot) & FileAttributes.ReparsePoint) != 0)
            return null;

        var installedVersion = ReadPackageVersion(manifestPath);
        var matches = new List<string>();
        var identityVersionMatches = 0;
        foreach (var packageDirectory in Directory.EnumerateDirectories(validationRoot))
        {
            var layout = Path.Combine(packageDirectory, "layout");
            var candidateManifest = Path.Combine(layout, "AppxManifest.xml");
            var adapter = Path.Combine(layout, AdapterAssemblyName);
            var core = Path.Combine(layout, CoreAssemblyName);
            if ((File.GetAttributes(packageDirectory) & FileAttributes.ReparsePoint) != 0
                || !Directory.Exists(layout) || !File.Exists(candidateManifest) || !File.Exists(adapter) || !File.Exists(core)
                || (File.GetAttributes(layout) & FileAttributes.ReparsePoint) != 0
                || (File.GetAttributes(adapter) & FileAttributes.ReparsePoint) != 0
                || (File.GetAttributes(core) & FileAttributes.ReparsePoint) != 0
                || !ManifestMatches(candidateManifest)
                || !string.Equals(ReadPackageVersion(candidateManifest), installedVersion, StringComparison.Ordinal))
                continue;

            identityVersionMatches++;

            if (string.Equals(await HashFileAsync(adapter), installedAdapterHash, StringComparison.Ordinal)
                && string.Equals(await HashFileAsync(core), installedCoreHash, StringComparison.Ordinal))
                matches.Add(layout);
        }

        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"本地验证包候选统计：目录{Directory.EnumerateDirectories(validationRoot).Count()}个，身份/版本匹配{identityVersionMatches}个，程序集散列匹配{matches.Count}个。");
            return null;
        }

        return matches.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).First();
    }

    private static string? FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "XiaoK.sln")))
                return directory.FullName;
        return null;
    }

    private static bool IsExpectedInstalledPackage(string packageRoot)
    {
        var windowsAppsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        var fullWindowsAppsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(windowsAppsRoot));
        var fullPackageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
        var parent = Directory.GetParent(fullPackageRoot)?.FullName;
        var leaf = Path.GetFileName(fullPackageRoot);
        return string.Equals(parent, fullWindowsAppsRoot, StringComparison.OrdinalIgnoreCase)
            && leaf.StartsWith(PackageName + "_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ManifestMatches(string path)
    {
        try
        {
            XNamespace identityNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
            var identity = XDocument.Load(path).Root?.Element(identityNamespace + "Identity");
            return identity is not null
                && identity.Attribute("Name")?.Value == PackageName
                && identity.Attribute("Publisher")?.Value == PackagePublisher;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return false;
        }
    }

    private static string ReadPackageVersion(string path)
    {
        XNamespace identityNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        return XDocument.Load(path).Root?.Element(identityNamespace + "Identity")?.Attribute("Version")?.Value ?? "unknown";
    }

    private static string CreateFixtureRoot()
    {
        var baseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var root = Path.Combine(baseDirectory, "installed-content-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fullRoot = Path.GetFullPath(root);
        if (!fullRoot.StartsWith(baseDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("合成检查目录没有位于工具输出目录中。");
        return fullRoot;
    }

    private static void CreateSyntheticFiles(string root)
    {
        var allowed = Path.Combine(root, "allowed");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(allowed);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(allowed, "notes.txt"),
            "Needle alpha PRIVATE_SENTINEL\nsecond line\nNeedle omega PRIVATE_SENTINEL", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(allowed, "excluded.env"), "Needle PRIVATE_SENTINEL", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(allowed, "utf16.txt"), "竹子\n普通行", Encoding.Unicode);
        File.WriteAllBytes(Path.Combine(allowed, "binary.png"), [0, 1, 2, 3, 4, 5]);
        File.WriteAllText(Path.Combine(outside, "outside.txt"), "Needle PRIVATE_SENTINEL", new UTF8Encoding(false));
    }

    private static void DeleteFixtureRoot(string root)
    {
        var baseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!fullRoot.StartsWith(baseDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullRoot).StartsWith("installed-content-search-", StringComparison.Ordinal)
            || !Directory.Exists(fullRoot)
            || (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("拒绝清理不属于本次检查的目录。");

        foreach (var entry in Directory.EnumerateFileSystemEntries(fullRoot, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("合成检查目录包含重解析点；停止清理。");
        Directory.Delete(fullRoot, recursive: true);
    }

    private sealed class InstalledPackageLoadContext(string packageRoot) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly string _packageRoot = packageRoot;

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.IsNullOrWhiteSpace(assemblyName.Name)
                || !assemblyName.Name.StartsWith("XiaoK.", StringComparison.Ordinal)) return null;
            var candidate = Path.Combine(_packageRoot, assemblyName.Name + ".dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }
}
