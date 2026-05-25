using System;
using System.Reflection;
using SkimDownForWindows.Application.Abstractions;
using Windows.ApplicationModel;

namespace SkimDownForWindows.Infrastructure.Windows;

/// <summary>
/// パッケージ実行時は <see cref="Package.Current"/> から、非パッケージ実行時は実行アセンブリの
/// メタデータからアプリ情報を取得する <see cref="IAppInfoService"/> 既定実装。
/// </summary>
/// <remarks>
/// 値はインスタンス生成時に 1 度だけ取得して保持する (プロセスライフタイムで不変なため)。
/// 取得失敗時はスローせずに安全な既定値にフォールバックする。
/// </remarks>
public sealed class PackageAppInfoService : IAppInfoService
{
    // パッケージ識別が無い (非パッケージ実行) 場合の HRESULT。Package.Current の取得時に投げられる。
    private const int APPMODEL_ERROR_NO_PACKAGE = unchecked((int)0x80073D54);

    // 非パッケージ実行時 / 取得失敗時のフォールバック値。
    private const string FallbackDisplayName = "SkimDown";
    private const string FallbackDescription = "A quiet, read-only Markdown folder reader.";
    private const string FallbackCopyright = "© okazuki";

    public string DisplayName { get; }
    public string Version { get; }
    public string Description { get; }
    public string Copyright { get; }

    public PackageAppInfoService()
    {
        var pkg = TryGetCurrentPackage();
        if (pkg is not null)
        {
            DisplayName = SafeOr(() => pkg.DisplayName, FallbackDisplayName);
            Version = FormatPackageVersion(pkg.Id.Version);
            Description = SafeOr(() => pkg.Description, FallbackDescription);
        }
        else
        {
            DisplayName = FallbackDisplayName;
            Version = ReadAssemblyVersion();
            Description = FallbackDescription;
        }

        Copyright = ReadCopyright();
    }

    private static Package? TryGetCurrentPackage()
    {
        try
        {
            return Package.Current;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == APPMODEL_ERROR_NO_PACKAGE)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatPackageVersion(PackageVersion v)
    {
        // ストア互換のため Revision は常に 0 で固定運用 (README "Bumping the version" 参照)。
        // ユーザー向け表示は Major.Minor.Build に短縮する。
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private static string ReadAssemblyVersion()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                // "1.0.1+abcdef0" のような SemVer 拡張から build metadata を除去。
                var plus = info.IndexOf('+');
                return plus >= 0 ? info.Substring(0, plus) : info;
            }

            var file = asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            if (!string.IsNullOrWhiteSpace(file))
            {
                return file;
            }

            var ver = asm.GetName().Version;
            return ver is null ? "unknown" : $"{ver.Major}.{ver.Minor}.{ver.Build}";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string ReadCopyright()
    {
        try
        {
            var copyright = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;
            return string.IsNullOrWhiteSpace(copyright) ? FallbackCopyright : copyright;
        }
        catch
        {
            return FallbackCopyright;
        }
    }

    private static string SafeOr(Func<string?> read, string fallback)
    {
        try
        {
            var v = read();
            return string.IsNullOrWhiteSpace(v) ? fallback : v;
        }
        catch
        {
            return fallback;
        }
    }
}
